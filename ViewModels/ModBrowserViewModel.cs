using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GHPC_Mod_Manager.Models;
using GHPC_Mod_Manager.Services;
using GHPC_Mod_Manager.Helpers;
using System.Collections.ObjectModel;
using System.Windows;
using GHPC_Mod_Manager.Resources;

namespace GHPC_Mod_Manager.ViewModels;

/// <summary>
/// Tag筛选项，用于MOD浏览页的Chip筛选栏
/// </summary>
public partial class TagFilter : ObservableObject
{
    // Tag标识符（用于筛选逻辑）
    public string Tag { get; set; }

    // 本地化显示名（用于UI展示）
    public string DisplayName { get; set; }

    // 选中状态变化时通知ViewModel重新过滤
    public Action? OnSelectionChanged { get; set; }

    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) => OnSelectionChanged?.Invoke();

    public TagFilter(string tag, string displayName)
    {
        Tag = tag;
        DisplayName = displayName;
    }
}

public partial class ModBrowserViewModel : ObservableObject
{
    private readonly IModManagerService _modManagerService;
    private readonly ILoggingService _loggingService;
    private readonly ISettingsService _settingsService;
    private readonly IMelonLoaderService _melonLoaderService;
    private readonly IUpdateService _updateService;

    // 全量MOD列表（由MainViewModel注入）
    private List<ModViewModel> _allMods = new();

    [ObservableProperty]
    private ObservableCollection<ModViewModel> _filteredMods = new();

    [ObservableProperty]
    private ObservableCollection<TagFilter> _availableTags = new();

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _filteredModCount = string.Empty;

    [ObservableProperty]
    private bool _isDownloading;

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private bool _hasDeterminateProgress;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    // 当前游戏版本（从 Latest.log 读取，如 "20260210.1"），null 表示未检测到
    [ObservableProperty]
    private string? _currentGameVersion;

    [ObservableProperty]
    private bool _showUninstalledOnly;

    // 新增MOD横幅状态（会话级，不持久化）
    [ObservableProperty]
    private bool _hasNewMods;
    [ObservableProperty]
    private int _newModsCount;
    [ObservableProperty]
    private List<string> _newModsPreview = new();
    [ObservableProperty]
    private bool _isNewModsBannerDismissed;

    // 横幅可见性：有新增且未被关闭时显示
    public bool CanShowNewModsBanner => HasNewMods && !IsNewModsBannerDismissed;

    // 内部存储检测到的新增MOD
    private List<ModViewModel> _detectedNewMods = new();

    // 通知MainViewModel导航到详情页
    public event EventHandler<ModViewModel>? NavigateToDetailRequested;

    // 通知MainViewModel刷新数据
    public event EventHandler? RefreshRequested;

    // 通知MainViewModel执行检查更新（true=只检查已安装）
    public event EventHandler<bool>? CheckForUpdatesRequested;

    // 通知MainViewModel导航到设置页面
    public event EventHandler? NavigateToSettingsRequested;

    public string RedownloadReinstallText => Strings.ResourceManager.GetString("RedownloadReinstall", Strings.Culture) ?? "Redownload & Reinstall";

    public ModBrowserViewModel(IModManagerService modManagerService, ILoggingService loggingService, ISettingsService settingsService, IMelonLoaderService melonLoaderService, IUpdateService updateService)
    {
        _modManagerService = modManagerService;
        _loggingService = loggingService;
        _settingsService = settingsService;
        _melonLoaderService = melonLoaderService;
        _updateService = updateService;

        // 异步读取游戏版本，不阻塞构造
        _ = LoadCurrentGameVersionAsync();
    }

    private async Task LoadCurrentGameVersionAsync()
    {
        var gameRoot = _settingsService.Settings.GameRootPath;
        if (string.IsNullOrEmpty(gameRoot)) return;
        CurrentGameVersion = await _melonLoaderService.GetCurrentGameVersionAsync(gameRoot);
    }

    partial void OnSearchTextChanged(string value) => FilterMods();

    partial void OnShowUninstalledOnlyChanged(bool value) => FilterMods();

    /// <summary>
    /// 由MainViewModel在数据加载完成后调用，注入全量MOD列表
    /// </summary>
    public void SetModsSource(IEnumerable<ModViewModel> allMods)
    {
        _allMods = allMods.ToList();

        // 用settings中的语言，避免异步上下文中CultureInfo不可靠
        var lang = _settingsService.Settings.Language;
        var prefix = lang.Length >= 2 ? lang.Substring(0, 2) : lang;
        var tagMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in _allMods)
        {
            foreach (var kv in mod.Config?.Tags ?? new())
            {
                if (!tagMap.ContainsKey(kv.Key))
                {
                    var names = kv.Value;
                    string displayName;
                    if (names == null || names.Count == 0)
                        displayName = kv.Key;
                    else if (names.TryGetValue(lang, out var n) && !string.IsNullOrWhiteSpace(n))
                        displayName = n;
                    else
                    {
                        // 前缀匹配，处理 zh-Hans-CN / zh-CN 等变体
                        var prefixMatch = names.FirstOrDefault(p => p.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(p.Value));
                        if (prefixMatch.Value != null)
                            displayName = prefixMatch.Value;
                        else if (names.TryGetValue("en-US", out var en) && !string.IsNullOrWhiteSpace(en))
                            displayName = en;
                        else
                            displayName = names.Values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? kv.Key;
                    }
                    tagMap[kv.Key] = displayName;
                }
            }
        }

