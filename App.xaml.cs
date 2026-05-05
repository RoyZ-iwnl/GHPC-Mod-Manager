using GHPC_Mod_Manager.Services;
using GHPC_Mod_Manager.ViewModels;
using GHPC_Mod_Manager.Views;
using GHPC_Mod_Manager.Models;
using GHPC_Mod_Manager.Resources;
using GHPC_Mod_Manager.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using System.Runtime.InteropServices;
using System.Threading;

namespace GHPC_Mod_Manager
{
    public partial class App : Application
    {
        private IHost? _host;
        private static Mutex? _mutex;
        private const string MutexName = "GHPC_Mod_Manager_SingleInstance_Mutex";

        // 待处理的协议激活参数
        private static string[]? _pendingProtocolArgs;

        // Windows API 用于激活已运行的实例
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private const int SW_RESTORE = 9;

        protected override async void OnStartup(StartupEventArgs e)
        {
            // 单实例检测
            bool createdNew;
            _mutex = new Mutex(true, MutexName, out createdNew);

            // 如果命令行有参数，说明可能是协议激活
            bool hasProtocolArgs = e.Args.Length > 0 && e.Args.Any(arg => arg.StartsWith("ghpcmm://", StringComparison.OrdinalIgnoreCase));

            if (!createdNew)
            {
                // 已有实例在运行
                Task<bool>? protocolSendTask = null;
                if (hasProtocolArgs)
                {
                    var protocolArg = e.Args.FirstOrDefault(arg => arg.StartsWith("ghpcmm://", StringComparison.OrdinalIgnoreCase));
                    if (protocolArg != null)
                    {
                        protocolSendTask = ProtocolIpcClient.SendAsync(protocolArg);
                    }
                }

                // 找到并激活已运行的实例窗口
                IntPtr hwnd = FindMainWindow();
                if (hwnd != IntPtr.Zero)
                {
                    ShowWindow(hwnd, SW_RESTORE);
                    SetForegroundWindow(hwnd);
                }

                if (protocolSendTask != null)
                {
                    await protocolSendTask;
                }

                _mutex?.Dispose();
                _mutex = null;
                Shutdown();
                return;
            }

            // 解析命令行参数
            CommandLineArgs.Parse(e.Args);

            // 保存协议参数（在服务初始化完成后处理）
            if (hasProtocolArgs)
            {
                _pendingProtocolArgs = e.Args;
            }

            _host = Host.CreateDefaultBuilder()
                .ConfigureServices((context, services) =>
                {
                    // Services
                    services.AddSingleton<ILoggingService, LoggingService>();
                    services.AddSingleton<ISecureStorageService, SecureStorageService>();
                    services.AddSingleton<ISettingsService, SettingsService>();
                    services.AddSingleton<IMainConfigService, MainConfigService>();
                    services.AddSingleton<IThemeService, ThemeService>();
                    services.AddSingleton<INavigationService, NavigationService>();
                    services.AddSingleton<IProcessService, ProcessService>();
                    services.AddSingleton<IMelonLoaderService, MelonLoaderService>();
                    services.AddSingleton<IModManagerService, ModManagerService>();
                    services.AddSingleton<ITranslationManagerService, TranslationManagerService>();
                    services.AddSingleton<IModI18nService, ModI18nService>();
                    services.AddSingleton<IModBackupService, ModBackupService>();
                    services.AddSingleton<IAnnouncementService, AnnouncementService>();
                    services.AddSingleton<IUpdateService, UpdateService>();
                    services.AddSingleton<ISteamGameFinderService, SteamGameFinderService>();
                    services.AddSingleton<IVersionCleanupService, VersionCleanupService>();
                    services.AddSingleton<IDialogService, DialogService>();
                    services.AddSingleton<IPreviousInstallationService, PreviousInstallationService>();
                    services.AddSingleton<IModCatalogStateService, ModCatalogStateService>();
                    services.AddSingleton<IProtocolActivationService, ProtocolActivationService>();
                    services.AddSingleton<IProtocolIpcServer, NamedPipeProtocolIpcServer>();
                    services.ConfigureHttpClientDefaults(builder =>
                    {
                        builder.ConfigureHttpClient(client =>
                        {
                            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/139.0.0.0 Safari/537.36 Edg/139.0.0.0");
                            client.Timeout = TimeSpan.FromMinutes(10);
                        });

                        builder.ConfigurePrimaryHttpMessageHandler(serviceProvider =>
                        {
                            var settingsService = serviceProvider.GetRequiredService<ISettingsService>();
                            var loggingService = serviceProvider.GetRequiredService<ILoggingService>();

                            var handler = new SocketsHttpHandler
                            {
                                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                                ConnectTimeout = TimeSpan.FromSeconds(15),
                                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                                SslOptions = new SslClientAuthenticationOptions
                                {
                                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                                    RemoteCertificateValidationCallback = (_, _, chain, sslPolicyErrors) =>
                                        ValidateServerCertificate(chain, sslPolicyErrors)
                                }
                            };

                            handler.ConnectCallback = (context, cancellationToken) =>
                                DnsOverHttpsConnector.ConnectAsync(
                                    context,
                                    settingsService.Settings.UseDnsOverHttps &&
                                    string.Equals(settingsService.Settings.Language, "zh-CN", StringComparison.OrdinalIgnoreCase),
                                    loggingService,
                                    cancellationToken);

                            return handler;
                        });
                    });

                    services.AddHttpClient<INetworkService, NetworkService>();

                    // ViewModels
                    services.AddTransient<MainWindowViewModel>();
                    services.AddTransient<SetupWizardViewModel>();
                    services.AddSingleton<MainViewModel>(); // 改为单例，避免导航时重新创建
                    services.AddSingleton<Lazy<MainViewModel>>(sp => new Lazy<MainViewModel>(sp.GetRequiredService<MainViewModel>));
                    services.AddTransient<SettingsViewModel>();
                    services.AddTransient<ModConfigurationViewModel>();
                    services.AddTransient<AnnouncementViewModel>();
                    services.AddTransient<ModInfoDumperViewModel>();
                    // 子页面ViewModel（单例，数据共享）
                    services.AddSingleton<InstalledModsViewModel>();
                    services.AddSingleton<ModBrowserViewModel>();
                    services.AddSingleton<ModDetailViewModel>();

                    // Views
                    services.AddTransient<MainWindow>();
                    services.AddTransient<SetupWizardView>();
                    services.AddSingleton<MainView>(); // 改为单例，避免导航时重新创建
                    services.AddTransient<SettingsView>();
                    services.AddTransient<ModInfoDumperWindow>();
                    // 子页面View（Transient，每次导航到详情页创建新实例）
                    services.AddSingleton<InstalledModsView>();
                    services.AddSingleton<ModBrowserView>();
                    services.AddSingleton<TranslationView>();
                    services.AddTransient<ModDetailView>();
                })
                .ConfigureLogging(logging =>
                {
                    logging.AddConsole();
                    logging.AddDebug();
                })
                .Build();

            await _host.StartAsync();

            // 启动协议IPC服务端
            var protocolIpcServer = _host.Services.GetRequiredService<IProtocolIpcServer>();
            protocolIpcServer.Start();

            var settingsService = _host.Services.GetRequiredService<ISettingsService>();
            await settingsService.LoadSettingsAsync();
            // Ensure language setting is applied immediately on startup
            settingsService.ApplyLanguageSetting();
            var startupLoggingService = _host.Services.GetRequiredService<ILoggingService>();
            startupLoggingService.LogInfo(
                "DoH startup state: enabled={0}, language={1}",
                settingsService.Settings.UseDnsOverHttps,
                settingsService.Settings.Language);

            // 设置 -dev 参数，必须在 mainConfigService.LoadAsync() 之前
            if (CommandLineArgs.DevModeEnabled)
            {
                startupLoggingService.LogInfo("Dev mode enabled via -dev argument");
                if (!string.IsNullOrWhiteSpace(CommandLineArgs.DevConfigUrlOverride))
                {
                    startupLoggingService.LogInfo($"Dev config path: {CommandLineArgs.DevConfigUrlOverride}");
                }
            }

            // 首次运行时跳过主配置加载，在 SetupWizard 步骤2手动触发
            var mainConfigService = _host.Services.GetRequiredService<IMainConfigService>();
            if (!settingsService.Settings.IsFirstRun)
            {
                await mainConfigService.LoadAsync();
            }

            // 初始化主题系统
            var themeService = _host.Services.GetRequiredService<IThemeService>();
            var loggingService = _host.Services.GetRequiredService<ILoggingService>();
            
            loggingService.LogInfo(Strings.AppStartupSettingsTheme, settingsService.Settings.Theme);
            loggingService.LogInfo(Strings.AppStartupResourceDictionaryCount, Application.Current.Resources.MergedDictionaries.Count);

            // 验证终末地主题解锁状态
            if (settingsService.Settings.Theme == AppTheme.Endfield && settingsService.Settings.IsEndfieldThemeUnlocked != true)
            {
                loggingService.LogWarning(Strings.EndfieldThemeLockedReset);
                settingsService.Settings.Theme = AppTheme.Light;
                await settingsService.SaveSettingsAsync();
            }

            // 由于App.xaml已经加载了Light主题，我们需要确保状态正确同步
            if (settingsService.Settings.Theme == AppTheme.Light)
            {
                // 如果设置是Light，需要同步ThemeService的内部状态但不需要重新加载资源
                loggingService.LogInfo(Strings.SettingsLightThemeConsistent);
                themeService.InitializeWithCurrentState(AppTheme.Light);
            }
            else
            {
                // 如果设置是其他主题（Dark/Endfield），需要切换主题并同步内部状态
                loggingService.LogInfo(Strings.SettingsDarkThemeNeedSwitch);
                themeService.SetTheme(settingsService.Settings.Theme);
            }
            
            loggingService.LogInfo(Strings.ThemeInitializationComplete, Application.Current.Resources.MergedDictionaries.Count);

            var processService = _host.Services.GetRequiredService<IProcessService>();
            await processService.StartMonitoringAsync();

            // 检查并执行旧版本文件清理（后台运行，不阻断启动）
            // 首次运行时跳过，避免触发 GitHub API
            if (!settingsService.Settings.IsFirstRun)
            {
                var versionCleanupService = _host.Services.GetRequiredService<IVersionCleanupService>();
                _ = Task.Run(async () => await versionCleanupService.RunIfNeededAsync());
            }

            // Show log window if -log parameter is provided
            if (CommandLineArgs.ShowLogWindow)
            {
                var logWindow = new LogWindow();
                logWindow.Show();
            }

            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            MainWindow = mainWindow;
            mainWindow.Show();

            // TODO: 启用时取消注释 - 启动时打开官网
            // if (settingsService.Settings.OpenWebsiteOnStartup)
            // {
            //     try
            //     {
            //         Process.Start(new ProcessStartInfo
            //         {
            //             FileName = "https://ghpcmm.link/",
            //             UseShellExecute = true
            //         });
            //     }
            //     catch { }
            // }

            // 注册自定义URL协议（首次运行时）
            RegisterUrlProtocol();

            // 处理协议激活（在主窗口显示后执行）
            if (_pendingProtocolArgs != null)
            {
                var protocolArg = _pendingProtocolArgs.FirstOrDefault(arg => arg.StartsWith("ghpcmm://", StringComparison.OrdinalIgnoreCase));
                if (protocolArg != null)
                {
                    var protocolActivationService = _host.Services.GetRequiredService<IProtocolActivationService>();
                    await protocolActivationService.HandleAsync(protocolArg);
                }
                _pendingProtocolArgs = null;
            }

            // 非首次运行时保存当前程序路径到注册表
            if (!settingsService.Settings.IsFirstRun)
            {
                var prevService = _host.Services.GetRequiredService<IPreviousInstallationService>();
                prevService.SaveCurrentAppPath();
            }

            base.OnStartup(e);
        }

