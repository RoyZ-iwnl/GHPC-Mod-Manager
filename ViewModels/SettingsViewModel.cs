using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GHPC_Mod_Manager.Services;
using GHPC_Mod_Manager.Models;
using GHPC_Mod_Manager.Views;
using GHPC_Mod_Manager.Helpers;
using Microsoft.Win32;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Reflection;
using GHPC_Mod_Manager.Resources;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.ObjectModel;
using System.Media;

namespace GHPC_Mod_Manager.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settingsService;
    private readonly INavigationService _navigationService;
    private readonly ILoggingService _loggingService;
    private readonly INetworkService _networkService;
    private readonly IModI18nService _modI18nService;
    private readonly IThemeService _themeService;
    private readonly IModBackupService _modBackupService;
    private readonly IUpdateService _updateService;
    private readonly IMelonLoaderService _melonLoaderService;
    private readonly IMainConfigService _mainConfigService;
    private readonly IModManagerService _modManagerService;
    private readonly Lazy<MainViewModel> _mainViewModel;
    private readonly IServiceProvider _serviceProvider;
    private readonly IProcessService _processService;

    private const string BilibiliSpaceUrl = "https://space.bilibili.com/3493285595187364";
    private const string GhpcCommunityUrl = "https://qm.qq.com/q/pSriG1UocE";

    // 记录原始语言，用于检测是否改变
    private string _originalLanguage = "zh-CN";

    // 记录原始游戏路径，用于检测是否改变
    private string _originalGameRootPath = string.Empty;

    [ObservableProperty]
    private string _selectedLanguage = "zh-CN";

    partial void OnSelectedLanguageChanged(string value)
    {
        // Force disable GitHub proxy when language is not Chinese
        if (value != "zh-CN")
        {
            UseGitHubProxy = false;
            UseDnsOverHttps = false;
        }

        OnPropertyChanged(nameof(IsChineseLanguage));
        // Notify that proxy visibility has changed
        OnPropertyChanged(nameof(IsGitHubProxyVisible));
        OnPropertyChanged(nameof(IsDnsOverHttpsVisible));
    }

    [ObservableProperty]
    private string _gameRootPath = string.Empty;

    [ObservableProperty]
    private string _mainConfigUrl = string.Empty;

    [ObservableProperty]
    private bool _useGitHubProxy = false;

    [ObservableProperty]
    private List<ProxyServerItem> _availableProxyServers = ProxyServerItem.BuildFallback();

    [ObservableProperty]
    private ProxyServerItem? _selectedProxyServer;

    // 测速结果列表
    [ObservableProperty]
    private List<ProxyServerSpeedTestResult> _proxyServerTestResults = new();

    // 测速状态
    [ObservableProperty]
    private bool _isSpeedTesting;

    [ObservableProperty]
    private string _speedTestProgress = string.Empty;

    // 选中的测速结果（用于RadioButton选择）
    [ObservableProperty]
    private ProxyServerSpeedTestResult? _selectedSpeedTestResult;

    [ObservableProperty]
    private bool _useDnsOverHttps;

    partial void OnSelectedProxyServerChanged(ProxyServerItem? value)
    {
        // 配置token时禁用代理，token清空时重新启用代理选项
        OnPropertyChanged(nameof(IsGitHubProxyEnabled));

        // 同步更新选中的测速结果
        if (value != null && ProxyServerTestResults.Count > 0)
        {
            SelectedSpeedTestResult = ProxyServerTestResults.FirstOrDefault(r => r.ServerId == value.ServerId)
                ?? ProxyServerTestResults.FirstOrDefault(r => r.Server == value.EnumValue);
        }

        // 保存选择到设置
        if (value != null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    _loggingService.LogInfo($"保存代理服务器选择: ServerId={value.ServerId}, EnumValue={value.EnumValue}");
                    _settingsService.Settings.GitHubProxyServer = value.EnumValue;
                    _settingsService.Settings.GitHubProxyServerId = value.ServerId;
                    await _settingsService.SaveSettingsAsync();
                    _loggingService.LogInfo($"保存成功: GitHubProxyServerId={_settingsService.Settings.GitHubProxyServerId}");
                }
                catch (Exception ex)
                {
                    _loggingService.LogError(ex, "FailedToSaveProxyServerChange");
                }
            });
        }
    }

    public void RefreshProxyServerList()
    {
        var remote = _mainConfigService.GetRemoteProxyServers();
        var lang = _settingsService.Settings.Language;
        AvailableProxyServers = remote != null && remote.Count > 0
            ? ProxyServerItem.BuildFromRemote(remote, lang)
            : ProxyServerItem.BuildFallback();

        // 列表刷新后根据已保存的服务器ID选中对应项（优先使用服务器ID）
        var savedServerId = _settingsService.Settings.GitHubProxyServerId;
        var savedEnum = _settingsService.Settings.GitHubProxyServer;

        if (!string.IsNullOrEmpty(savedServerId))
        {
            SelectedProxyServer = AvailableProxyServers.FirstOrDefault(p => p.ServerId == savedServerId)
                ?? AvailableProxyServers.FirstOrDefault(p => p.EnumValue == savedEnum)
                ?? AvailableProxyServers[0];
        }
        else
        {
            SelectedProxyServer = AvailableProxyServers.FirstOrDefault(p => p.EnumValue == savedEnum)
                ?? AvailableProxyServers[0];
        }

        // 同时更新测速结果列表
        RefreshSpeedTestResults();
    }

    // 刷新测速结果列表（基于当前的 AvailableProxyServers）
    private void RefreshSpeedTestResults()
    {
        var results = new List<ProxyServerSpeedTestResult>();
        foreach (var proxy in AvailableProxyServers)
        {
            // 使用 proxy.ServerId 作为服务器标识（远程下发的Id）
            var result = new ProxyServerSpeedTestResult
            {
                Server = proxy.EnumValue,
                ServerId = proxy.ServerId,  // 保存远程下发的服务器Id
                Domain = proxy.Domain,
                DisplayName = proxy.DisplayName,
                Status = SpeedTestStatus.Pending
            };
            results.Add(result);
        }
        ProxyServerTestResults = results;

        // 选中当前选中的代理服务器对应的测速结果
        if (SelectedProxyServer != null)
        {
            SelectedSpeedTestResult = results.FirstOrDefault(r => r.ServerId == SelectedProxyServer.ServerId)
                ?? results.FirstOrDefault(r => r.Server == SelectedProxyServer.EnumValue);
        }
    }

    [ObservableProperty]
    private string _gitHubApiToken = string.Empty;

    partial void OnGitHubApiTokenChanged(string value)
    {
        // 配置token时禁用代理
        if (!string.IsNullOrWhiteSpace(value))
        {
            UseGitHubProxy = false;
        }
        OnPropertyChanged(nameof(IsGitHubProxyEnabled));
    }

    [ObservableProperty]
    private List<string> _availableLanguages = new() { "zh-CN", "en-US" };

    [ObservableProperty]
    private string _refreshStatus = string.Empty;

    [ObservableProperty]
    private AppTheme _selectedTheme = AppTheme.Light;

    [ObservableProperty]
    private ObservableCollection<AppTheme> _availableThemes = new() { AppTheme.Light, AppTheme.Dark };

    [ObservableProperty]
    private UpdateChannel _selectedUpdateChannel = UpdateChannel.Stable;

    partial void OnSelectedUpdateChannelChanged(UpdateChannel value)
    {
        // Auto-save when update channel changes
        var settings = _settingsService.Settings;
        settings.UpdateChannel = value;
        _ = _settingsService.SaveSettingsAsync();
    }

    [ObservableProperty]
    private List<UpdateChannel> _availableUpdateChannels = new() { UpdateChannel.Stable, UpdateChannel.Beta };

    [ObservableProperty]
    private bool _skipConflictCheck;

    [ObservableProperty]
    private bool _skipIntegrityCheck;

    [ObservableProperty]
    private bool _skipGameVersionCheck;

    [ObservableProperty]
    private bool _skipModUpdateCheck;

    [ObservableProperty]
    private bool _skipManagerVersionCheck;

    [ObservableProperty]
    private bool _onlineCheckOnLaunch;

    [ObservableProperty]
    private bool _openWebsiteOnStartup;

    partial void OnOpenWebsiteOnStartupChanged(bool value)
    {
        // 如果用户尝试关闭，弹窗确认
        if (!value)
        {
            // TODO: 启用时取消注释
            // var result = System.Windows.MessageBox.Show(
            //     Strings.ConfirmDisableWebsiteOnStartup,
            //     Strings.Confirm,
            //     System.Windows.MessageBoxButton.YesNo,
            //     System.Windows.MessageBoxImage.Question);
            //
            // if (result == System.Windows.MessageBoxResult.No)
            // {
            //     // 用户取消，恢复为 true
            //     OpenWebsiteOnStartup = true;
            //     return;
            // }
        }

        _settingsService.Settings.OpenWebsiteOnStartup = value;
        _ = _settingsService.SaveSettingsAsync();
    }

    // MelonLoader 管理
    [ObservableProperty]
    private string _melonLoaderInstalledVersion = string.Empty;

    [ObservableProperty]
    private bool _isMelonLoaderDisabled;

    [ObservableProperty]
    private bool _isMelonLoaderInstalled;

    [ObservableProperty]
    private List<GitHubRelease> _melonLoaderReleases = new();

    [ObservableProperty]
    private GitHubRelease? _selectedMelonLoaderRelease;

    [ObservableProperty]
    private bool _isGameRunning;

    [ObservableProperty]
    private bool _isMelonLoaderOperating;

    // 版本号点击彩蛋
    private int _versionClickCount = 0; // 记录点击次数
    private System.Threading.Timer? _versionClickTimer; // 点击计时器，用于在一定时间内重置点击计数
    private const int EasterEggClickThreshold = 10; // 点击次数阈值，达到后触发彩蛋

    // Windows API 播放音频（支持重叠）
    [DllImport("winmm.dll", CharSet = CharSet.Auto)]
    private static extern uint mciSendString(string command, string? buffer, uint bufferSize, IntPtr callback);

    private static int _audioInstanceCounter = 0;

    public string ApplicationVersion => GetApplicationVersion();

    public bool IsChineseLanguage => SelectedLanguage == "zh-CN";

    public bool IsGitHubProxyVisible => IsChineseLanguage;

    public bool IsDnsOverHttpsVisible => IsChineseLanguage;

    public bool IsGitHubProxyEnabled => string.IsNullOrWhiteSpace(GitHubApiToken);

    // 开发模式：仅通过 -dev 启动参数解锁
    public bool IsDevMode => CommandLineArgs.DevModeEnabled;

    public SettingsViewModel(
        ISettingsService settingsService,
        INavigationService navigationService,
        ILoggingService loggingService,
        INetworkService networkService,
        IModI18nService modI18nService,
        IThemeService themeService,
        IModBackupService modBackupService,
        IUpdateService updateService,
        IMelonLoaderService melonLoaderService,
        IMainConfigService mainConfigService,
        IModManagerService modManagerService,
        Lazy<MainViewModel> mainViewModel,
        IServiceProvider serviceProvider,
        IProcessService processService)
    {
        _settingsService = settingsService;
        _navigationService = navigationService;
        _loggingService = loggingService;
        _networkService = networkService;
        _modI18nService = modI18nService;
        _themeService = themeService;
        _modBackupService = modBackupService;
        _updateService = updateService;
        _melonLoaderService = melonLoaderService;
        _mainConfigService = mainConfigService;
        _modManagerService = modManagerService;
        _mainViewModel = mainViewModel;
        _serviceProvider = serviceProvider;
        _processService = processService;

        IsGameRunning = _processService.IsGameRunning;
        _processService.GameRunningStateChanged += (s, e) =>
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() => IsGameRunning = e);
        };

        // 订阅主题变更事件，同步更新设置页显示
        _themeService.ThemeChanged += OnThemeChanged;

        LoadSettings();
        RefreshProxyServerList();
        _ = LoadMelonLoaderStatusAsync();
    }

    private void LoadSettings()
    {
        var settings = _settingsService.Settings;
        SelectedLanguage = settings.Language;
        _originalLanguage = settings.Language; // 记录原始语言
        GameRootPath = settings.GameRootPath;
        _originalGameRootPath = settings.GameRootPath; // 记录原始游戏路径
        // dev模式下从 CommandLineArgs 读取，显示当前生效的 URL
        MainConfigUrl = CommandLineArgs.DevConfigUrlOverride ?? string.Empty;
        UseGitHubProxy = settings.UseGitHubProxy;
        UseDnsOverHttps = settings.UseDnsOverHttps;
        // SelectedProxyServer 由 RefreshProxyServerList() 在 LoadSettings 之后设置
        GitHubApiToken = settings.GitHubApiToken;
        SelectedTheme = settings.Theme; // 加载主题设置
        SelectedUpdateChannel = settings.UpdateChannel; // 加载更新通道设置
        SkipConflictCheck = settings.SkipConflictCheck;
        SkipIntegrityCheck = settings.SkipIntegrityCheck;
        SkipGameVersionCheck = settings.SkipGameVersionCheck;
        SkipModUpdateCheck = settings.SkipModUpdateCheck;
        SkipManagerVersionCheck = settings.SkipManagerVersionCheck;
        OnlineCheckOnLaunch = settings.OnlineCheckOnLaunch;
        OpenWebsiteOnStartup = settings.OpenWebsiteOnStartup;

        // 检查是否已解锁终末地主题
        if (settings.IsEndfieldThemeUnlocked == true && !AvailableThemes.Contains(AppTheme.Endfield))
        {
            AvailableThemes.Add(AppTheme.Endfield);
        }

        // 验证：如果主题是终末地但未解锁，重置为浅色主题
        if (SelectedTheme == AppTheme.Endfield && settings.IsEndfieldThemeUnlocked != true)
        {
            SelectedTheme = AppTheme.Light;
            settings.Theme = AppTheme.Light;
            _loggingService.LogInfo(Strings.EndfieldThemeLockedReset);
        }
    }

    private void OnThemeChanged(object? sender, AppTheme newTheme)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            SelectedTheme = newTheme;

            // 如果是 Endfield 且未在列表中，添加到可用主题
            if (newTheme == AppTheme.Endfield && !AvailableThemes.Contains(AppTheme.Endfield))
            {
                AvailableThemes.Add(AppTheme.Endfield);
            }
        });
    }

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        try
        {
            // 检查游戏路径是否改变且游戏是否在运行
            bool gamePathChanged = GameRootPath != _originalGameRootPath;
            if (gamePathChanged && IsGameRunning)
            {
                MessageDialogHelper.ShowError(Strings.CannotChangeGamePathWhileRunning, Strings.Error);
                return;
            }

            // 检测语言是否改变
            bool languageChanged = SelectedLanguage != _originalLanguage;

            var settings = _settingsService.Settings;
            settings.Language = SelectedLanguage;
            settings.GameRootPath = GameRootPath;
            // dev模式下写入 CommandLineArgs override（不持久化到 settings.json）
            CommandLineArgs.DevConfigUrlOverride = string.IsNullOrWhiteSpace(MainConfigUrl) ? null : MainConfigUrl;
            settings.UseGitHubProxy = UseGitHubProxy;
            settings.GitHubProxyServer = SelectedProxyServer?.EnumValue ?? GitHubProxyServer.GhDmrGg;
            settings.GitHubProxyServerId = SelectedProxyServer?.ServerId ?? string.Empty;
            settings.UseDnsOverHttps = UseDnsOverHttps && SelectedLanguage == "zh-CN";
            settings.GitHubApiToken = GitHubApiToken;
            settings.Theme = SelectedTheme; // 保存主题设置
            settings.UpdateChannel = SelectedUpdateChannel; // 保存更新通道设置
            settings.SkipConflictCheck = SkipConflictCheck;
            settings.SkipIntegrityCheck = SkipIntegrityCheck;
            settings.SkipGameVersionCheck = SkipGameVersionCheck;
            settings.SkipModUpdateCheck = SkipModUpdateCheck;
            settings.SkipManagerVersionCheck = SkipManagerVersionCheck;
            settings.OnlineCheckOnLaunch = OnlineCheckOnLaunch;

            await _settingsService.SaveSettingsAsync();
            _loggingService.LogInfo("DoH setting saved: enabled={0}, language={1}", settings.UseDnsOverHttps, settings.Language);

            if (_themeService.CurrentTheme != SelectedTheme)
            {
                _themeService.SetTheme(SelectedTheme);
            }

            // 如果语言改变了，提示用户重启应用
            if (languageChanged)
            {
                var result = MessageDialogHelper.Confirm(
                    Strings.LanguageChangedRestartRequired,
                    Strings.RestartRequired);

                if (result)
                {
                    // 重启应用
                    RestartApplication();
                }
                else
                {
                    // 用户选择稍后重启，更新原始语言以避免重复提示
                    _originalLanguage = SelectedLanguage;
                }
            }
            else
            {
                MessageDialogHelper.ShowSuccess(Strings.SettingsSaved, Strings.Success);
            }

            _loggingService.LogInfo(Strings.SettingsSaved);
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.SettingsSaveError);
            MessageDialogHelper.ShowError(Strings.SettingsSaveFailed, Strings.Error);
        }
    }

    private void RestartApplication()
    {
        try
        {
            // 获取当前应用程序路径
            var currentProcess = Process.GetCurrentProcess();
            var exePath = currentProcess.MainModule?.FileName;

            if (!string.IsNullOrEmpty(exePath))
            {
                // 启动新实例
                Process.Start(new ProcessStartInfo
                {
                    FileName = exePath,
                    UseShellExecute = true
                });

                // 退出当前应用
                Application.Current.Shutdown();
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.RestartApplicationError);
            MessageDialogHelper.ShowError(Strings.RestartApplicationFailed, Strings.Error);
        }
    }

    [RelayCommand]
    private async Task ForceRefreshConfigurationsAsync()
    {
        try
        {
            RefreshStatus = GHPC_Mod_Manager.Resources.Strings.Refreshing;
            
            // Clear all network caches
            _networkService.ClearCache();

            // 重新加载主配置（dev模式下会读本地路径，正常模式走远程并等待结果）
            await _mainConfigService.ForceReloadAsync();

            // 刷新代理服务器列表（可能有新节点下发）
            RefreshProxyServerList();

            // Refresh ModI18n configuration
            await _modI18nService.RefreshModI18nConfigAsync(forceRefresh: true);

            // 重新拉取 Mod 列表和所有 GitHub Releases（缓存已清空，必须重拉）
            await _modManagerService.GetModListAsync(forceRefresh: true);
            
            RefreshStatus = GHPC_Mod_Manager.Resources.Strings.ConfigurationsRefreshedSuccessfully;
            _loggingService.LogInfo(Strings.ConfigurationRefreshComplete);
            
            // Clear the status message after 3 seconds
            await Task.Delay(3000);
            RefreshStatus = string.Empty;
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.ConfigurationRefreshError);
            RefreshStatus = GHPC_Mod_Manager.Resources.Strings.RefreshFailedCheckLogs;
            
            await Task.Delay(3000);
            RefreshStatus = string.Empty;
        }
    }

    [RelayCommand(CanExecute = nameof(CanBrowseGameDirectory))]
    private void BrowseGameDirectory()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "GHPC.exe|GHPC.exe",
            Title = Strings.SelectGHPCExeFile
        };

        if (dialog.ShowDialog() == true)
        {
            var selectedFile = dialog.FileName;
            if (Path.GetFileName(selectedFile).Equals("GHPC.exe", StringComparison.OrdinalIgnoreCase))
            {
                GameRootPath = Path.GetDirectoryName(selectedFile) ?? string.Empty;
            }
            else
            {
                MessageDialogHelper.ShowError(Strings.PleaseSelectGHPCExe, Strings.Error);
            }
        }
    }

    [RelayCommand]
    private async Task CleanTempFilesAsync()
    {
        try
        {
            var tempPath = _settingsService.TempPath;
            if (Directory.Exists(tempPath))
            {
                var files = Directory.GetFiles(tempPath);
                var directories = Directory.GetDirectories(tempPath);

                foreach (var file in files)
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch
                    {
                        // Ignore files that can't be deleted
                    }
                }

                foreach (var directory in directories)
                {
                    try
                    {
                        Directory.Delete(directory, true);
                    }
                    catch
                    {
                        // Ignore directories that can't be deleted
                    }
                }

                MessageDialogHelper.ShowSuccess(Strings.TempFilesCleaned_, Strings.Success);
                _loggingService.LogInfo(Strings.TempFilesCleaned);
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.TempFilesCleanError);
            MessageDialogHelper.ShowError(Strings.TempFilesCleanFailed, Strings.Error);
        }
    }

    [RelayCommand]
    private async Task CleanupModBackupsAsync()
    {
        try
        {
            var result = MessageDialogHelper.Confirm(
                Strings.ConfirmCleanupModBackups,
                Strings.ConfirmCleanup);

            if (result)
            {
                var freedBytes = await _modBackupService.CleanupModBackupsAsync();
                var freedMB = (freedBytes / (1024.0 * 1024.0)).ToString("F1");

                // 刷新MainViewModel的Mod列表，更新HasBackup状态
                await _mainViewModel.Value.RefreshDataAsync();

                MessageDialogHelper.ShowSuccess(
                    string.Format(Strings.CleanupModBackupsSuccessful, freedMB),
                    Strings.Success);
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.CleanupModBackupsFailed, ex.Message);
            MessageDialogHelper.ShowError(
                string.Format(Strings.CleanupModBackupsFailed, ex.Message),
                Strings.Error);
        }
    }

    [RelayCommand]
    private void ShowLogWindow()
    {
        var logWindow = new Views.LogWindow();
        logWindow.Show();
    }

    [RelayCommand]
    private void BackToMain()
    {
        _navigationService.NavigateToMainView();
    }

    [RelayCommand]
    private void ChangeTheme()
    {
        try
        {
            _themeService.SetTheme(SelectedTheme);
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.ThemeApplicationError);
        }
    }

    [RelayCommand]
    private void OpenFollowBilibili()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = BilibiliSpaceUrl,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.FailedToOpenBilibili);
        }
    }

    [RelayCommand]
    private void OpenGhpcCommunity()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = GhpcCommunityUrl,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.FailedToOpenGhpcCommunity);
        }
    }

    [RelayCommand]
    private void OpenSupportAuthor()
    {
        // TODO: 启用时取消注释
        // try
        // {
        //     Process.Start(new ProcessStartInfo
        //     {
        //         FileName = "https://ghpcmm.link/support",
        //         UseShellExecute = true
        //     });
        // }
        // catch (Exception ex)
        // {
        //     _loggingService.LogError(ex, Strings.FailedToOpenSupportPage);
        // }
    }

    [RelayCommand]
    private void OpenProjectUrl()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://github.com/RoyZ-iwnl/GHPC-Mod-Manager/",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.OpenProjectUrlError);
        }
    }

    // 版本号点击彩蛋
    public async void HandleVersionClick()
    {
        _versionClickCount++;

        // 第一次点击时播放电量低音效（可重叠）
        if (_versionClickCount == 2)
        {
            try
            {
                var audioPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "battery_low.wav");
                if (File.Exists(audioPath))
                {
                    // mciSendString 每个文件用独立别名，支持真正的重叠播放
                    var alias = $"battery_{Interlocked.Increment(ref _audioInstanceCounter)}";
                    mciSendString($"open \"{audioPath}\" type waveaudio alias {alias}", null, 0, IntPtr.Zero);
                    mciSendString($"play {alias}", null, 0, IntPtr.Zero);
                }
            }
            catch { }
        }

        // 达到阈值时打开网页并解锁终末地主题
        if (_versionClickCount >= EasterEggClickThreshold)
        {
            _versionClickCount = 0;
            _versionClickTimer?.Dispose();

            // 播放彩蛋音频（可重叠）
            try
            {
                var audioPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "easter_egg.wav");
                if (File.Exists(audioPath))
                {
                    // mciSendString 每个文件用独立别名，支持真正的重叠播放
                    var alias = $"easter_{Interlocked.Increment(ref _audioInstanceCounter)}";
                    mciSendString($"open \"{audioPath}\" type waveaudio alias {alias}", null, 0, IntPtr.Zero);
                    mciSendString($"play {alias}", null, 0, IntPtr.Zero);
                }
            }
            catch { }

            try
            {
                var url = _settingsService.Settings.Language == "zh-CN"
                    ? "https://ghpcmm.link/eg"
                    : "https://ghpc.dmr.gg/eg";

                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch { }

            try
            {
                if (_settingsService.Settings.IsEndfieldThemeUnlocked != true)
                {
                    _settingsService.Settings.IsEndfieldThemeUnlocked = true;
                    await _settingsService.SaveSettingsAsync();

                    if (!AvailableThemes.Contains(AppTheme.Endfield))
                    {
                        AvailableThemes.Add(AppTheme.Endfield);
                    }

                    _loggingService.LogInfo(Strings.EndfieldThemeUnlocked);
                }
            }
            catch (Exception ex)
            {
                _loggingService.LogError(ex, Strings.EndfieldThemeUnlockError);
            }

            return;
        }

        // 重置计时器
        _versionClickTimer?.Dispose();
        _versionClickTimer = new System.Threading.Timer(_ =>
        {
            _versionClickCount = 0;
        }, null, 2000, System.Threading.Timeout.Infinite);
    }

    [RelayCommand]
    private async Task CheckForAppUpdatesAsync()
    {
        try
        {
            _loggingService.LogInfo(Strings.CheckingForApplicationUpdates);

            var currentVersion = _updateService.GetCurrentVersion();
            var (hasUpdate, latestVersion, downloadUrl, expectedSize, expectedDigest) = await _updateService.CheckForUpdatesAsync();

            if (hasUpdate && !string.IsNullOrEmpty(latestVersion) && !string.IsNullOrEmpty(downloadUrl))
            {
                var result = MessageDialogHelper.Confirm(
                    $"{Strings.NewVersionAvailable} {latestVersion}\n{Strings.ApplicationVersion}: {currentVersion}\n\n{Strings.DoYouWantToDownloadAndInstall}",
                    Strings.CheckForUpdates);

                if (result)
                {
                    var progress = new Progress<DownloadProgress>(downloadProgress =>
                    {
                        var speedText = downloadProgress.GetFormattedSpeed();
                        var progressText = downloadProgress.GetFormattedProgress();
                        var percentage = downloadProgress.ProgressPercentage;

                        _loggingService.LogInfo(Strings.DownloadingUpdateProgress, latestVersion, percentage.ToString("F1"), progressText, speedText);
                    });

                    var success = await _updateService.DownloadAndInstallUpdateAsync(downloadUrl, latestVersion, progress, expectedSize, expectedDigest);

                    if (!success)
                    {
                        MessageDialogHelper.ShowError(Strings.FailedToDownloadOrInstall, Strings.Error);
                    }
                    // If success, the application will exit and restart
                }
            }
            else
            {
                MessageDialogHelper.ShowInformation(string.Format(Strings.YouAreUsingLatestVersion, currentVersion), Strings.CheckForUpdates);
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.ApplicationUpdateCheckFailed);
            MessageDialogHelper.ShowError($"Failed to check for updates: {ex.Message}", Strings.Error);
        }
    }

    [RelayCommand]
    private async Task ShowAnnouncementAsync()
    {
        try
        {
            var language = _settingsService.Settings.Language;
            var announcementViewModel = _serviceProvider.GetRequiredService<AnnouncementViewModel>();

            await announcementViewModel.LoadAnnouncementAsync(language);

            if (announcementViewModel.HasContent)
            {
                var announcementWindow = new Views.AnnouncementWindow
                {
                    DataContext = announcementViewModel,
                    Owner = Application.Current.MainWindow,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner
                };
                announcementWindow.ShowDialog();
            }
            else
            {
                MessageDialogHelper.ShowInformation(string.Format(Strings.NoAnnouncementAvailable, language), Strings.Announcement);
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.AnnouncementLoadFailed);
        }
    }

    private static string GetApplicationVersion()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            return informationalVersion ?? assembly.GetName().Version?.ToString() ?? "Unknown";
        }
        catch
        {
            return "Unknown";
        }
    }

    private async Task LoadMelonLoaderStatusAsync()
    {
        var gameRoot = _settingsService.Settings.GameRootPath;
        if (string.IsNullOrEmpty(gameRoot)) return;

        IsMelonLoaderInstalled = await _melonLoaderService.IsMelonLoaderInstalledAsync(gameRoot);
        IsMelonLoaderDisabled = _melonLoaderService.IsMelonLoaderDisabled(gameRoot);

        if (IsMelonLoaderInstalled || IsMelonLoaderDisabled)
        {
            var ver = await _melonLoaderService.GetInstalledVersionAsync(gameRoot);
            MelonLoaderInstalledVersion = ver ?? Strings.MelonLoaderNotDetected;
        }
        else
        {
            MelonLoaderInstalledVersion = Strings.MelonLoaderNotDetected;
        }

        try
        {
            MelonLoaderReleases = await _melonLoaderService.GetMelonLoaderReleasesAsync();
            SelectedMelonLoaderRelease = MelonLoaderReleases.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.MelonLoaderReleasesLoadError);
        }
    }

    [RelayCommand(CanExecute = nameof(CanExecuteMelonLoaderOps))]
    private async Task ToggleMelonLoaderAsync()
    {
        var gameRoot = _settingsService.Settings.GameRootPath;
        if (string.IsNullOrEmpty(gameRoot)) return;

        IsMelonLoaderOperating = true;
        try
        {
            bool success;
            if (IsMelonLoaderDisabled)
            {
                success = await _melonLoaderService.EnableMelonLoaderAsync(gameRoot);
                if (success)
                {
                    IsMelonLoaderDisabled = false;
                    _loggingService.LogInfo(Strings.MelonLoaderEnableSuccess);
                }
                else
                {
                    MessageDialogHelper.ShowError(Strings.MelonLoaderEnableFailed, Strings.Error);
                }
            }
            else
            {
                success = await _melonLoaderService.DisableMelonLoaderAsync(gameRoot);
                if (success)
                {
                    IsMelonLoaderDisabled = true;
                    _loggingService.LogInfo(Strings.MelonLoaderDisableSuccess);
                }
                else
                {
                    MessageDialogHelper.ShowError(Strings.MelonLoaderDisableFailed, Strings.Error);
                }
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.MelonLoaderCheckError);
        }
        finally
        {
            IsMelonLoaderOperating = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanExecuteMelonLoaderOps))]
    private async Task ReinstallMelonLoaderAsync()
    {
        if (SelectedMelonLoaderRelease == null)
        {
            MessageDialogHelper.ShowError(Strings.PleaseSelectMelonLoaderVersion, Strings.Error);
            return;
        }

        var gameRoot = _settingsService.Settings.GameRootPath;
        if (string.IsNullOrEmpty(gameRoot)) return;

        IsMelonLoaderOperating = true;
        try
        {
            var progress = new Progress<DownloadProgress>(p =>
                _loggingService.LogInfo(Strings.InstallingMelonLoader + $" {p.ProgressPercentage:F1}% - {p.GetFormattedSpeed()}"));

            // 已安装时先用当前版本ZIP建立索引并清理旧文件，再安装新版本
            if (IsMelonLoaderInstalled && !string.IsNullOrEmpty(MelonLoaderInstalledVersion)
                && MelonLoaderInstalledVersion != Strings.MelonLoaderNotDetected)
            {
                var uninstallOk = await _melonLoaderService.UninstallCurrentVersionAsync(
                    gameRoot, MelonLoaderInstalledVersion, progress);
                if (!uninstallOk)
                {
                    MessageDialogHelper.ShowError(Strings.MelonLoaderUninstallFailed, Strings.Error);
                    return;
                }
            }

            var success = await _melonLoaderService.InstallMelonLoaderAsync(gameRoot, SelectedMelonLoaderRelease.TagName, progress);
            if (success)
            {
                var installedVersion = SelectedMelonLoaderRelease.TagName;
                await LoadMelonLoaderStatusAsync();
                MessageDialogHelper.ShowSuccess(string.Format(Strings.MelonLoaderInstalled, installedVersion),
                    Strings.Success);
            }
            else
            {
                MessageDialogHelper.ShowError(Strings.MelonLoaderInstallFailed, Strings.Error);
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.MelonLoaderInstallError, SelectedMelonLoaderRelease.TagName);
            MessageDialogHelper.ShowError(Strings.MelonLoaderInstallationError, Strings.Error);
        }
        finally
        {
            IsMelonLoaderOperating = false;
        }
    }

    private bool CanExecuteMelonLoaderOps() => !IsGameRunning && !IsMelonLoaderOperating;

    private bool CanBrowseGameDirectory() => !IsGameRunning;

    partial void OnIsGameRunningChanged(bool value)
    {
        ToggleMelonLoaderCommand.NotifyCanExecuteChanged();
        ReinstallMelonLoaderCommand.NotifyCanExecuteChanged();
        BrowseGameDirectoryCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsMelonLoaderOperatingChanged(bool value)
    {
        ToggleMelonLoaderCommand.NotifyCanExecuteChanged();
        ReinstallMelonLoaderCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void ShowModInfoDumper()
    {
        var window = _serviceProvider.GetRequiredService<ModInfoDumperWindow>();
        window.Owner = Application.Current.MainWindow;
        window.ShowDialog();
    }

    #region 代理服务器测速功能

    // 测速使用的测试URL（小型文件，用于测试下载速度）
    private const string SpeedTestUrl = "https://raw.githubusercontent.com/microsoft/vscode/main/package.json";
    private const int SpeedTestTimeoutMs = 10000; // 10秒超时

    [RelayCommand]
    private async Task TestAllProxyServersAsync()
    {
        if (IsSpeedTesting || ProxyServerTestResults.Count == 0)
            return;

        try
        {
            IsSpeedTesting = true;
            int total = ProxyServerTestResults.Count;
            int completed = 0;

            foreach (var result in ProxyServerTestResults)
            {
                SpeedTestProgress = string.Format(Strings.SpeedTestProgress, result.DisplayName, completed + 1, total);
                await TestSingleProxyServerAsync(result);
                completed++;
                SpeedTestProgress = $"已完成 {completed}/{total}";
            }

            // 找出最优服务器并自动选中
            var bestResult = ProxyServerTestResults
                .Where(r => r.Status == SpeedTestStatus.Success)
                .OrderBy(r => r.LatencyMs)
                .FirstOrDefault();

            if (bestResult != null)
            {
                SelectedSpeedTestResult = bestResult;
                // 优先使用ServerId匹配，其次使用枚举匹配
                SelectedProxyServer = AvailableProxyServers.FirstOrDefault(p => p.ServerId == bestResult.ServerId)
                    ?? AvailableProxyServers.FirstOrDefault(p => p.EnumValue == bestResult.Server);
            }

            SpeedTestProgress = Strings.SpeedTestComplete;
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, "ProxySpeedTestFailed");
            SpeedTestProgress = Strings.SpeedTestFailed;
        }
        finally
        {
            IsSpeedTesting = false;
        }
    }

    [RelayCommand]
    private async Task TestSingleProxyServerAsync(ProxyServerSpeedTestResult? result)
    {
        if (result == null || result.Status == SpeedTestStatus.Testing)
            return;

        try
        {
            result.Status = SpeedTestStatus.Testing;

            // 获取代理域名（使用ServerId从远程配置获取）
            string proxyDomain = GetProxyDomain(result.ServerId);
            _loggingService.LogInfo($"测速服务器: {result.ServerId}, 域名: {proxyDomain}");

            // 1. 延迟测试：通过代理访问 GitHub API 测试连通性
            var (latency, isRateLimited) = await TestLatencyViaProxyAsync(proxyDomain);
            result.LatencyMs = latency;
            result.IsRateLimited = isRateLimited;
            _loggingService.LogInfo($"延迟测试结果: {latency}ms, 超限: {isRateLimited}");

            // 2. 下载速度测试：使用代理下载小文件测试速度
            double speedMbps = await TestDownloadSpeedViaProxyAsync(proxyDomain);
            result.SpeedMbps = speedMbps;
            _loggingService.LogInfo($"速度测试结果: {speedMbps:F2} MB/s");

            // 如果被限流，标记状态但不算失败
            if (result.IsRateLimited)
            {
                // rate limited but still connected
                result.Status = SpeedTestStatus.Success;
            }
            else
            {
                result.Status = SpeedTestStatus.Success;
            }
            result.TestTime = DateTime.Now;
        }
        catch (OperationCanceledException)
        {
            result.Status = SpeedTestStatus.Timeout;
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, "SpeedTestError");
            result.Status = SpeedTestStatus.Failed;
        }
    }

    // 测试延迟 - 通过代理服务器访问 GitHub API
    // 返回: (延迟毫秒, 是否API超限)
    private async Task<(int latency, bool isRateLimited)> TestLatencyViaProxyAsync(string proxyDomain)
    {
        // 使用代理域名访问 GitHub API
        string testUrl = $"https://{proxyDomain}/https://api.github.com/repos/thebeninator/Pact-Increased-Lethality/releases";

        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };

        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMilliseconds(SpeedTestTimeoutMs)
        };
        client.DefaultRequestHeaders.Add("User-Agent", "GHPC-Mod-Manager/1.0");

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var response = await client.GetAsync(testUrl);
            var statusCode = (int)response.StatusCode;
            var content = await response.Content.ReadAsStringAsync();
            stopwatch.Stop();

            // 判断是否成功连接
            // 200: 成功获取 releases
            // 404: 仓库不存在，但代理连通
            // 403: 可能 rate limit，需要检查响应内容
            if (statusCode == 200)
            {
                return ((int)stopwatch.ElapsedMilliseconds, false);
            }
            else if (statusCode == 404)
            {
                return ((int)stopwatch.ElapsedMilliseconds, false);
            }
            else if (statusCode == 403)
            {
                // 检查是否是 rate limit
                if (content.Contains("rate limit") || content.Contains("API rate limit"))
                {
                    // rate limit 也说明代理连通，只是配额用完了
                    return ((int)stopwatch.ElapsedMilliseconds, true);
                }
                // 其他 403 可能是代理被拒绝
                throw new Exception($"代理返回403: {content.Substring(0, Math.Min(100, content.Length))}");
            }
            else
            {
                throw new Exception($"代理返回错误码: {statusCode}");
            }
        }
        catch (TaskCanceledException)
        {
            stopwatch.Stop();
            throw; // 重新抛出，让外层处理超时
        }
        catch
        {
            stopwatch.Stop();
            throw;
        }
    }

    // 测试下载速度 - 通过代理下载小文件
    private async Task<double> TestDownloadSpeedViaProxyAsync(string proxyDomain)
    {
        // 使用代理下载一个小文件（vscode的package.json很小，适合测速）
        string testUrl = $"https://{proxyDomain}/https://raw.githubusercontent.com/microsoft/vscode/main/package.json";

        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };

        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMilliseconds(SpeedTestTimeoutMs)
        };
        client.DefaultRequestHeaders.Add("User-Agent", "GHPC-Mod-Manager/1.0");

        try
        {
            var response = await client.GetAsync(testUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            using var contentStream = await response.Content.ReadAsStreamAsync();
            using var memoryStream = new MemoryStream();

            var buffer = new byte[8192];
            long totalBytes = 0;
            var startTime = DateTime.Now;

            while (true)
            {
                var bytesRead = await contentStream.ReadAsync(buffer);
                if (bytesRead == 0) break;

                await memoryStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                totalBytes += bytesRead;

                // 超时检测
                if ((DateTime.Now - startTime).TotalSeconds > 10)
                    break;
            }

            var elapsed = (DateTime.Now - startTime).TotalSeconds;
            if (elapsed > 0 && totalBytes > 0)
            {
                // 转换为 MB/s
                return (totalBytes / 1024.0 / 1024.0) / elapsed;
            }

            return 0;
        }
        catch
        {
            return 0;
        }
    }

    // 获取代理服务器域名（根据服务器Id从远程配置获取）
    private string GetProxyDomain(string serverId)
    {
        // 优先从远程配置获取
        var remoteServers = _mainConfigService.GetRemoteProxyServers();
        if (remoteServers != null)
        {
            var remote = remoteServers.FirstOrDefault(p =>
                string.Equals(p.Id, serverId, StringComparison.OrdinalIgnoreCase));
            if (remote != null)
                return remote.Domain;
        }

        // 兜底到本地枚举映射
        if (Enum.TryParse<GitHubProxyServer>(serverId, out var enumVal))
        {
            return enumVal switch
            {
                GitHubProxyServer.GhDmrGg => "gh.dmr.gg",
                GitHubProxyServer.Gh1DmrGg => "gh1.dmr.gg",
                GitHubProxyServer.EdgeOneGhProxyCom => "edgeone.gh-proxy.com",
                GitHubProxyServer.GhProxyCom => "gh-proxy.com",
                GitHubProxyServer.HkGhProxyCom => "hk.gh-proxy.com",
                GitHubProxyServer.CdnGhProxyCom => "cdn.gh-proxy.com",
                _ => "gh.dmr.gg"
            };
        }

        return "gh.dmr.gg";
    }

    // 选择测速结果时同步到 SelectedProxyServer
    partial void OnSelectedSpeedTestResultChanged(ProxyServerSpeedTestResult? value)
    {
        if (value != null)
        {
            // 更新所有项的选中状态（单选）
            foreach (var item in ProxyServerTestResults)
            {
                item.IsSelected = item == value;
            }

            // 优先使用ServerId匹配，其次使用枚举匹配
            SelectedProxyServer = AvailableProxyServers.FirstOrDefault(p => p.ServerId == value.ServerId)
                ?? AvailableProxyServers.FirstOrDefault(p => p.EnumValue == value.Server);
        }
    }

    #endregion
}
