using GHPC_Mod_Manager.Services;
using GHPC_Mod_Manager.ViewModels;
using GHPC_Mod_Manager.Views;
using GHPC_Mod_Manager.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
            // Check for -log command line argument
            bool showLogWindow = e.Args.Contains("-log") || e.Args.Contains("--log");
            
            _host = Host.CreateDefaultBuilder()
                .ConfigureServices((context, services) =>
                {
                    // Services
                    services.AddSingleton<ILoggingService, LoggingService>();
                    services.AddSingleton<ISettingsService, SettingsService>();
                    services.AddSingleton<IThemeService, ThemeService>();
                    services.AddSingleton<INavigationService, NavigationService>();
                    services.AddSingleton<IProcessService, ProcessService>();
                    services.AddSingleton<IMelonLoaderService, MelonLoaderService>();
                    services.AddSingleton<IModManagerService, ModManagerService>();
                    services.AddSingleton<ITranslationManagerService, TranslationManagerService>();
                    services.AddSingleton<IModI18nService, ModI18nService>();
                    services.AddSingleton<IModBackupService, ModBackupService>();
                    services.AddSingleton<IAnnouncementService, AnnouncementService>();
                    
                    services.AddHttpClient<INetworkService, NetworkService>(client =>
                    {
                        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/139.0.0.0 Safari/537.36 Edg/139.0.0.0");
                    })
                    .ConfigurePrimaryHttpMessageHandler(() =>
                    {
                        var handler = new HttpClientHandler();
                        
                        // 配置证书验证回调以处理自签发证书和代理环境
                        handler.ServerCertificateCustomValidationCallback = (HttpRequestMessage request, X509Certificate2? cert, X509Chain? chain, SslPolicyErrors sslPolicyErrors) =>
                        {
                            // 如果没有错误，直接返回 true
                            if (sslPolicyErrors == SslPolicyErrors.None)
                                return true;

                            // 如果只是证书撤销状态无法验证的问题（常见于代理环境），则接受证书
                            if (sslPolicyErrors == SslPolicyErrors.RemoteCertificateChainErrors && chain != null)
                            {
                                // 检查链状态，如果只是撤销状态无法验证，则接受
                                foreach (var status in chain.ChainStatus)
                                {
                                    if (status.Status == X509ChainStatusFlags.RevocationStatusUnknown ||
                                        status.Status == X509ChainStatusFlags.OfflineRevocation)
                                    {
                                        continue; // 忽略撤销状态检查问题
                                    }
                                    else if (status.Status != X509ChainStatusFlags.NoError)
                                    {
                                        return false; // 其他错误不接受
                                    }
                                }
                                return true;
                            }

                            // 对于本地反代或开发环境，可以考虑接受自签名证书
                            // 但这里我们保持相对安全的策略，只处理撤销状态问题
                            return false;
                        };

                        // 禁用证书撤销检查以避免网络问题
                        handler.CheckCertificateRevocationList = false;
                        
                        return handler;
                    });

                    // ViewModels
                    services.AddTransient<MainWindowViewModel>();
                    services.AddTransient<SetupWizardViewModel>();
                    services.AddTransient<MainViewModel>();
                    services.AddTransient<SettingsViewModel>();
                    services.AddTransient<ModConfigurationViewModel>();
                    services.AddTransient<AnnouncementViewModel>();

                    // Views
                    services.AddTransient<MainWindow>();
                    services.AddTransient<SetupWizardView>();
                    services.AddTransient<MainView>();
                    services.AddTransient<SettingsView>();
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

            // 初始化主题系统
            var themeService = _host.Services.GetRequiredService<IThemeService>();
            var loggingService = _host.Services.GetRequiredService<ILoggingService>();
            
            loggingService.LogInfo("AppStartupSettingsTheme", settingsService.Settings.Theme);
            loggingService.LogInfo("AppStartupResourceDictionaryCount", Application.Current.Resources.MergedDictionaries.Count);
            
            // 由于App.xaml已经加载了Light主题，我们需要确保状态正确同步
            if (settingsService.Settings.Theme == AppTheme.Light)
            {
                // 如果设置是Light，需要同步ThemeService的内部状态但不需要重新加载资源
                loggingService.LogInfo("SettingsLightThemeConsistent");
                themeService.InitializeWithCurrentState(AppTheme.Light);
            }
            else
            {
                // 如果设置是Dark，需要切换主题并同步内部状态
                loggingService.LogInfo("SettingsDarkThemeNeedSwitch");
                themeService.SetTheme(settingsService.Settings.Theme);
            }
            
            loggingService.LogInfo("ThemeInitializationComplete", Application.Current.Resources.MergedDictionaries.Count);

            var processService = _host.Services.GetRequiredService<IProcessService>();
            await processService.StartMonitoringAsync();

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
                catch (Exception ex)
                {
                    // Log error but don't prevent shutdown
                    System.Diagnostics.Debug.WriteLine($"Error during shutdown: {ex.Message}");
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
    }
}