        protected override async void OnExit(ExitEventArgs e)
        {
            if (_host != null)
            {
                try
                {
                    var processService = _host.Services.GetService<IProcessService>();
                    processService?.StopMonitoring();

                    // Stop any ongoing network operations
                    var networkService = _host.Services.GetService<INetworkService>();
                    if (networkService is IDisposable disposableNetwork)
                    {
                        disposableNetwork.Dispose();
                    }

                    // Stop mod manager service
                    var modManagerService = _host.Services.GetService<IModManagerService>();
                    if (modManagerService is IDisposable disposableModManager)
                    {
                        disposableModManager.Dispose();
                    }

                    // Stop translation service
                    var translationService = _host.Services.GetService<ITranslationManagerService>();
                    if (translationService is IDisposable disposableTranslation)
                    {
                        disposableTranslation.Dispose();
                    }

                    // Stop protocol IPC server
                    var protocolIpcServer = _host.Services.GetService<IProtocolIpcServer>();
                    protocolIpcServer?.Dispose();

                    await _host.StopAsync(TimeSpan.FromSeconds(5));
                    _host.Dispose();
                }
                catch
                {
                    // 忽略关闭时的错误，不阻止退出
                }
            }

            // 释放 Mutex（必须在 Environment.Exit 之前）
            _mutex?.ReleaseMutex();
            _mutex?.Dispose();
            _mutex = null;

            // Force exit if needed
            System.Environment.Exit(0);
        }

