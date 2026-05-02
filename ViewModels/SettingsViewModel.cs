using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GHPC_Mod_Manager.Services;
using GHPC_Mod_Manager.Models;
using GHPC_Mod_Manager.Views;
using GHPC_Mod_Manager.Helpers;
using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Diagnostics;
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

    private const string BilibiliSpaceUrl = "https://space.bilibili.com/3493285595187364";
    private const string GhpcCommunityUrl = "https://qm.qq.com/q/pSriG1UocE";

    // 记录原始语言，用于检测是否改变
    private string _originalLanguage = "zh-CN";

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

    [ObservableProperty]
    private bool _useDnsOverHttps;

    partial void OnSelectedProxyServerChanged(ProxyServerItem? value)
    {
        // 配置token时禁用代理，token清空时重新启用代理选项
        OnPropertyChanged(nameof(IsGitHubProxyEnabled));
    }

    private void RefreshProxyServerList()
    {
        var remote = _mainConfigService.GetRemoteProxyServers();
        var lang = _settingsService.Settings.Language;
        AvailableProxyServers = remote != null && remote.Count > 0
            ? ProxyServerItem.BuildFromRemote(remote, lang)
            : ProxyServerItem.BuildFallback();

        // 列表刷新后根据已保存枚举值选中对应项
        var savedEnum = _settingsService.Settings.GitHubProxyServer;
        SelectedProxyServer = AvailableProxyServers.FirstOrDefault(p => p.EnumValue == savedEnum)
            ?? AvailableProxyServers[0];
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
    private bool _isMelonLoaderOperating;

    // 版本号点击彩蛋
    private int _versionClickCount = 0;
    private System.Threading.Timer? _versionClickTimer;
    private const int EasterEggClickThreshold = 7;

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
        IServiceProvider serviceProvider)
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

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        try
        {
            // 检测语言是否改变
            bool languageChanged = SelectedLanguage != _originalLanguage;

            var settings = _settingsService.Settings;
            settings.Language = SelectedLanguage;
            settings.GameRootPath = GameRootPath;
            // dev模式下写入 CommandLineArgs override（不持久化到 settings.json）
            CommandLineArgs.DevConfigUrlOverride = string.IsNullOrWhiteSpace(MainConfigUrl) ? null : MainConfigUrl;
            settings.UseGitHubProxy = UseGitHubProxy;
            settings.GitHubProxyServer = SelectedProxyServer?.EnumValue ?? GitHubProxyServer.GhDmrGg;
            settings.UseDnsOverHttps = UseDnsOverHttps && SelectedLanguage == "zh-CN";
            settings.GitHubApiToken = GitHubApiToken;
            settings.Theme = SelectedTheme; // 保存主题设置
            settings.UpdateChannel = SelectedUpdateChannel; // 保存更新通道设置
            settings.SkipConflictCheck = SkipConflictCheck;
            settings.SkipIntegrityCheck = SkipIntegrityCheck;

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

    [RelayCommand]
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

        // 达到阈值时打开网页并解锁终末地主题
        if (_versionClickCount >= EasterEggClickThreshold)
        {
            _versionClickCount = 0;
            _versionClickTimer?.Dispose();

            // 播放彩蛋音频
            try
            {
                var audioPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "easter_egg.wav");
                if (File.Exists(audioPath))
                {
                    Task.Run(() =>
                    {
                        using var player = new SoundPlayer(audioPath);
                        player.PlaySync();
                    });
                }
            }
            catch { }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://ghpc.dmr.gg/eg",
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

    [RelayCommand]
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

    [RelayCommand]
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

    [RelayCommand]
    private void ShowModInfoDumper()
    {
        var window = _serviceProvider.GetRequiredService<ModInfoDumperWindow>();
        window.Owner = Application.Current.MainWindow;
        window.ShowDialog();
    }
}
