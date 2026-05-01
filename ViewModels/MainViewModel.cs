using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GHPC_Mod_Manager.Models;
using GHPC_Mod_Manager.Services;
using GHPC_Mod_Manager.Views;
using GHPC_Mod_Manager.Helpers;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using GHPC_Mod_Manager.Resources;
using System.Linq;

namespace GHPC_Mod_Manager.ViewModels;

// 导航项模型
public class NavigationItem
{
    public string Label { get; set; } = string.Empty;
    public NavigationPage Page { get; set; }
}

// 导航页面枚举
public enum NavigationPage
{
    InstalledMods,
    ModBrowser,
    Translation
}

internal static class ModInstallCompatibilityHelper
{
    public static async Task<bool> ConfirmInstallAsync(
        ModViewModel mod,
        string? currentGameVersion,
        ISettingsService settingsService,
        IMelonLoaderService melonLoaderService)
    {
        var supportedVersions = mod.SupportedGameVersions
            .Select(NormalizeVersion)
            .Where(version => !string.IsNullOrWhiteSpace(version))
            .Where(version => !string.Equals(version, Strings.Unknown, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (supportedVersions.Count == 0)
            return true;

        var resolvedCurrentVersion = NormalizeVersion(currentGameVersion);
        if (string.IsNullOrWhiteSpace(resolvedCurrentVersion))
        {
            var gameRoot = settingsService.Settings.GameRootPath;
            if (!string.IsNullOrWhiteSpace(gameRoot))
                resolvedCurrentVersion = NormalizeVersion(await melonLoaderService.GetCurrentGameVersionAsync(gameRoot));
        }

        if (string.IsNullOrWhiteSpace(resolvedCurrentVersion))
            return true;

        if (supportedVersions.Contains(resolvedCurrentVersion, StringComparer.OrdinalIgnoreCase))
            return true;

        var supportedVersionText = string.Join(", ", supportedVersions);
        var result = MessageDialogHelper.Confirm(
            string.Format(Strings.GameVersionMismatchDialogMessage, mod.DisplayName, resolvedCurrentVersion, supportedVersionText),
            Strings.GameVersionMismatchDialogTitle);

        return result;
    }

    private static string NormalizeVersion(string? version)
        => version?.Trim().TrimStart('v', 'V') ?? string.Empty;
}

public partial class MainViewModel : ObservableObject
{
    // Static flag to ensure startup update check runs only once per application session
    private static bool _hasPerformedStartupUpdateCheck = false;
    private static readonly object _updateCheckLock = new object();

    // Instance flag to prevent re-initialization when navigating back from settings
    private bool _isInitialized = false;
    private bool _hasPerformedStartupIntegrityCheck = false;

    private readonly IModManagerService _modManagerService;
    private readonly ITranslationManagerService _translationManagerService;
    private readonly IProcessService _processService;
    private readonly INavigationService _navigationService;
    private readonly ILoggingService _loggingService;
    private readonly IServiceProvider _serviceProvider;
    private readonly ISettingsService _settingsService;
    private readonly INetworkService _networkService;
    private readonly IUpdateService _updateService;
    private readonly IMelonLoaderService _melonLoaderService;
    private readonly IAnnouncementService _announcementService;
    private readonly IModCatalogStateService _modCatalogStateService;

    // 子ViewModel（导航页面）
    private InstalledModsViewModel? _installedModsViewModel;
    private ModBrowserViewModel? _modBrowserViewModel;
    private ModDetailViewModel? _modDetailViewModel;

    // 子View（导航页面）
    private UserControl? _installedModsView;
    private UserControl? _modBrowserView;
    private UserControl? _translationView;

    [ObservableProperty]
    private UserControl? _currentPageView;

    [ObservableProperty]
    private string _footerStatusMessage = string.Empty;

    [ObservableProperty]
    private bool _isFooterBusy;

    [ObservableProperty]
    private bool _isFooterProgressIndeterminate;

    [ObservableProperty]
    private double _footerProgressValue;

    [ObservableProperty]
    private string _footerProgressText = string.Empty;

    [ObservableProperty]
    private ObservableCollection<NavigationItem> _navigationItems = new();

    [ObservableProperty]
    private NavigationItem? _selectedNavigationItem;

    partial void OnSelectedNavigationItemChanged(NavigationItem? value)
    {
        if (value != null)
            NavigateToPage(value.Page);
    }

    [ObservableProperty]
    private ObservableCollection<ModViewModel> _mods = new();

    [ObservableProperty]
    private ObservableCollection<ModViewModel> _filteredMods = new();

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _filteredModCount = string.Empty;

    [ObservableProperty]
    private ModViewModel? _selectedMod;

    [ObservableProperty]
    private bool _isGameRunning;

    [ObservableProperty]
    private bool _isTranslationInstalled;

    [ObservableProperty]
    private bool _isTranslationPluginEnabled;

    [ObservableProperty]
    private bool _isTranslationManuallyInstalled;

    [ObservableProperty]
    private bool _isTranslationUpdateAvailable;

    [ObservableProperty]
    private bool _isTranslationPluginUpdateAvailable;

    [ObservableProperty]
    private bool _isTranslationResourceUpdateAvailable;

    [ObservableProperty]
    private string _latestXUnityVersion = string.Empty;

    [ObservableProperty]
    private List<string> _availableLanguages = new();

    [ObservableProperty]
    private string _currentTranslationLanguage = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isDownloading;

    [ObservableProperty]
    private double _operationProgressValue;

    [ObservableProperty]
    private bool _hasDeterminateOperationProgress;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private int _selectedTabIndex = 0;

    [ObservableProperty]
    private bool _onlyCheckInstalledMods = true;

    [ObservableProperty]
    private bool _isAppUpdateAvailable;

    [ObservableProperty]
    private string _latestAppVersion = string.Empty;

    [ObservableProperty]
    private string _latestAppDownloadUrl = string.Empty;

    [ObservableProperty]
    private bool _isTranslationUpdating = false;

    [ObservableProperty]
    private bool _isCheckingUpdates = false;

    [ObservableProperty]
    private bool _isOfflineMode = false;

    [ObservableProperty]
    private bool _isMelonLoaderDisabled = false;

    [ObservableProperty]
    private bool _isMelonLoaderNotInstalled = false;

    // 已安装下架Mod数量（用于横幅显示）
    [ObservableProperty]
    private int _delistedModCount;

    // 是否有已安装的下架Mod
    public bool HasDelistedMods => DelistedModCount > 0;

    // 含有未知字段的Mod数量（无论是否已安装）
    [ObservableProperty]
    private int _unknownFieldsModCount;

    // 是否有含未知字段的Mod
    public bool HasUnknownFieldsMods => UnknownFieldsModCount > 0;

    public void RefreshMelonLoaderState()
    {
        var gameRoot = _settingsService.Settings.GameRootPath;
        if (!string.IsNullOrEmpty(gameRoot))
            IsMelonLoaderDisabled = _melonLoaderService.IsMelonLoaderDisabled(gameRoot);
    }

    /// <summary>
    /// 启动时检测MelonLoader是否已安装，未安装则显示overlay提示
    /// </summary>
    private async Task CheckMelonLoaderInstalledAsync()
    {
        var gameRoot = _settingsService.Settings.GameRootPath;
        if (string.IsNullOrEmpty(gameRoot)) return;

        // 已禁用状态说明之前安装过，不需要提示
        if (IsMelonLoaderDisabled) return;

        var isInstalled = await _melonLoaderService.IsMelonLoaderInstalledAsync(gameRoot);
        if (!isInstalled)
        {
            _loggingService.LogWarning(Strings.MelonLoaderNotInstalledMessage);
            IsMelonLoaderNotInstalled = true;
        }
    }

    [RelayCommand]
    private void GoToInstallMelonLoader()
    {
        IsMelonLoaderNotInstalled = false;
        _navigationService.NavigateToSettings();
    }

    public MainViewModel(
        IModManagerService modManagerService,
        ITranslationManagerService translationManagerService,
        IProcessService processService,
        INavigationService navigationService,
        ILoggingService loggingService,
        IServiceProvider serviceProvider,
        ISettingsService settingsService,
        INetworkService networkService,
        IUpdateService updateService,
        IMelonLoaderService melonLoaderService,
        IAnnouncementService announcementService,
        IModCatalogStateService modCatalogStateService)
    {
        _modManagerService = modManagerService;
        _translationManagerService = translationManagerService;
        _processService = processService;
        _navigationService = navigationService;
        _loggingService = loggingService;
        _serviceProvider = serviceProvider;
        _settingsService = settingsService;
        _networkService = networkService;
        _updateService = updateService;
        _melonLoaderService = melonLoaderService;
        _announcementService = announcementService;
        _modCatalogStateService = modCatalogStateService;

        _processService.GameRunningStateChanged += OnGameRunningStateChanged;
        IsGameRunning = _processService.IsGameRunning;
        if (IsGameRunning)
        {
            StatusMessage = Strings.GameRunningCannotOperate;
        }

        // 初始化子ViewModel
        _installedModsViewModel = _serviceProvider.GetRequiredService<InstalledModsViewModel>();
        _modBrowserViewModel = _serviceProvider.GetRequiredService<ModBrowserViewModel>();
        _modDetailViewModel = _serviceProvider.GetRequiredService<ModDetailViewModel>();

        // 订阅子VM事件
        _installedModsViewModel.NavigateToDetailRequested += OnNavigateToDetail;
        _installedModsViewModel.RefreshRequested += async (s, e) => await RefreshDataAsync();
        _installedModsViewModel.CheckForUpdatesRequested += async (s, onlyInstalled) => await CheckForModUpdatesWithScopeAsync(onlyInstalled);
        _installedModsViewModel.NavigateToModBrowserRequested += (s, e) => _navigationService.NavigateToModBrowser();
        _modBrowserViewModel.NavigateToDetailRequested += OnNavigateToDetail;
        _modBrowserViewModel.RefreshRequested += async (s, e) => await RefreshDataAsync();
        _modBrowserViewModel.CheckForUpdatesRequested += async (s, onlyInstalled) => await CheckForModUpdatesWithScopeAsync(onlyInstalled);
        _modBrowserViewModel.NavigateToSettingsRequested += (s, e) => _navigationService.NavigateToSettings();
        _modDetailViewModel.GoBackRequested += OnDetailGoBack;
        _modDetailViewModel.NavigateToModRequested += OnNavigateToMod;
        _modDetailViewModel.RefreshRequested += async (s, e) => await RefreshDataAsync();
        _modDetailViewModel.ConfigureModRequested += async (s, modId) =>
        {
            var mod = Mods.FirstOrDefault(m => m.Id == modId);
            if (mod != null) await ConfigureModAsync(mod);
        };
        _installedModsViewModel.PropertyChanged += OnChildViewModelPropertyChanged;
        _modBrowserViewModel.PropertyChanged += OnChildViewModelPropertyChanged;
        _modDetailViewModel.PropertyChanged += OnChildViewModelPropertyChanged;

        // 初始化导航项
        NavigationItems = new ObservableCollection<NavigationItem>
        {
            new NavigationItem { Label = Strings.NavInstalledMods, Page = NavigationPage.InstalledMods },
            new NavigationItem { Label = Strings.NavModBrowser, Page = NavigationPage.ModBrowser },
            new NavigationItem { Label = Strings.NavTranslation, Page = NavigationPage.Translation },
        };

        // 订阅页面导航事件
        _navigationService.PageNavigationRequested += OnPageNavigationRequested;

        InitializeAsync();

        // 订阅搜索文本变化
        PropertyChanged += (sender, e) =>
        {
            if (e.PropertyName == nameof(SearchText))
                FilterAndSortMods();
        };

        UpdateShellStatus();
    }

    // 导航到指定页面
    private void NavigateToPage(NavigationPage page)
    {
        switch (page)
        {
            case NavigationPage.InstalledMods:
                _installedModsView ??= (UserControl)_serviceProvider.GetRequiredService<InstalledModsView>();
                _installedModsView.DataContext = _installedModsViewModel;
                CurrentPageView = _installedModsView;
                break;
            case NavigationPage.ModBrowser:
                _modBrowserView ??= (UserControl)_serviceProvider.GetRequiredService<ModBrowserView>();
                _modBrowserView.DataContext = _modBrowserViewModel;
                CurrentPageView = _modBrowserView;
                _modBrowserViewModel?.OnNavigatedToBrowser();
                break;
            case NavigationPage.Translation:
                _translationView ??= (UserControl)_serviceProvider.GetRequiredService<TranslationView>();
                _translationView.DataContext = this;
                CurrentPageView = _translationView;
                break;
        }
    }

    /// <summary>
    /// 处理NavigationService发出的页面导航请求
    /// </summary>
    private void OnPageNavigationRequested(object? sender, string pageName)
    {
        var page = pageName switch
        {
            "ModBrowser" => NavigationPage.ModBrowser,
            "InstalledMods" => NavigationPage.InstalledMods,
            "Translation" => NavigationPage.Translation,
            _ => NavigationPage.InstalledMods
        };

        // 直接导航并同步导航栏选中状态
        NavigateToPage(page);
        var navItem = NavigationItems.FirstOrDefault(n => n.Page == page);
        if (navItem != null && SelectedNavigationItem != navItem)
            SelectedNavigationItem = navItem;
    }

    // 导航到MOD详情页（来自已安装或浏览页）
    private void OnNavigateToDetail(object? sender, ModViewModel mod)
    {
        var allMods = Mods.ToList();
        _ = _modDetailViewModel!.InitializeAsync(mod, allMods);

        // 根据来源决定显示哪个View的详情
        if (sender is InstalledModsViewModel)
        {
            _modDetailViewModel.ReturnToPage = NavigationPage.InstalledMods;
            var detailView = _serviceProvider.GetRequiredService<ModDetailView>();
            detailView.DataContext = _modDetailViewModel;
            CurrentPageView = detailView;
        }
        else
        {
            _modDetailViewModel.ReturnToPage = NavigationPage.ModBrowser;
            var detailView = _serviceProvider.GetRequiredService<ModDetailView>();
            detailView.DataContext = _modDetailViewModel;
            CurrentPageView = detailView;
        }
    }

    // 从详情页返回
    private void OnDetailGoBack(object? sender, EventArgs e)
    {
        var returnPage = _modDetailViewModel?.ReturnToPage ?? NavigationPage.InstalledMods;
        // 直接调用NavigateToPage，避免SelectedNavigationItem值未变时不触发changed事件
        NavigateToPage(returnPage);
        // 同步更新左侧导航栏选中状态
        var navItem = NavigationItems.FirstOrDefault(n => n.Page == returnPage);
        if (navItem != null && SelectedNavigationItem != navItem)
            SelectedNavigationItem = navItem;
    }

    // 从详情页跳转到另一个MOD（依赖跳转）
    private void OnNavigateToMod(object? sender, string modId)
    {
        var targetMod = Mods.FirstOrDefault(m => m.Id == modId);
        if (targetMod == null) return;

        var allMods = Mods.ToList();
        _ = _modDetailViewModel!.InitializeAsync(targetMod, allMods);

        var detailView = _serviceProvider.GetRequiredService<ModDetailView>();
        detailView.DataContext = _modDetailViewModel;
        CurrentPageView = detailView;
    }

    private async void InitializeAsync()
    {
        // 防止重复初始化（从设置返回时不应重新初始化）
        if (_isInitialized)
        {
            _loggingService.LogInfo("MainViewModel already initialized, skipping re-initialization");
            return;
        }

        _isInitialized = true;
        _loggingService.LogInfo("MainViewModel initializing for the first time");

        // 检查 MelonLoader 是否被禁用
        var gameRoot = _settingsService.Settings.GameRootPath;
        if (!string.IsNullOrEmpty(gameRoot))
            IsMelonLoaderDisabled = _melonLoaderService.IsMelonLoaderDisabled(gameRoot);

        // 默认导航到已安装页
        var defaultNavItem = NavigationItems.FirstOrDefault(n => n.Page == NavigationPage.InstalledMods);
        if (defaultNavItem != null)
            SelectedNavigationItem = defaultNavItem;
        else
            NavigateToPage(NavigationPage.InstalledMods);

        // 只在非首次运行时自动加载数据（首次运行用户还在配置网络）
        if (!_settingsService.Settings.IsFirstRun)
        {
            await RefreshDataAsync();
            await CheckManagedModIntegrityAsync(promptOnMismatch: false);
        }

        // 检测 MelonLoader 是否已安装（可能在软件关闭期间被用户删除）
        await CheckMelonLoaderInstalledAsync();

        // 首次运行跳过后台检查，避免 API 超限
        if (_settingsService.Settings.IsFirstRun)
            return;

        // 后台检测网络连接,不阻塞UI
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1000); // 延迟1秒,让数据先加载
                await CheckNetworkAndEnterOfflineModeIfNeededAsync();
            }
            catch (Exception ex)
            {
                _loggingService.LogError(ex, "Network check failed in background");
            }
        });

        // Check for app updates silently on first MainViewModel initialization only
        // Subsequent checks should be triggered manually by user in Settings
        bool shouldCheckForUpdates = false;
        lock (_updateCheckLock)
        {
            if (!_hasPerformedStartupUpdateCheck)
            {
                _hasPerformedStartupUpdateCheck = true;
                shouldCheckForUpdates = true;
            }
        }

        if (shouldCheckForUpdates)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(2000);

                    // 优先显示公告
                    await CheckAndShowAnnouncementAsync();

                    await CheckForAppUpdatesSilentlyAsync();

                    // 如果配置了 Token 或开启了代理，检查已安装 Mod 更新
                    if (ShouldPerformStartupModUpdateCheck())
                    {
                        await CheckForInstalledModUpdatesOnStartupAsync();
                    }
                }
                catch (Exception ex)
                {
                    _loggingService.LogWarning(Strings.StartupUpdateCheckError, ex.Message);
                }
            });
        }
    }

    /// <summary>
    /// Silently check for app updates on startup (called from App.xaml.cs)
    /// </summary>
    public async Task CheckForAppUpdatesSilentlyAsync()
    {
        try
        {
            var (hasUpdate, latestVersion, downloadUrl, _, _) = await _updateService.CheckForUpdatesAsync();

            if (hasUpdate && !string.IsNullOrEmpty(latestVersion))
            {
                IsAppUpdateAvailable = true;
                LatestAppVersion = latestVersion;
                LatestAppDownloadUrl = downloadUrl ?? string.Empty;
            }
            else
            {
                IsAppUpdateAvailable = false;
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning(Strings.SilentUpdateCheckFailed, ex.Message);
            // Silently fail - don't bother the user
        }
    }

    /// <summary>
    /// 判断是否应该在启动时检查 Mod 更新
    /// </summary>
    private bool ShouldPerformStartupModUpdateCheck()
    {
        var settings = _settingsService.Settings;
        return !string.IsNullOrEmpty(settings.GitHubApiToken) || settings.UseGitHubProxy;
    }

    /// <summary>
    /// 检查并显示公告
    /// </summary>
    private async Task CheckAndShowAnnouncementAsync()
    {
        try
        {
            var settings = _settingsService.Settings;

            var lang = settings.Language;
            var (content, md5) = await _announcementService.GetAnnouncementAsync(lang, forceRefresh: true);

            if (string.IsNullOrEmpty(content) || string.IsNullOrEmpty(md5)) return;

            // 判断是否为新公告
            var isNewAnnouncement = md5 != settings.LastAnnouncementMd5;

            // 不是新公告，直接返回
            if (!isNewAnnouncement)
            {
                return;
            }

            // 更新MD5
            settings.LastAnnouncementMd5 = md5;
            await _settingsService.SaveSettingsAsync();

            // 显示公告窗口
            await Application.Current.Dispatcher.InvokeAsync(async () =>
            {
                var announcementViewModel = _serviceProvider.GetRequiredService<AnnouncementViewModel>();
                await announcementViewModel.LoadAnnouncementAsync(lang);

                if (announcementViewModel.HasContent)
                {
                    var announcementWindow = new AnnouncementWindow
                    {
                        DataContext = announcementViewModel,
                        WindowStartupLocation = WindowStartupLocation.CenterScreen
                    };

                    if (Application.Current.MainWindow != null)
                    {
                        announcementWindow.Owner = Application.Current.MainWindow;
                        announcementWindow.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                    }

                    announcementWindow.ShowDialog();
                }
            });

            // 窗口关闭后重新加载设置（用户可能勾选了复选框）
            await _settingsService.LoadSettingsAsync();
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, "AnnouncementCheckFailed", ex.Message);
        }
    }

    /// <summary>
    /// 启动时静默检查已安装 Mod 的更新
    /// </summary>
    private async Task CheckForInstalledModUpdatesOnStartupAsync()
    {
        try
        {
            var installedMods = Mods.Where(m => m.IsInstalled && !m.IsManuallyInstalled).ToList();
            if (installedMods.Count == 0) return;

            foreach (var mod in installedMods)
            {
                try
                {
                    var releaseUrl = mod.Config.ReleaseUrl;
                    if (string.IsNullOrEmpty(releaseUrl)) continue;

                    var owner = GetRepoOwner(releaseUrl);
                    var repo = GetRepoName(releaseUrl);
                    if (string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(repo)) continue;

                    var releases = await _networkService.GetGitHubReleasesAsync(owner, repo, forceRefresh: true);
                    if (releases.Count > 0)
                    {
                        var latestVersion = releases[0].TagName?.TrimStart('v') ?? "";
                        var installedVersion = mod.InstalledVersion?.TrimStart('v') ?? "";

                        if (!string.IsNullOrEmpty(latestVersion))
                        {
                            mod.LatestVersion = latestVersion;
                            mod.UpdateDate = releases[0].PublishedAt;
                        }
                    }
                }
                catch
                {
                    // 单个 Mod 检查失败不影响其他 Mod
                }
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning("StartupModUpdateCheckFailed", ex.Message);
        }
    }

    // 从 ReleaseUrl 提取 owner
    private string GetRepoOwner(string repoUrl)
    {
        try
        {
            var segments = new Uri(repoUrl).AbsolutePath.Trim('/').Split('/');
            return segments.Length >= 2 ? segments[1] : "";
        }
        catch { return ""; }
    }

    // 从 ReleaseUrl 提取 repo
    private string GetRepoName(string repoUrl)
    {
        try
        {
            var segments = new Uri(repoUrl).AbsolutePath.Trim('/').Split('/');
            return segments.Length >= 3 ? segments[2] : "";
        }
        catch { return ""; }
    }

    [RelayCommand]
    public async Task RefreshDataAsync()
    {
        try
        {
            HasDeterminateOperationProgress = false;
            OperationProgressValue = 0;
            IsLoading = true;
            StatusMessage = Strings.RefreshingData;

            // 清除会话缓存（非GitHub资源）
            _networkService.ClearSessionCache();

            // Clear any rate limit blocks to allow fresh API requests
            _networkService.ClearRateLimitBlocks();

            // Load mods
            var modList = await _modManagerService.GetModListAsync();

            // 用settings中的语言，避免异步上下文中CultureInfo不可靠
            var lang = _settingsService.Settings.Language;
            foreach (var mod in modList)
                mod.RefreshLocalization(lang);

            Mods.Clear();
            foreach (var mod in modList)
            {
                Mods.Add(mod);
            }

            // Apply filtering and sorting
            FilterAndSortMods();

            // 注入数据到子ViewModel
            _installedModsViewModel?.SetModsSource(Mods);
            _modBrowserViewModel?.SetModsSource(Mods);

            // 检测新增MOD并推送到浏览页
            await DetectAndPushNewModsAsync();

            // 同步新增MOD横幅数据到已安装页
            if (_modBrowserViewModel?.CanShowNewModsBanner == true)
            {
                _installedModsViewModel?.SetNewModsInfo(
                    true,
                    _modBrowserViewModel.NewModsCount,
                    _modBrowserViewModel.NewModsPreview.ToList()
                );
            }
            else
            {
                _installedModsViewModel?.SetNewModsInfo(false, 0, new List<string>());
            }

            // 计算下架Mod和未知字段Mod数量（用于横幅显示）
            DelistedModCount = Mods.Count(m => m.IsInstalled && m.IsDelisted);
            UnknownFieldsModCount = Mods.Count(m => m.HasUnknownConfigFields);
            OnPropertyChanged(nameof(HasDelistedMods));
            OnPropertyChanged(nameof(HasUnknownFieldsMods));

            // 如果当前正在显示详情页，用新数据重新初始化（确保按钮状态正确）
            if (_modDetailViewModel?.Mod != null && CurrentPageView is ModDetailView)
            {
                var updatedMod = Mods.FirstOrDefault(m => m.Id == _modDetailViewModel.Mod.Id);
                if (updatedMod != null)
                    _ = _modDetailViewModel.InitializeAsync(updatedMod, Mods.ToList());
            }

            // Check translation status
            IsTranslationInstalled = await _translationManagerService.IsTranslationInstalledAsync();
            IsTranslationManuallyInstalled = await _translationManagerService.IsTranslationManuallyInstalledAsync();
            IsTranslationPluginEnabled = await _translationManagerService.IsTranslationPluginEnabledAsync();

            // 翻译插件更新：7天缓存
            IsTranslationPluginUpdateAvailable = await _translationManagerService.IsXUnityUpdateAvailableAsync();
            // 翻译资源更新：配置了Token或代理时强制刷新，否则使用缓存
            var shouldForceRefreshTranslation = ShouldPerformStartupModUpdateCheck();
            IsTranslationResourceUpdateAvailable = await _translationManagerService.IsTranslationUpdateAvailableAsync(forceRefresh: shouldForceRefreshTranslation);
            IsTranslationUpdateAvailable = IsTranslationPluginUpdateAvailable || IsTranslationResourceUpdateAvailable;

            // 获取最新XUnity版本（使用默认缓存行为）
            LatestXUnityVersion = await _translationManagerService.GetLatestXUnityVersionAsync();

            if (IsTranslationInstalled)
            {
                AvailableLanguages = await _translationManagerService.GetAvailableLanguagesAsync();
                CurrentTranslationLanguage = await _translationManagerService.GetCurrentLanguageAsync();
            }

            StatusMessage = Strings.DataRefreshComplete;
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.DataRefreshError);
            StatusMessage = Strings.DataRefreshFailed;
        }
        finally
        {
            IsLoading = false;
        }
    }

    // ModBrowserView.xaml
    /// <summary>
    /// 计算当前有效的MOD ID集合，与上次快照对比检测新增MOD
    /// </summary>
    private async Task DetectAndPushNewModsAsync()
    {
        if (_modBrowserViewModel == null) return;

        // 加载上一次的状态快照
        var previousState = await _modCatalogStateService.LoadAsync();

        // 计算当前目录的有效MOD ID（排除已下架Mod和不受支持的手动Mod）
        var validMods = Mods.Where(m => !m.IsDelisted && !(m.IsManuallyInstalled && m.IsUnsupportedManualMod)).ToList();
        var currentIds = validMods.Select(m => m.Id).ToHashSet();

        if (previousState == null || previousState.ModIds.Count == 0)
        {
            // 首次基线或快照不可用，直接保存当前状态，不弹横幅
            await _modCatalogStateService.SaveAsync(currentIds);
            _modBrowserViewModel.ClearNewMods();
            return;
        }

        // 计算差集：当前有但基线没有的 = 新增MOD
        var baselineIds = previousState.ModIds.ToHashSet();
        var newIds = currentIds.Where(id => !baselineIds.Contains(id)).ToList();

        if (newIds.Count > 0)
        {
            var newMods = validMods.Where(m => newIds.Contains(m.Id)).ToList();
            _modBrowserViewModel.SetNewMods(newMods);
        }
        else
        {
            _modBrowserViewModel.ClearNewMods();
        }

        // 用当前目录覆盖快照，供下次启动使用
        await _modCatalogStateService.SaveAsync(currentIds);
    }

    [RelayCommand]
    private async Task InstallModAsync(ModViewModel mod)
    {
        if (mod.LatestVersion == null || mod.LatestVersion == "Unknown")
        {
            MessageDialogHelper.ShowError(Strings.CannotGetModVersionInfo, Strings.Error);
            return;
        }

        // 检查管理器版本要求
        if (!string.IsNullOrEmpty(mod.Config.RequiredManagerVersion))
        {
            if (!_updateService.MeetsRequiredVersion(mod.Config.RequiredManagerVersion))
            {
                var currentVersion = _updateService.GetCurrentVersion();
                var goToUpdate = MessageDialogHelper.ShowGoToSettingsCancel(
                    string.Format(Strings.ManagerVersionRequirementMessage, mod.Config.RequiredManagerVersion, currentVersion),
                    Strings.ManagerVersionRequirementTitle);
                if (goToUpdate)
                {
                    _navigationService.NavigateToSettings();
                }
                return;
            }
        }

        if (!await ModInstallCompatibilityHelper.ConfirmInstallAsync(mod, null, _settingsService, _melonLoaderService))
            return;

        try
        {
            IsDownloading = true;
            HasDeterminateOperationProgress = false;
            OperationProgressValue = 0;
            StatusMessage = string.Format(Strings.Installing, mod.DisplayName);
            
            var progress = new Progress<DownloadProgress>(downloadProgress =>
            {
                var speedText = downloadProgress.GetFormattedSpeed();
                var progressText = downloadProgress.GetFormattedProgress();
                var percentage = downloadProgress.ProgressPercentage;
                HasDeterminateOperationProgress = true;
                OperationProgressValue = percentage;
                StatusMessage = $"{string.Format(Strings.Installing, mod.DisplayName)} - {percentage:F1}% ({progressText}) - {speedText}";
            });
            
            var success = await _modManagerService.InstallModAsync(mod.Config, mod.LatestVersion, progress);

            if (success)
            {
                StatusMessage = string.Format(Strings.InstallSuccessful, mod.DisplayName);
                await RefreshDataAsync();
            }
            else
            {
                StatusMessage = string.Format(Strings.InstallFailed, mod.DisplayName);
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.ModInstallError, mod.Id);
            StatusMessage = string.Format(Strings.InstallFailed, mod.DisplayName);
        }
        finally
        {
            HasDeterminateOperationProgress = false;
            OperationProgressValue = 0;
            IsDownloading = false;
        }
    }

    [RelayCommand]
    private async Task UninstallModAsync(ModViewModel mod)
    {
        var result = MessageDialogHelper.Confirm(
            string.Format(Strings.ConfirmUninstallMod, mod.DisplayName),
            Strings.ConfirmUninstall);

        if (!result) return;

        try
        {
            StatusMessage = string.Format(Strings.Uninstalling, mod.DisplayName);

            if (mod.IsManuallyInstalled)
            {
                // Show warning for manual mods with option to proceed
                var manualUninstallResult = MessageDialogHelper.Confirm(
                    string.Format(Strings.ManualModUninstallWarning, mod.DisplayName),
                    Strings.ManualModUninstall);

                if (!manualUninstallResult) return;
                
                // Use manual uninstall method
                var success = await _modManagerService.UninstallManualModAsync(mod.Id);
                
                if (success)
                {
                    StatusMessage = string.Format(Strings.ModUninstalledSuccessfully, mod.DisplayName);
                    await RefreshDataAsync();
                }
                else
                {
                    StatusMessage = string.Format(Strings.UninstallFailed, mod.DisplayName);
                }
                return;
            }

            var normalSuccess = await _modManagerService.UninstallModAsync(mod.Id);

            if (normalSuccess)
            {
                StatusMessage = string.Format(Strings.ModUninstalledSuccessfully, mod.DisplayName);
                await RefreshDataAsync();
            }
            else
            {
                StatusMessage = string.Format(Strings.UninstallFailed, mod.DisplayName);
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.ModUninstallError, mod.Id);
            StatusMessage = string.Format(Strings.UninstallFailed, mod.DisplayName);
        }
    }

    [RelayCommand]
    private async Task ToggleModAsync(ModViewModel mod)
    {
        try
        {
            bool success;
            if (mod.IsEnabled)
            {
                success = await _modManagerService.DisableModAsync(mod.Id);
                StatusMessage = success ? string.Format(Strings.ModDisabledStatus, mod.DisplayName) : string.Format(Strings.ModDisableFailed, mod.DisplayName);
            }
            else
            {
                success = await _modManagerService.EnableModAsync(mod.Id);
                StatusMessage = success ? string.Format(Strings.ModEnabledStatus, mod.DisplayName) : string.Format(Strings.ModEnableFailed, mod.DisplayName);
            }

            if (success)
            {
                await RefreshDataAsync();
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.ModToggleError, mod.Id);
            StatusMessage = string.Format(Strings.StatusToggleFailed, mod.DisplayName);
        }
    }

    [RelayCommand]
    private async Task ConfigureModAsync(ModViewModel mod)
    {
        try
        {
            var configViewModel = _serviceProvider.GetRequiredService<ModConfigurationViewModel>();
            var configWindow = new ModConfigurationWindow
            {
                DataContext = configViewModel,
                Owner = Application.Current.MainWindow
            };

            configViewModel.OwnerWindow = configWindow;
            await configViewModel.InitializeAsync(mod.Id, mod.DisplayName);

            configWindow.ShowDialog();
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.ConfigureModError, mod.Id);
            StatusMessage = string.Format(Strings.OpenConfigWindowFailed, mod.DisplayName);
        }
    }

    [RelayCommand]
    private async Task UpdateModAsync(ModViewModel mod)
    {
        if (!mod.CanUpdate)
        {
            _loggingService.LogWarning(Strings.CannotUpdateModCanUpdateFalse, mod.Id);
            return;
        }

        try
        {
            IsDownloading = true;
            HasDeterminateOperationProgress = false;
            OperationProgressValue = 0;
            StatusMessage = string.Format(Strings.UpdatingMod, mod.DisplayName, mod.InstalledVersion, mod.LatestVersion);
            
            var progress = new Progress<DownloadProgress>(downloadProgress =>
            {
                var speedText = downloadProgress.GetFormattedSpeed();
                var progressText = downloadProgress.GetFormattedProgress();
                var percentage = downloadProgress.ProgressPercentage;
                HasDeterminateOperationProgress = true;
                OperationProgressValue = percentage;
                StatusMessage = $"{string.Format(Strings.UpdatingMod, mod.DisplayName, mod.InstalledVersion, mod.LatestVersion)} - {percentage:F1}% ({progressText}) - {speedText}";
            });
            
            var success = await _modManagerService.UpdateModAsync(mod.Id, mod.LatestVersion!, progress);

            if (success)
            {
                StatusMessage = string.Format(Strings.ModUpdatedSuccessfully, mod.DisplayName);
                await RefreshDataAsync();
            }
            else
            {
                StatusMessage = string.Format(Strings.ModUpdateFailed, mod.DisplayName);
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.ModUpdateFailed, mod.Id);
            StatusMessage = string.Format(Strings.ModUpdateFailed, mod.DisplayName);
        }
        finally
        {
            HasDeterminateOperationProgress = false;
            OperationProgressValue = 0;
            IsDownloading = false;
        }
    }

    [RelayCommand]
    private async Task ReinstallModAsync(ModViewModel mod)
    {
        if (!mod.CanReinstall)
        {
            _loggingService.LogWarning(Strings.CannotReinstallModCanReinstallFalse, mod.Id);
            return;
        }

        if (mod.LatestVersion == null || mod.LatestVersion == "Unknown")
        {
            MessageDialogHelper.ShowError(Strings.CannotGetModVersionInfo, Strings.Error);
            return;
        }

        // 检查管理器版本要求
        if (!string.IsNullOrEmpty(mod.Config.RequiredManagerVersion))
        {
            if (!_updateService.MeetsRequiredVersion(mod.Config.RequiredManagerVersion))
            {
                var currentVersion = _updateService.GetCurrentVersion();
                var goToUpdate = MessageDialogHelper.ShowGoToSettingsCancel(
                    string.Format(Strings.ManagerVersionRequirementMessage, mod.Config.RequiredManagerVersion, currentVersion),
                    Strings.ManagerVersionRequirementTitle);
                if (goToUpdate)
                {
                    _navigationService.NavigateToSettings();
                }
                return;
            }
        }

        if (!await ModInstallCompatibilityHelper.ConfirmInstallAsync(mod, null, _settingsService, _melonLoaderService))
            return;

        try
        {
            IsDownloading = true;
            HasDeterminateOperationProgress = false;
            OperationProgressValue = 0;
            StatusMessage = string.Format(Strings.Installing, mod.DisplayName);

            var progress = new Progress<DownloadProgress>(downloadProgress =>
            {
                var speedText = downloadProgress.GetFormattedSpeed();
                var progressText = downloadProgress.GetFormattedProgress();
                var percentage = downloadProgress.ProgressPercentage;
                HasDeterminateOperationProgress = true;
                OperationProgressValue = percentage;
                StatusMessage = $"{string.Format(Strings.Installing, mod.DisplayName)} - {percentage:F1}% ({progressText}) - {speedText}";
            });

            // Reinstall the mod using the regular installation method
            // This will convert it from manually installed to managed
            var success = await _modManagerService.InstallModAsync(mod.Config, mod.LatestVersion, progress, skipDependencyCheck: true);

            if (success)
            {
                StatusMessage = string.Format(Strings.InstallSuccessful, mod.DisplayName);
                await RefreshDataAsync();
            }
            else
            {
                StatusMessage = string.Format(Strings.InstallFailed, mod.DisplayName);
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.ModInstallError, mod.Id);
            StatusMessage = string.Format(Strings.InstallFailed, mod.DisplayName);
        }
        finally
        {
            HasDeterminateOperationProgress = false;
            OperationProgressValue = 0;
            IsDownloading = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanExecuteModOperations))]
    private async Task InstallTranslationAsync()
    {
        try
        {
            // Show version selection dialog (simplified for now)
            var releases = await _translationManagerService.GetXUnityReleasesAsync();
            var latestVersion = releases.FirstOrDefault()?.TagName;

            if (string.IsNullOrEmpty(latestVersion))
            {
                MessageDialogHelper.ShowError(Strings.CannotGetTranslationVersions, Strings.Error);
                return;
            }

            IsDownloading = true;
            HasDeterminateOperationProgress = false;
            OperationProgressValue = 0;
            StatusMessage = Strings.InstallingTranslationSystem;
            
            var progress = new Progress<DownloadProgress>(downloadProgress =>
            {
                var speedText = downloadProgress.GetFormattedSpeed();
                var progressText = downloadProgress.GetFormattedProgress();
                var percentage = downloadProgress.ProgressPercentage;
                HasDeterminateOperationProgress = true;
                OperationProgressValue = percentage;
                StatusMessage = $"{Strings.InstallingTranslationSystem} - {percentage:F1}% ({progressText}) - {speedText}";
            });
            
            var success = await _translationManagerService.InstallTranslationAsync(latestVersion, progress);

            if (success)
            {
                StatusMessage = Strings.TranslationInstalled;
                await RefreshDataAsync();
            }
            else
            {
                StatusMessage = Strings.TranslationSystemInstallFailed;
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.TranslationInstallError);
            StatusMessage = Strings.TranslationSystemInstallFailed;
        }
        finally
        {
            HasDeterminateOperationProgress = false;
            OperationProgressValue = 0;
            IsDownloading = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanExecuteModOperations))]
    private async Task UpdateTranslationAsync()
    {
        // Check if translation was manually installed
        if (IsTranslationManuallyInstalled)
        {
            MessageDialogHelper.ShowWarning(
                Strings.CannotUpdateManuallyInstalledTranslation,
                Strings.Error);
            return;
        }

        try
        {
            IsTranslationUpdating = true;
            HasDeterminateOperationProgress = false;
            OperationProgressValue = 0;
            StatusMessage = Strings.UpdatingTranslationFiles;

            var progress = new Progress<DownloadProgress>(downloadProgress =>
            {
                var speedText = downloadProgress.GetFormattedSpeed();
                var progressText = downloadProgress.GetFormattedProgress();
                var percentage = downloadProgress.ProgressPercentage;
                HasDeterminateOperationProgress = true;
                OperationProgressValue = percentage;
                StatusMessage = $"{Strings.UpdatingTranslationFiles} - {percentage:F1}% ({progressText}) - {speedText}";
            });

            var success = await _translationManagerService.UpdateTranslationFilesAsync(progress);

            if (success)
            {
                StatusMessage = Strings.TranslationFilesUpdated;
                await RefreshDataAsync();

                // 更新完成后重新检查更新状态
                IsTranslationResourceUpdateAvailable = await _translationManagerService.IsTranslationUpdateAvailableAsync();
                IsTranslationUpdateAvailable = IsTranslationPluginUpdateAvailable || IsTranslationResourceUpdateAvailable;
            }
            else
            {
                StatusMessage = Strings.TranslationFilesUpdateFailed;
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.TranslationUpdateError);
            StatusMessage = Strings.TranslationFilesUpdateFailed;
        }
        finally
        {
            HasDeterminateOperationProgress = false;
            OperationProgressValue = 0;
            IsTranslationUpdating = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanExecuteModOperations))]
    private async Task UpdateTranslationPluginAsync()
    {
        // Check if translation was manually installed
        if (IsTranslationManuallyInstalled)
        {
            MessageDialogHelper.ShowWarning(
                Strings.CannotUpdateManuallyInstalledTranslation,
                Strings.Error);
            return;
        }

        try
        {
            IsTranslationUpdating = true;
            HasDeterminateOperationProgress = false;
            OperationProgressValue = 0;
            StatusMessage = Strings.UpdatingTranslationPlugin;

            var progress = new Progress<DownloadProgress>(downloadProgress =>
            {
                var speedText = downloadProgress.GetFormattedSpeed();
                var progressText = downloadProgress.GetFormattedProgress();
                var percentage = downloadProgress.ProgressPercentage;
                HasDeterminateOperationProgress = true;
                OperationProgressValue = percentage;
                StatusMessage = $"{Strings.UpdatingTranslationPlugin} - {percentage:F1}% ({progressText}) - {speedText}";
            });

            var success = await _translationManagerService.UpdateXUnityPluginAsync(progress);

            if (success)
            {
                StatusMessage = Strings.TranslationPluginUpdateSuccessful;
                await RefreshDataAsync();

                // 更新完成后重新检查更新状态
                IsTranslationPluginUpdateAvailable = await _translationManagerService.IsXUnityUpdateAvailableAsync();
                IsTranslationUpdateAvailable = IsTranslationPluginUpdateAvailable || IsTranslationResourceUpdateAvailable;
            }
            else
            {
                StatusMessage = Strings.TranslationPluginUpdateFailed;
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.TranslationPluginUpdateFailed);
            StatusMessage = Strings.TranslationPluginUpdateFailed;
        }
        finally
        {
            HasDeterminateOperationProgress = false;
            OperationProgressValue = 0;
            IsTranslationUpdating = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanExecuteModOperations))]
    private async Task UninstallTranslationAsync()
    {
        // Check if translation was manually installed
        if (IsTranslationManuallyInstalled)
        {
            MessageDialogHelper.ShowWarning(
                Strings.CannotUninstallManuallyInstalledTranslation,
                Strings.Error);
            return;
        }

        var result = MessageDialogHelper.Confirm(
            Strings.ConfirmUninstallTranslation,
            Strings.ConfirmUninstall);

        if (!result) return;

        try
        {
            StatusMessage = Strings.UninstallingTranslationSystem;
            var success = await _translationManagerService.UninstallTranslationAsync();

            if (success)
            {
                StatusMessage = Strings.TranslationUninstalled;
                await RefreshDataAsync();
            }
            else
            {
                StatusMessage = Strings.TranslationSystemUninstallFailed;
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.TranslationUninstallError);
            StatusMessage = Strings.TranslationSystemUninstallFailed;
        }
    }

    [RelayCommand(CanExecute = nameof(CanExecuteModOperations))]
    private async Task SetTranslationLanguageAsync(string language)
    {
        try
        {
            var success = await _translationManagerService.SetLanguageAsync(language);
            if (success)
            {
                CurrentTranslationLanguage = language;
                StatusMessage = string.Format(Strings.TranslationLanguageSetTo, language);
            }
            else
            {
                StatusMessage = Strings.TranslationLanguageSetFailed;
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.TranslationLanguageSetError, language);
            StatusMessage = Strings.TranslationLanguageSetFailed;
        }
    }

    [RelayCommand(CanExecute = nameof(CanExecuteModOperations))]
    private async Task ToggleTranslationPluginAsync()
    {
        try
        {
            if (IsTranslationPluginEnabled)
            {
                StatusMessage = Strings.DisableTranslationPlugin;
                var success = await _translationManagerService.SetTranslationPluginEnabledAsync(false);
                if (success)
                {
                    StatusMessage = Strings.TranslationPluginDisabled;
                    await RefreshDataAsync();
                }
                else
                {
                    StatusMessage = Strings.TranslationPluginToggleFailed;
                }
            }
            else
            {
                StatusMessage = Strings.EnableTranslationPlugin;
                var success = await _translationManagerService.SetTranslationPluginEnabledAsync(true);
                if (success)
                {
                    StatusMessage = Strings.TranslationPluginEnabled;
                    await RefreshDataAsync();
                }
                else
                {
                    StatusMessage = Strings.TranslationPluginToggleFailed;
                }
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.TranslationPluginToggleError, ex.Message);
            StatusMessage = Strings.TranslationPluginToggleFailed;
        }
    }

    [RelayCommand(CanExecute = nameof(CanExecuteModOperations))]
    private async Task LaunchGameAsync()
    {
        try
        {
            var gameRootPath = _settingsService.Settings.GameRootPath;
            if (string.IsNullOrEmpty(gameRootPath))
            {
                MessageDialogHelper.ShowError(Strings.GamePathNotSet, Strings.Error);
                return;
            }

            StatusMessage = Strings.CheckingModConflictsAndDependencies;

            // 检查MOD冲突
            if (!_settingsService.Settings.SkipConflictCheck)
            {
                var (hasConflicts, conflicts) = await _modManagerService.CheckEnabledModsConflictsAsync();
                if (hasConflicts)
            {
                var conflictMessages = conflicts.Select(c =>
                    string.Format(Strings.ModConflictsWith,
                        GetModDisplayName(c.ModId),
                        GetModDisplayName(c.ConflictsWith)))
                    .ToList();

                var conflictText = string.Join("\n", conflictMessages);
                var result = MessageDialogHelper.Confirm(
                    string.Format(Strings.ConflictWarningMessage, conflictText),
                    Strings.ConflictDetectedOnLaunch);

                if (!result)
                {
                    StatusMessage = Strings.Ready;
                    return;
                }
            }
            }

            // 检查MOD依赖
            var (hasMissingDeps, missingDeps) = await _modManagerService.CheckEnabledModsDependenciesAsync();
            if (hasMissingDeps)
            {
                var depMessages = missingDeps.Select(d =>
                    string.Format(Strings.ModRequiresDependency,
                        GetModDisplayName(d.ModId),
                        GetModDisplayName(d.RequiredMod)))
                    .ToList();

                var depText = string.Join("\n", depMessages);
                var result = MessageDialogHelper.Confirm(
                    string.Format(Strings.DependencyWarningMessage, depText),
                    Strings.DependencyMissingOnLaunch);

                if (!result)
                {
                    StatusMessage = Strings.Ready;
                    return;
                }
            }

            // 检查已安装的下架Mod
            var delistedMods = Mods.Where(m => m.IsInstalled && m.IsDelisted).ToList();
            if (delistedMods.Any())
            {
                var delistedNames = delistedMods.Select(m => m.DisplayName).ToList();
                var delistedText = string.Join("\n• ", delistedNames);
                var result = MessageDialogHelper.Confirm(
                    string.Format(Strings.DelistedModWarningMessage, "• " + delistedText),
                    Strings.DelistedModDetectedOnLaunch);

                if (!result)
                {
                    StatusMessage = Strings.Ready;
                    return;
                }
            }

            if (!_settingsService.Settings.SkipIntegrityCheck)
            {
                var integrityOkay = await CheckManagedModIntegrityAsync(promptOnMismatch: true);
                if (!integrityOkay)
                {
                    StatusMessage = Strings.Ready;
                    return;
                }
            }

            StatusMessage = Strings.StartingGame;
            var success = await _processService.LaunchGameAsync(gameRootPath);

            if (success)
            {
                StatusMessage = Strings.GameStartedSuccessfully;
            }
            else
            {
                StatusMessage = Strings.GameLaunchFailed;
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.LaunchGameError);
            StatusMessage = Strings.GameLaunchFailed;
        }
    }

    private async Task<bool> CheckManagedModIntegrityAsync(bool promptOnMismatch)
    {
        if (!promptOnMismatch && _hasPerformedStartupIntegrityCheck)
            return true;

        var issues = await _modManagerService.CheckManagedModsIntegrityAsync();
        if (!promptOnMismatch)
            _hasPerformedStartupIntegrityCheck = true;

        if (!issues.Any())
            return true;

        var groupedIssues = issues
            .GroupBy(issue => issue.ModDisplayName)
            .Select(group =>
            {
                var lines = group.Select(issue =>
                {
                    var status = issue.IssueType == ModIntegrityIssueType.Missing
                        ? (Strings.ResourceManager.GetString("ManagedModIntegrityIssueMissing", Strings.Culture) ??
                           (_settingsService.Settings.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "缺失" : "Missing"))
                        : (Strings.ResourceManager.GetString("ManagedModIntegrityIssueModified", Strings.Culture) ??
                           (_settingsService.Settings.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "已修改" : "Modified"));
                    return $"  - {issue.RelativePath} [{status}]";
                });
                return $"{group.Key}\n{string.Join("\n", lines)}";
            })
            .ToList();

        var issueText = string.Join("\n\n", groupedIssues);
        var messageTemplate = promptOnMismatch
            ? (Strings.ResourceManager.GetString("ManagedModIntegrityLaunchMessage", Strings.Culture) ??
               (_settingsService.Settings.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
                    ? "检测到已安装受管 Mod 的文件完整性异常：\n\n{0}\n\n建议先修复或重装这些 Mod。仍要继续启动游戏吗？"
                    : "Detected integrity issues in installed managed mods:\n\n{0}\n\nFixing or reinstalling these mods is recommended. Continue launching the game anyway?"))
            : (Strings.ResourceManager.GetString("ManagedModIntegrityStartupMessage", Strings.Culture) ??
               (_settingsService.Settings.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
                    ? "检测到已安装受管 Mod 的文件完整性异常：\n\n{0}\n\n建议修复或重装这些 Mod。"
                    : "Detected integrity issues in installed managed mods:\n\n{0}\n\nFixing or reinstalling these mods is recommended."));
        var message = string.Format(messageTemplate, issueText);

        if (promptOnMismatch)
        {
            return MessageDialogHelper.Confirm(message, Strings.Warning);
        }
        else
        {
            MessageDialogHelper.ShowWarning(message, Strings.Warning);
            return true;
        }
    }

    /// <summary>
    /// 获取MOD的显示名称
    /// </summary>
    private string GetModDisplayName(string modId)
    {
        var mod = Mods.FirstOrDefault(m => m.Id == modId);
        return mod?.DisplayName ?? modId;
    }

    [RelayCommand(CanExecute = nameof(CanExecuteStopGame))]
    private async Task StopGameAsync()
    {
        try
        {
            StatusMessage = Strings.StoppingGame;
            var success = await _processService.StopGameAsync();

            if (success)
            {
                StatusMessage = Strings.GameStoppedSuccessfully;
            }
            else
            {
                StatusMessage = Strings.GameStopFailed;
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.StopGameError);
            StatusMessage = Strings.GameStopFailed;
        }
    }

    private bool CanExecuteStopGame()
    {
        // 只有游戏正在运行时才能停止
        return IsGameRunning;
    }

    [RelayCommand]
    private void OpenGameFolder()
    {
        var gameRootPath = _settingsService.Settings.GameRootPath;
        if (string.IsNullOrEmpty(gameRootPath) || !System.IO.Directory.Exists(gameRootPath))
        {
            MessageDialogHelper.ShowError(Strings.GamePathNotSet, Strings.Error);
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = gameRootPath,
                UseShellExecute = true,
                Verb = "open"
            });
            _loggingService.LogInfo(Strings.OpenedGameRootDirectory, gameRootPath);
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.FailedToOpenGameDirectory);
            MessageDialogHelper.ShowError(string.Format(Strings.UnableToOpenGameDirectory, ex.Message), Strings.Error);
        }
    }

    [RelayCommand(CanExecute = nameof(CanExecuteModOperations))]
    private void OpenSettings()
    {
        _navigationService.NavigateToSettings();
    }

    [RelayCommand]
    private void OpenModGitHubPage(ModViewModel mod)
    {
        if (mod?.GitHubRepositoryUrl == null)
        {
            _loggingService.LogWarning(Strings.NoGitHubUrlAvailable, mod?.DisplayName ?? "Unknown");
            return;
        }

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = mod.GitHubRepositoryUrl,
                UseShellExecute = true
            };
            System.Diagnostics.Process.Start(psi);
            _loggingService.LogInfo(Strings.OpenedGitHubPage, mod.DisplayName, mod.GitHubRepositoryUrl);
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.FailedToOpenGitHubPage, mod.DisplayName);
            MessageDialogHelper.ShowError(string.Format(Strings.UnableToOpenGitHubPage, ex.Message), Strings.Error);
        }
    }

    [RelayCommand]
    private void OpenGameRootDirectory()
    {
        try
        {
            var gameRootPath = _settingsService.Settings.GameRootPath;
            
            if (string.IsNullOrEmpty(gameRootPath) || !System.IO.Directory.Exists(gameRootPath))
            {
                MessageDialogHelper.ShowError(Strings.GameDirectoryNotSetOrNotFound, Strings.Error);
                return;
            }

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = gameRootPath,
                UseShellExecute = true
            };
            System.Diagnostics.Process.Start(psi);
            _loggingService.LogInfo(Strings.OpenedGameRootDirectory, gameRootPath);
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.FailedToOpenGameDirectory);
            MessageDialogHelper.ShowError(string.Format(Strings.UnableToOpenGameDirectory, ex.Message), Strings.Error);
        }
    }

    [RelayCommand]
    private void GoToInstalledMods()
    {
        _navigationService.NavigateToInstalledMods();
    }

    [RelayCommand]
    private void GoToTranslationManagement()
    {
        _navigationService.NavigateToTranslation();
    }

    [RelayCommand]
    private async Task CheckForTranslationUpdatesAsync()
    {
        try
        {
            StatusMessage = Strings.CheckingTranslationUpdates;
            
            // Check if translation is installed first
            if (!await _translationManagerService.IsTranslationInstalledAsync())
            {
                StatusMessage = Strings.TranslationSystemNotInstalled;
                return;
            }

            IsTranslationUpdateAvailable = await _translationManagerService.IsTranslationUpdateAvailableAsync();
            
            StatusMessage = IsTranslationUpdateAvailable 
                ? Strings.TranslationUpdateAvailable
                : Strings.NoTranslationUpdatesAvailable;
                
            _loggingService.LogInfo(StatusMessage);
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.TranslationUpdateCheckError);
            StatusMessage = Strings.TranslationUpdateCheckError;
        }
    }

    // 子VM触发时指定检查范围
    private async Task CheckForModUpdatesWithScopeAsync(bool onlyInstalled)
    {
        var prev = OnlyCheckInstalledMods;
        OnlyCheckInstalledMods = onlyInstalled;
        try { await CheckForModUpdatesAsync(); }
        finally { OnlyCheckInstalledMods = prev; }
    }

    [RelayCommand(CanExecute = nameof(CanExecuteCheckUpdates))]
    private async Task CheckForModUpdatesAsync()
    {
        try
        {
            IsCheckingUpdates = true;
            IsLoading = true;
            StatusMessage = Strings.CheckingForModUpdates;

            var modsToCheck = OnlyCheckInstalledMods
                ? Mods.Where(m => m.IsInstalled && !(m.IsManuallyInstalled && m.IsUnsupportedManualMod)).ToList()
                : Mods.Where(m => !(m.IsManuallyInstalled && m.IsUnsupportedManualMod)).ToList();

            if (modsToCheck.Count == 0)
            {
                StatusMessage = Strings.NoModsToCheck;
                return;
            }

            // Clear any rate limit blocks to allow fresh API requests
            _networkService.ClearRateLimitBlocks();

            int updatesFound = 0;
            var updatedMods = new List<string>();

            // 并行检查所有Mod的版本更新
            var updateTasks = modsToCheck
                .Where(m => m.Config != null && !string.IsNullOrEmpty(m.Config.ReleaseUrl))
                .Select(async mod =>
                {
                    try
                    {
                        var releases = await _networkService.GetGitHubReleasesAsync(
                            GetRepoOwnerFromApiUrl(mod.Config.ReleaseUrl),
                            GetRepoNameFromApiUrl(mod.Config.ReleaseUrl),
                            forceRefresh: true
                        );

                        var latestVersion = releases.FirstOrDefault()?.TagName;

                        if (!string.IsNullOrEmpty(latestVersion))
                        {
                            mod.LatestVersion = latestVersion;

                            if (mod.IsInstalled &&
                                !string.IsNullOrEmpty(mod.InstalledVersion) &&
                                mod.InstalledVersion != latestVersion &&
                                mod.InstalledVersion != Strings.Manual)
                            {
                                lock (updatedMods)
                                {
                                    updatesFound++;
                                    updatedMods.Add($"{mod.DisplayName}: {mod.InstalledVersion} → {latestVersion}");
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _loggingService.LogWarning(Strings.FailedToCheckModUpdate, mod.DisplayName, ex.Message);
                    }
                }).ToList();

            await Task.WhenAll(updateTasks);

            // Apply filtering and sorting to refresh the view
            FilterAndSortMods();

            StatusMessage = updatesFound > 0
                ? string.Format(Strings.ModUpdatesFound, updatesFound)
                : Strings.NoModUpdatesAvailable;

            _loggingService.LogInfo(Strings.ModUpdateCheckComplete);

            // 同时检查翻译更新
            await CheckTranslationUpdatesInModUpdateFlowAsync();

            if (updatesFound > 0)
            {
                var updatesList = string.Join("\n", updatedMods);
                MessageDialogHelper.ShowInformation($"{string.Format(Strings.ModUpdatesFound, updatesFound)}\n\n{updatesList}",
                    Strings.Information);
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.ModUpdateCheckFailed);
            StatusMessage = Strings.ModUpdateCheckFailed;
        }
        finally
        {
            IsLoading = false;
            IsCheckingUpdates = false;
        }
    }

    private async Task CheckTranslationUpdatesInModUpdateFlowAsync()
    {
        try
        {
            // 只检查翻译更新，不显示消息
            if (await _translationManagerService.IsTranslationInstalledAsync())
            {
                // 分别检查翻译插件和资源更新（强制刷新缓存）
                IsTranslationPluginUpdateAvailable = await _translationManagerService.IsXUnityUpdateAvailableAsync(forceRefresh: true);
                IsTranslationResourceUpdateAvailable = await _translationManagerService.IsTranslationUpdateAvailableAsync(forceRefresh: true);
                IsTranslationUpdateAvailable = IsTranslationPluginUpdateAvailable || IsTranslationResourceUpdateAvailable;

                // 获取最新XUnity版本（强制刷新缓存）
                LatestXUnityVersion = await _translationManagerService.GetLatestXUnityVersionAsync(forceRefresh: true);

                if (IsTranslationUpdateAvailable)
                {
                    var updateTypes = new List<string>();
                    if (IsTranslationPluginUpdateAvailable) updateTypes.Add("plugin");
                    if (IsTranslationResourceUpdateAvailable) updateTypes.Add("resources");

                    _loggingService.LogInfo(Strings.TranslationUpdatesFound, string.Join(", ", updateTypes));
                }
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning(Strings.TranslationUpdateCheckInModFlowFailed, ex.Message);
        }
    }

    // 从GitHub API URL提取owner（与ModManagerService中的实现一致）
    private string GetRepoOwnerFromApiUrl(string apiUrl)
    {
        // https://api.github.com/repos/owner/repo/releases/latest
        var segments = new Uri(apiUrl).AbsolutePath.Trim('/').Split('/');
        return segments.Length >= 2 ? segments[1] : "";
    }

    // 从GitHub API URL提取repo name（与ModManagerService中的实现一致）
    private string GetRepoNameFromApiUrl(string apiUrl)
    {
        // https://api.github.com/repos/owner/repo/releases/latest
        var segments = new Uri(apiUrl).AbsolutePath.Trim('/').Split('/');
        return segments.Length >= 3 ? segments[2] : "";
    }

    [RelayCommand]
    private async Task CheckForAppUpdatesAsync()
    {
        try
        {
            StatusMessage = Strings.CheckingForApplicationUpdates;
            _loggingService.LogInfo(Strings.CheckingForApplicationUpdates);

            var currentVersion = _updateService.GetCurrentVersion();
            var (hasUpdate, latestVersion, downloadUrl, expectedSize, expectedDigest) = await _updateService.CheckForUpdatesAsync();

            if (hasUpdate && !string.IsNullOrEmpty(latestVersion) && !string.IsNullOrEmpty(downloadUrl))
            {
                var result = MessageDialogHelper.Confirm(
                    $"New version available: {latestVersion}\nCurrent version: {currentVersion}\n\nDo you want to download and install the update?",
                    "Update Available");

                if (result)
                {
                    IsDownloading = true;
                    StatusMessage = $"Downloading update {latestVersion}...";

                    var progress = new Progress<DownloadProgress>(downloadProgress =>
                    {
                        var speedText = downloadProgress.GetFormattedSpeed();
                        var progressText = downloadProgress.GetFormattedProgress();
                        var percentage = downloadProgress.ProgressPercentage;

                        StatusMessage = $"Downloading update {latestVersion} - {percentage:F1}% ({progressText}) - {speedText}";
                    });

                    var success = await _updateService.DownloadAndInstallUpdateAsync(downloadUrl, latestVersion, progress, expectedSize, expectedDigest);

                    if (!success)
                    {
                        StatusMessage = "Update download failed";
                        MessageDialogHelper.ShowError("Failed to download or install the update. Please try again later.", Strings.Error);
                    }
                    // If success, the application will exit and restart
                }
                else
                {
                    StatusMessage = "Update cancelled";
                }
            }
            else
            {
                StatusMessage = "No updates available";
                MessageDialogHelper.ShowInformation($"You are using the latest version ({currentVersion})", "No Updates");
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.ApplicationUpdateCheckFailed);
            StatusMessage = "Update check failed";
            MessageDialogHelper.ShowError($"Failed to check for updates: {ex.Message}", Strings.Error);
        }
        finally
        {
            IsDownloading = false;
        }
    }

    [RelayCommand]
    private async Task DownloadAndInstallAppUpdateAsync()
    {
        try
        {
            if (!IsAppUpdateAvailable || string.IsNullOrEmpty(LatestAppDownloadUrl))
            {
                _loggingService.LogWarning(Strings.NoUpdateAvailableToDownload);
                return;
            }

            var result = MessageDialogHelper.Confirm(
                $"{Strings.NewVersionAvailable} {LatestAppVersion}\n{Strings.ApplicationVersion}: {_updateService.GetCurrentVersion()}\n\n{Strings.DoYouWantToDownloadAndInstall}",
                Strings.CheckForUpdates);

            if (result)
            {
                IsDownloading = true;
                StatusMessage = string.Format(Strings.DownloadingUpdate, LatestAppVersion);

                var progress = new Progress<DownloadProgress>(downloadProgress =>
                {
                    var speedText = downloadProgress.GetFormattedSpeed();
                    var progressText = downloadProgress.GetFormattedProgress();
                    var percentage = downloadProgress.ProgressPercentage;

                    StatusMessage = string.Format(Strings.DownloadingUpdateProgress, LatestAppVersion, percentage.ToString("F1"), progressText, speedText);
                });

                var success = await _updateService.DownloadAndInstallUpdateAsync(LatestAppDownloadUrl, LatestAppVersion, progress);

                if (!success)
                {
                    StatusMessage = Strings.UpdateDownloadFailed;
                    MessageDialogHelper.ShowError(Strings.FailedToDownloadOrInstall, Strings.Error);
                }
                // If success, the application will exit and restart
            }
            else
            {
                StatusMessage = Strings.UpdateCancelled;
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.UpdateInstallFailed);
            StatusMessage = Strings.UpdateDownloadFailed;
            MessageDialogHelper.ShowError(Strings.FailedToDownloadOrInstall, Strings.Error);
        }
        finally
        {
            IsDownloading = false;
        }
    }

    private void OnGameRunningStateChanged(object? sender, bool isRunning)
    {
        IsGameRunning = isRunning;
        StatusMessage = isRunning ? Strings.GameRunningCannotOperate : Strings.Ready;
    }

    private bool CanExecuteWhenNotDownloading()
    {
        return !IsDownloading && !IsTranslationUpdating;
    }

    private bool CanExecuteModOperations()
    {
        return !IsDownloading && !IsTranslationUpdating && !IsGameRunning;
    }

    /// <summary>
    /// 检查网络连接,如果失败则进入离线模式
    /// </summary>
    private async Task CheckNetworkAndEnterOfflineModeIfNeededAsync()
    {
        try
        {
            // 通过主配置(main.json)渠道检测网络连通性
            var isConnected = await _networkService.CheckNetworkConnectionAsync();

            if (!isConnected)
            {
                IsOfflineMode = true;
                _loggingService.LogWarning(Strings.EnteringOfflineMode);

                // 弹窗提示用户
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    MessageDialogHelper.ShowWarning(
                        Strings.OfflineModeMessage,
                        Strings.OfflineModeTitle);
                });
            }
        }
        catch (Exception ex)
        {
            IsOfflineMode = true;
            _loggingService.LogError(ex, Strings.NetworkConnectionFailed);
        }
    }

    private bool CanExecuteCheckUpdates()
    {
        return !IsCheckingUpdates && !IsTranslationUpdating;
    }

    partial void OnIsDownloadingChanged(bool value)
    {
        UpdateAllCommandsCanExecute();
        UpdateShellStatus();
    }

    partial void OnIsTranslationUpdatingChanged(bool value)
    {
        UpdateAllCommandsCanExecute();
        UpdateShellStatus();
    }

    partial void OnIsCheckingUpdatesChanged(bool value)
    {
        UpdateAllCommandsCanExecute();
        // 同步检查状态到子VM，禁用其检查更新按钮防止重复触发
        if (_installedModsViewModel != null)
            _installedModsViewModel.IsCheckingUpdates = value;
        UpdateShellStatus();
    }

    partial void OnIsLoadingChanged(bool value)
    {
        UpdateShellStatus();
    }

    partial void OnStatusMessageChanged(string value)
    {
        UpdateShellStatus();
    }

    partial void OnCurrentPageViewChanged(UserControl? value)
    {
        UpdateShellStatus();
    }

    private void OnChildViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(InstalledModsViewModel.IsDownloading)
            or nameof(InstalledModsViewModel.ProgressValue)
            or nameof(InstalledModsViewModel.HasDeterminateProgress)
            or nameof(InstalledModsViewModel.StatusMessage)
            or nameof(InstalledModsViewModel.IsCheckingUpdates)
            or nameof(ModBrowserViewModel.IsDownloading)
            or nameof(ModBrowserViewModel.ProgressValue)
            or nameof(ModBrowserViewModel.HasDeterminateProgress)
            or nameof(ModBrowserViewModel.StatusMessage)
            or nameof(ModDetailViewModel.IsInstalling)
            or nameof(ModDetailViewModel.IsLoadingReleases)
            or nameof(ModDetailViewModel.ProgressValue)
            or nameof(ModDetailViewModel.HasDeterminateProgress)
            or nameof(ModDetailViewModel.StatusMessage))
        {
            UpdateShellStatus();
        }
    }

    private void UpdateShellStatus()
    {
        if (TryGetMainShellState(out var message, out var isBusy, out var isIndeterminate, out var progressValue))
        {
            ApplyShellStatus(message, isBusy, isIndeterminate, progressValue);
            return;
        }

        if (_modDetailViewModel != null && CurrentPageView is ModDetailView)
        {
            if (_modDetailViewModel.IsLoadingReleases)
            {
                ApplyShellStatus(Strings.LoadingReleases, true, true, 0);
                return;
            }

            if (_modDetailViewModel.IsInstalling)
            {
                ApplyShellStatus(
                    _modDetailViewModel.StatusMessage,
                    true,
                    !_modDetailViewModel.HasDeterminateProgress,
                    _modDetailViewModel.ProgressValue);
                return;
            }

            ApplyShellStatus(_modDetailViewModel.StatusMessage, false, false, 0);
            return;
        }

        if (_installedModsViewModel != null && ReferenceEquals(CurrentPageView, _installedModsView))
        {
            if (_installedModsViewModel.IsDownloading)
            {
                ApplyShellStatus(
                    _installedModsViewModel.StatusMessage,
                    true,
                    !_installedModsViewModel.HasDeterminateProgress,
                    _installedModsViewModel.ProgressValue);
                return;
            }

            ApplyShellStatus(_installedModsViewModel.StatusMessage, false, false, 0);
            return;
        }

        if (_modBrowserViewModel != null && ReferenceEquals(CurrentPageView, _modBrowserView))
        {
            if (_modBrowserViewModel.IsDownloading)
            {
                ApplyShellStatus(
                    _modBrowserViewModel.StatusMessage,
                    true,
                    !_modBrowserViewModel.HasDeterminateProgress,
                    _modBrowserViewModel.ProgressValue);
                return;
            }

            ApplyShellStatus(_modBrowserViewModel.StatusMessage, false, false, 0);
            return;
        }

        ApplyShellStatus(StatusMessage, false, false, 0);
    }

    private bool TryGetMainShellState(out string message, out bool isBusy, out bool isIndeterminate, out double progressValue)
    {
        if (IsLoading || IsCheckingUpdates)
        {
            message = StatusMessage;
            isBusy = true;
            isIndeterminate = true;
            progressValue = 0;
            return true;
        }

        if (IsDownloading || IsTranslationUpdating)
        {
            message = StatusMessage;
            isBusy = true;
            isIndeterminate = !HasDeterminateOperationProgress;
            progressValue = OperationProgressValue;
            return true;
        }

        message = string.Empty;
        isBusy = false;
        isIndeterminate = false;
        progressValue = 0;
        return false;
    }

    private void ApplyShellStatus(string? message, bool isBusy, bool isIndeterminate, double progressValue)
    {
        FooterStatusMessage = string.IsNullOrWhiteSpace(message) ? Strings.Ready : message;
        IsFooterBusy = isBusy;
        IsFooterProgressIndeterminate = isIndeterminate;
        FooterProgressValue = isBusy && !isIndeterminate ? progressValue : 0;
        FooterProgressText = isBusy && !isIndeterminate ? $"{progressValue:F1}%" : string.Empty;
    }

    partial void OnIsGameRunningChanged(bool value)
    {
        // 游戏运行状态改变时，更新所有依赖此状态的命令
        // 必须在UI线程上执行，因为可能从Timer线程触发
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            UpdateAllCommandsCanExecute();
        });
    }

    private void UpdateAllCommandsCanExecute()
    {
        // Update all commands that should be disabled during operations
        LaunchGameCommand?.NotifyCanExecuteChanged();
        StopGameCommand?.NotifyCanExecuteChanged();
        OpenSettingsCommand?.NotifyCanExecuteChanged();
        InstallTranslationCommand?.NotifyCanExecuteChanged();
        UpdateTranslationCommand?.NotifyCanExecuteChanged();
        UpdateTranslationPluginCommand?.NotifyCanExecuteChanged();
        UninstallTranslationCommand?.NotifyCanExecuteChanged();
        SetTranslationLanguageCommand?.NotifyCanExecuteChanged();
        ToggleTranslationPluginCommand?.NotifyCanExecuteChanged();
        CheckForModUpdatesCommand?.NotifyCanExecuteChanged();
    }

    private void FilterAndSortMods()
    {
        var allMods = Mods.AsEnumerable();
        
        // Apply search filter
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var searchLower = SearchText.ToLowerInvariant();
            allMods = allMods.Where(m => 
                m.DisplayName.ToLowerInvariant().Contains(searchLower) ||
                m.Id.ToLowerInvariant().Contains(searchLower));
        }
        
        // 已安装优先，次级按JSON原始顺序
        var sortedMods = allMods
            .OrderByDescending(m => m.IsInstalled)
            .ThenBy(m => Mods.IndexOf(m))
            .ToList();
        
        // Update filtered collection
        FilteredMods.Clear();
        foreach (var mod in sortedMods)
        {
            FilteredMods.Add(mod);
        }
        
        // Update count display
        var totalCount = Mods.Count;
        var filteredCount = FilteredMods.Count;
        FilteredModCount = filteredCount == totalCount ? 
            string.Format(Strings.ModsCount, totalCount) : 
            string.Format(Strings.ModsFilteredCount, filteredCount, totalCount);
    }
}