        public static T GetService<T>() where T : class
        {
            return ((App)Current)._host?.Services.GetService<T>()
                ?? throw new InvalidOperationException($"Service {typeof(T).Name} not found");
        }

        /// <summary>
        /// 释放单实例Mutex并启动旧版本应用，然后关闭当前实例
        /// </summary>
        /// <param name="previousExePath">旧版本exe的完整路径</param>
        public static void ReleaseMutexAndLaunchPrevious(string previousExePath)
        {
            // 必须先释放Mutex，否则旧exe启动后会检测到Mutex已存在而立即退出
            _mutex?.ReleaseMutex();
            _mutex?.Dispose();
            _mutex = null;

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = previousExePath,
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(previousExePath)
                });
            }
            catch
            {
                // 启动失败时保留当前实例，不继续退出
                return;
            }

            // 启动成功后关闭当前实例
            Current.Shutdown();
            System.Environment.Exit(0);
        }

        private static bool ValidateServerCertificate(X509Chain? chain, SslPolicyErrors sslPolicyErrors)
        {
            if (sslPolicyErrors == SslPolicyErrors.None)
                return true;

            if (sslPolicyErrors == SslPolicyErrors.RemoteCertificateChainErrors && chain != null)
            {
                foreach (var status in chain.ChainStatus)
                {
                    if (status.Status == X509ChainStatusFlags.RevocationStatusUnknown ||
                        status.Status == X509ChainStatusFlags.OfflineRevocation)
                    {
                        continue;
                    }

                    if (status.Status != X509ChainStatusFlags.NoError)
                        return false;
                }

                return true;
            }

            return false;
        }

        // 查找主窗口句柄
        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        private IntPtr FindMainWindow()
        {
            IntPtr result = IntPtr.Zero;

            EnumWindows((hWnd, lParam) =>
            {
                int length = GetWindowTextLength(hWnd);
                if (length == 0) return true;

                var sb = new System.Text.StringBuilder(length + 1);
                GetWindowText(hWnd, sb, sb.Capacity);

                string title = sb.ToString();
                // 查找 GHPC Mod Manager 主窗口
                if (title.Contains("GHPC Mod Manager") && IsWindowVisible(hWnd))
                {
                    result = hWnd;
                    return false; // 停止枚举
                }
                return true;
            }, IntPtr.Zero);

            return result;
        }

        // 注册自定义URL协议到注册表
        private void RegisterUrlProtocol()
        {
            try
            {
                var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(exePath)) return;

                using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Classes\ghpcmm"))
                {
                    key?.SetValue("", "URL:GHPC Mod Manager Protocol");
                    key?.SetValue("URL Protocol", "");
                }

                using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Classes\ghpcmm\shell\open\command"))
                {
                    key?.SetValue("", $"\"{exePath}\" \"%1\"");
                }
            }
            catch
            {
                // 忽略协议注册错误，应用仍可正常运行
            }
        }
    }
}