        // 保留已选中状态
        var selectedTags = AvailableTags.Where(t => t.IsSelected).Select(t => t.Tag).ToHashSet();
        AvailableTags.Clear();
        foreach (var kv in tagMap.OrderBy(x => x.Key))
        {
            var tf = new TagFilter(kv.Key, kv.Value)
            {
                IsSelected = selectedTags.Contains(kv.Key),
                OnSelectionChanged = FilterMods
            };
            AvailableTags.Add(tf);
        }

        FilterMods();

        // FilterMods后WPF才绑定到FilteredMods中的对象，此时再触发本地化刷新才有效
        foreach (var mod in _allMods)
            mod.RefreshLocalization(lang);
    }

    private void FilterMods()
    {
        var selectedTagKeys = AvailableTags.Where(t => t.IsSelected).Select(t => t.Tag).ToHashSet();

        var filtered = _allMods.AsEnumerable();

        // 浏览页彻底隐藏下架Mod，仅在已安装页保留显示
        filtered = filtered.Where(m => !m.IsDelisted);

        // 仅显示未安装
        if (ShowUninstalledOnly)
        {
            filtered = filtered.Where(m => !m.IsInstalled);
        }

        // 文本搜索
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            filtered = filtered.Where(m =>
                m.DisplayName.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                m.Id.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
        }

        // Tag筛选：选中的key必须全部出现在MOD的Tags字典key中
        if (selectedTagKeys.Count > 0)
        {
            filtered = filtered.Where(m =>
                selectedTagKeys.All(key => m.Config?.Tags?.ContainsKey(key) == true));
        }

        var result = filtered.ToList();
        FilteredMods = new ObservableCollection<ModViewModel>(result);
        var totalCount = _allMods.Count(m => !m.IsDelisted);
        FilteredModCount = $"{result.Count} / {totalCount}";
    }

    [RelayCommand]
    private void ClearTagFilter()
    {
        foreach (var tf in AvailableTags)
            tf.IsSelected = false;
        // IsSelected变化会通过OnSelectionChanged触发FilterMods，无需手动调用
    }

    [RelayCommand]
    private void NavigateToDetail(ModViewModel mod)
    {
        NavigateToDetailRequested?.Invoke(this, mod);
    }

    [RelayCommand]
    private void CheckForUpdates()
    {
        // 浏览页检查全部MOD的更新
        CheckForUpdatesRequested?.Invoke(this, false);
    }

    [RelayCommand]
    private void Refresh()
    {
        RefreshRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand(CanExecute = nameof(CanExecuteModOps))]
    private async Task InstallModAsync(ModViewModel mod)
        => await InstallModCoreAsync(mod, preferBackup: true);

    [RelayCommand(CanExecute = nameof(CanExecuteModOps))]
    private async Task RedownloadInstallAsync(ModViewModel mod)
        => await InstallModCoreAsync(mod, preferBackup: false);

    private async Task InstallModCoreAsync(ModViewModel mod, bool preferBackup)
    {
        if (string.IsNullOrEmpty(mod.LatestVersion))
        {
            MessageDialogHelper.ShowError(Strings.CannotGetModVersionInfo, Strings.Error);
            return;
        }

        // 检查管理器版本要求
        if (!string.IsNullOrEmpty(mod.Config?.RequiredManagerVersion))
        {
            if (!_updateService.MeetsRequiredVersion(mod.Config.RequiredManagerVersion))
            {
                var currentVersion = _updateService.GetCurrentVersion();
                var goToUpdate = MessageDialogHelper.ShowGoToSettingsCancel(
                    string.Format(Strings.ManagerVersionRequirementMessage, mod.Config.RequiredManagerVersion, currentVersion),
                    Strings.ManagerVersionRequirementTitle);
                if (goToUpdate)
                {
                    NavigateToSettingsRequested?.Invoke(this, EventArgs.Empty);
                }
                return;
            }
        }

        if (!await ModInstallCompatibilityHelper.ConfirmInstallAsync(mod, CurrentGameVersion, _settingsService, _melonLoaderService))
            return;

        try
        {
            IsDownloading = true;
            HasDeterminateProgress = false;
            ProgressValue = 0;
            StatusMessage = string.Format(Strings.Installing, mod.DisplayName);

            // 检查冲突：已安装的MOD中是否有与当前MOD冲突的
            if (mod.Config?.Conflicts?.Any() == true)
            {
                var conflictingInstalled = _allMods
                    .Where(m => m.IsInstalled && mod.Config.Conflicts.Contains(m.Id))
                    .Select(m => m.DisplayName)
                    .ToList();

                if (conflictingInstalled.Count > 0)
                {
                    IsDownloading = false;
                    var conflictNames = string.Join("\n• ", conflictingInstalled);
                    var msg = $"{Strings.ConflictDialogMessage}\n• {conflictNames}\n\n{Strings.ConflictInstallAnyway}";
                    var firstResult = MessageDialogHelper.Confirm(msg, Strings.ConflictDialogTitle);
                    if (!firstResult) return;

                    var secondResult = MessageDialogHelper.Confirm(
                        Strings.ConflictInstallConfirm,
                        Strings.ConflictDialogTitle);
                    if (!secondResult) return;

                    IsDownloading = true;
                }
            }

            // 检查依赖：缺失时弹对话框
            if (mod.Config?.Requirements?.Any() == true)
            {
                var (allSatisfied, missingIds) = await _modManagerService.CheckSingleModDependenciesAsync(mod.Id);
                if (!allSatisfied && missingIds.Count > 0)
                {
                    IsDownloading = false;
                    // 用DisplayName替换modId，找不到则保留id
                    var missingNames = string.Join("\n• ", missingIds.Select(id =>
                        _allMods.FirstOrDefault(m => m.Id == id)?.DisplayName ?? id));
                    var msg = $"{Strings.DependencyDialogMessage}\n• {missingNames}";
                    var result = MessageDialogHelper.ConfirmOK(msg, Strings.DependencyDialogTitle);
                    if (!result) return;

                    // 用户确认后跳转到第一个缺失依赖的详情页
                    var firstMissingMod = _allMods.FirstOrDefault(m => missingIds.Contains(m.Id));
                    if (firstMissingMod != null)
                    {
                        NavigateToDetailRequested?.Invoke(this, firstMissingMod);
                        return;
                    }
                    IsDownloading = true;
                }
            }

            var progress = new Progress<DownloadProgress>(p =>
            {
                HasDeterminateProgress = true;
                ProgressValue = p.ProgressPercentage;
                StatusMessage = $"{string.Format(Strings.Installing, mod.DisplayName)} - {p.ProgressPercentage:F1}% - {p.GetFormattedSpeed()}";
            });

            var success = await _modManagerService.InstallModAsync(mod.Config, mod.LatestVersion, progress, skipDependencyCheck: true, skipConflictCheck: true, preferBackup: preferBackup);
            StatusMessage = success
                ? string.Format(Strings.InstallSuccessful, mod.DisplayName)
                : string.Format(Strings.InstallFailed, mod.DisplayName);

            if (success) RefreshRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.ModInstallError, mod.Id);
            StatusMessage = string.Format(Strings.InstallFailed, mod.DisplayName);
        }
        finally
        {
            HasDeterminateProgress = false;
            ProgressValue = 0;
            IsDownloading = false;
        }
    }

    private bool CanExecuteModOps() => !IsDownloading;

    /// <summary>
    /// 由MainViewModel调用，推送检测到的新增MOD
    /// </summary>
    internal void SetNewMods(IEnumerable<ModViewModel> newMods)
    {
        _detectedNewMods = newMods.ToList();
        _isNewModsBannerDismissed = false;
        HasNewMods = _detectedNewMods.Count > 0;
        NewModsCount = _detectedNewMods.Count;
        NewModsPreview = _detectedNewMods.Take(3).Select(m => m.DisplayName).ToList();
    }

    /// <summary>
    /// 由MainViewModel调用，清除新增MOD状态
    /// </summary>
    internal void ClearNewMods()
    {
        _detectedNewMods.Clear();
        HasNewMods = false;
        NewModsCount = 0;
        NewModsPreview.Clear();
    }

    /// <summary>
    /// 导航到浏览页时调用，决定是否展示横幅
    /// </summary>
    internal void OnNavigatedToBrowser()
    {
        // 横幅可见性由 HasNewMods && !IsNewModsBannerDismissed 数据绑定控制
    }

    [RelayCommand]
    private void DismissNewModsBanner()
    {
        IsNewModsBannerDismissed = true;
    }

    partial void OnIsDownloadingChanged(bool value)
    {
        InstallModCommand.NotifyCanExecuteChanged();
        RedownloadInstallCommand.NotifyCanExecuteChanged();
    }

    partial void OnHasNewModsChanged(bool value) => OnPropertyChanged(nameof(CanShowNewModsBanner));
    partial void OnIsNewModsBannerDismissedChanged(bool value) => OnPropertyChanged(nameof(CanShowNewModsBanner));
}
