using GHPC_Mod_Manager.Services;
using GHPC_Mod_Manager.ViewModels;
using GHPC_Mod_Manager.Views;
using GHPC_Mod_Manager.Models;
using GHPC_Mod_Manager.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Windows;

namespace GHPC_Mod_Manager
{
    public partial class App : Application
    {
        private IHost? _host;

        protected override async void OnStartup(StartupEventArgs e)
        {
            // 解析命令行参数
            bool showLogWindow = e.Args.Contains("-log") || e.Args.Contains("--log");
            bool isDevMode = false;
            string? devConfigPath = null;

            // 解析 -dev 参数，支持三种格式：
            // 1. -dev 或 --dev（单独使用，仅启用 dev 模式）
            // 2. -dev:"path" 或 --dev:"path"（冒号分隔）
            // 3. -dev="path" 或 --dev="path"（等号分隔）
            // 注：快捷方式启动不支持双引号参数，推荐使用冒号或等号格式
            for (int i = 0; i < e.Args.Length; i++)
            {
                var arg = e.Args[i];
                // 检查是否是 -dev 或 --dev 参数（可能带路径）
                if (arg.StartsWith("-dev") || arg.StartsWith("--dev"))
                {
                    isDevMode = true;

                    // 检查冒号或等号分隔的路径
                    int separatorIndex = -1;
                    if (arg.Contains(':'))
                        separatorIndex = arg.IndexOf(':');
                    else if (arg.Contains('='))
                        separatorIndex = arg.IndexOf('=');

                    if (separatorIndex > 0 && separatorIndex < arg.Length - 1)
                    {
                        // 提取冒号/等号后的路径
                        devConfigPath = arg.Substring(separatorIndex + 1);
                    }
                    else if (arg == "-dev" || arg == "--dev")
                    {
                        // 单独的 -dev 参数，检查下一个参数是否是路径（不以 - 开头）
                        if (i + 1 < e.Args.Length && !e.Args[i + 1].StartsWith("-"))
                        {
                            devConfigPath = e.Args[i + 1];
                            i++; // 跳过路径参数
                        }
                    }
                    break;
                }
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
                    services.AddSingleton<ITranslationBackupService, TranslationBackupService>();
                    services.AddSingleton<IModI18nService, ModI18nService>();
                    services.AddSingleton<IModBackupService, ModBackupService>();
                    services.AddSingleton<IAnnouncementService, AnnouncementService>();
                    services.AddSingleton<IUpdateService, UpdateService>();
                    services.AddSingleton<ISteamGameFinderService, SteamGameFinderService>();
                    services.AddSingleton<IVersionCleanupService, VersionCleanupService>();

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
                    // 子页面ViewModel（单例，数据共享）
                    services.AddSingleton<InstalledModsViewModel>();
                    services.AddSingleton<ModBrowserViewModel>();
                    services.AddSingleton<ModDetailViewModel>();

                    // Views
                    services.AddTransient<MainWindow>();
                    services.AddTransient<SetupWizardView>();
                    services.AddSingleton<MainView>(); // 改为单例，避免导航时重新创建
                    services.AddTransient<SettingsView>();
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
            if (isDevMode)
            {
                startupLoggingService.LogInfo("Dev mode enabled via -dev argument");
                DevMode.IsEnabled = true;
                if (!string.IsNullOrWhiteSpace(devConfigPath))
                {
                    DevMode.MainConfigUrlOverride = devConfigPath;
                    startupLoggingService.LogInfo($"Dev config path: {devConfigPath}");
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
            
            // 由于App.xaml已经加载了Light主题，我们需要确保状态正确同步
            if (settingsService.Settings.Theme == AppTheme.Light)
            {
                // 如果设置是Light，需要同步ThemeService的内部状态但不需要重新加载资源
                loggingService.LogInfo(Strings.SettingsLightThemeConsistent);
                themeService.InitializeWithCurrentState(AppTheme.Light);
            }
            else
            {
                // 如果设置是Dark，需要切换主题并同步内部状态
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
            if (showLogWindow)
            {
                var logWindow = new LogWindow();
                logWindow.Show();
            }

            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            MainWindow = mainWindow;
            mainWindow.Show();

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

                    await _host.StopAsync(TimeSpan.FromSeconds(5));
                    _host.Dispose();
                }
                catch
                {
                    // 忽略关闭时的错误，不阻止退出
                }
            }

            // Force exit if needed
            System.Environment.Exit(0);
        }

        public static T GetService<T>() where T : class
        {
            return ((App)Current)._host?.Services.GetService<T>() 
                ?? throw new InvalidOperationException($"Service {typeof(T).Name} not found");
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
    }
}
