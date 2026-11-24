using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GHPC_Mod_Manager.Models;
using GHPC_Mod_Manager.Services;
using GHPC_Mod_Manager.Helpers;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using GHPC_Mod_Manager.Resources;
using GHPC_Mod_Manager.Views;

namespace GHPC_Mod_Manager.ViewModels;

/// <summary>
/// 依赖/冲突MOD的显示信息
/// </summary>
public class RelatedModInfo
{
    public string ModId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool IsInstalled { get; set; }
    public bool IsEnabled { get; set; }
    public bool IsDelisted { get; set; }

    // 作者推荐理由（仅推荐列表使用）
    public string? Note { get; set; }
    public bool HasNote => !string.IsNullOrWhiteSpace(Note);
}

public partial class ModDetailViewModel : ObservableObject
{
    private readonly IModManagerService _modManagerService;
    private readonly IModBackupService _modBackupService;
    private readonly INetworkService _networkService;
    private readonly ILoggingService _loggingService;
    private readonly ISettingsService _settingsService;
    private readonly IMelonLoaderService _melonLoaderService;
    private readonly IProcessService _processService;

    // 当前展示的MOD
    [ObservableProperty]
    private ModViewModel? _mod;

    // 所有可用版本（从GitHub Releases加载）
    [ObservableProperty]
    private ObservableCollection<GitHubRelease> _availableReleases = new();

    // 当前选中的版本（用于安装）
    [ObservableProperty]
    private GitHubRelease? _selectedRelease;

    [ObservableProperty]
    private bool _isLoadingReleases;

    [ObservableProperty]
    private bool _isLoadingReadme;

    [ObservableProperty]
    private bool _isGameRunning;

    [ObservableProperty]
    private bool _isInstalling;

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private bool _hasDeterminateProgress;

    [ObservableProperty]
    private bool _hasSelectedReleaseBackup;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    // 依赖列表
    [ObservableProperty]
    private ObservableCollection<RelatedModInfo> _dependencies = new();

    // 冲突列表
    [ObservableProperty]
    private ObservableCollection<RelatedModInfo> _conflicts = new();

    [ObservableProperty]
    private bool _hasDependencies;

    [ObservableProperty]
    private bool _hasConflicts;

    // 作者推荐搭配的Mod
    [ObservableProperty]
    private ObservableCollection<RelatedModInfo> _recommendations = new();

    [ObservableProperty]
    private bool _hasRecommendations;

    // 记录来源页面（依赖跳转时用于返回）
    public string? ReturnToModId { get; set; }
    public bool IsNavigatedFromDependency { get; set; }
    public NavigationPage ReturnToPage { get; set; } = NavigationPage.InstalledMods;

    // 通知返回（由GoBackCommand触发）
    public event EventHandler? GoBackRequested;

    // 通知导航到另一个MOD详情（依赖跳转）
    public event EventHandler<string>? NavigateToModRequested;

    // 通知刷新数据
    public event EventHandler? RefreshRequested;

    // 全量MOD列表引用（用于查找依赖/冲突的显示名称和安装状态）
    private List<ModViewModel> _allMods = new();

    public string RedownloadReinstallText => Strings.ResourceManager.GetString("RedownloadReinstall", Strings.Culture) ?? "Redownload & Reinstall";

    public ModDetailViewModel(IModManagerService modManagerService, IModBackupService modBackupService, INetworkService networkService, ILoggingService loggingService, ISettingsService settingsService, IMelonLoaderService melonLoaderService, IProcessService processService)
    {
        _modManagerService = modManagerService;
        _modBackupService = modBackupService;
        _networkService = networkService;
        _loggingService = loggingService;
        _settingsService = settingsService;
        _melonLoaderService = melonLoaderService;
        _processService = processService;

        IsGameRunning = _processService.IsGameRunning;
        _processService.GameRunningStateChanged += (s, e) =>
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() => IsGameRunning = e);
        };
    }

    /// <summary>
    /// 初始化详情页，加载版本列表和依赖状态
    /// </summary>
    public async Task InitializeAsync(ModViewModel mod, List<ModViewModel> allMods)
    {
        Mod = mod;
        // 用settings中的语言，避免异步上下文中CultureInfo不可靠
        mod.RefreshLocalization(_settingsService.Settings.Language);
        _allMods = allMods;
        StatusMessage = string.Empty;

        // 加载依赖和冲突信息
        LoadRelatedMods();

        // 异步加载版本列表
        await LoadReleasesAsync();
    }

    private void LoadRelatedMods()
    {
        Dependencies.Clear();
        Conflicts.Clear();

        if (Mod?.Config == null) return;

        // 构建依赖列表
        foreach (var reqId in Mod.Config.Requirements ?? new List<string>())
        {
            var relatedMod = _allMods.FirstOrDefault(m => m.Id == reqId);
            // 跳过已下架的MOD
            if (relatedMod?.IsDelisted == true)
                continue;

            Dependencies.Add(new RelatedModInfo
            {
                ModId = reqId,
                DisplayName = GetRelatedModDisplayName(reqId, relatedMod),
                IsInstalled = relatedMod?.IsInstalled ?? false,
                IsEnabled = relatedMod?.IsEnabled ?? false,
                IsDelisted = relatedMod?.IsDelisted ?? false
            });
        }

        // 构建冲突列表
        foreach (var conflictId in Mod.Config.Conflicts ?? new List<string>())
        {
            var relatedMod = _allMods.FirstOrDefault(m => m.Id == conflictId);
            // 跳过已下架的MOD
            if (relatedMod?.IsDelisted == true)
                continue;

            Conflicts.Add(new RelatedModInfo
            {
                ModId = conflictId,
                DisplayName = GetRelatedModDisplayName(conflictId, relatedMod),
                IsInstalled = relatedMod?.IsInstalled ?? false,
                IsEnabled = relatedMod?.IsEnabled ?? false,
                IsDelisted = relatedMod?.IsDelisted ?? false
            });
        }

        // 构建推荐列表：只显示Mod列表里存在且未下架的Mod
        Recommendations.Clear();
        foreach (var rec in Mod.Config.Recommends ?? new List<ModRecommendation>())
        {
            var relatedMod = _allMods.FirstOrDefault(m => string.Equals(m.Id, rec.Id, StringComparison.OrdinalIgnoreCase));
            if (relatedMod == null || relatedMod.IsDelisted || relatedMod.Id == Mod.Id)
                continue;

            Recommendations.Add(new RelatedModInfo
            {
                ModId = relatedMod.Id,
                DisplayName = relatedMod.DisplayName,
                IsInstalled = relatedMod.IsInstalled,
                IsEnabled = relatedMod.IsEnabled,
                Note = GetLocalizedNote(rec.Note)
            });
        }

        HasDependencies = Dependencies.Count > 0;
        HasConflicts = Conflicts.Count > 0;
        HasRecommendations = Recommendations.Count > 0;
    }

    // 按当前语言取推荐理由：完全匹配 → 语言前缀 → en-US → 任意
    private string? GetLocalizedNote(Dictionary<string, string>? notes)
    {
        if (notes == null || notes.Count == 0)
            return null;

        var lang = _settingsService.Settings.Language;
        if (notes.TryGetValue(lang, out var exact) && !string.IsNullOrWhiteSpace(exact))
            return exact;

        var prefix = lang.Length >= 2 ? lang.Substring(0, 2) : lang;
        var prefixMatch = notes.FirstOrDefault(kv => kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(kv.Value));
        if (prefixMatch.Value != null)
            return prefixMatch.Value;

        if (notes.TryGetValue("en-US", out var en) && !string.IsNullOrWhiteSpace(en))
            return en;

        return notes.Values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
    }

    private string GetRelatedModDisplayName(string modId, ModViewModel? relatedMod)
    {
        if (!string.IsNullOrWhiteSpace(relatedMod?.DisplayName))
        {
            return relatedMod.DisplayName;
        }

        if (!string.IsNullOrWhiteSpace(modId) &&
            !string.Equals(modId, Strings.Unknown, StringComparison.OrdinalIgnoreCase))
        {
            return modId;
        }

        return _settingsService.Settings.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "无" : "None";
    }

    private async Task LoadReleasesAsync()
    {
        if (Mod == null) return;

        IsLoadingReleases = true;
        AvailableReleases.Clear();

        try
        {
            var releases = await _modManagerService.GetModReleasesAsync(Mod.Id);
            foreach (var r in releases)
                AvailableReleases.Add(r);

            // 默认选中最新版本
            SelectedRelease = AvailableReleases.FirstOrDefault(r => r.TagName == Mod.LatestVersion)
                              ?? AvailableReleases.FirstOrDefault();
            await UpdateSelectedReleaseBackupStateAsync(SelectedRelease);
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, "LoadReleasesAsync failed for mod: {0}", Mod.Id);
        }
        finally
        {
            IsLoadingReleases = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallSelectedVersionAsync()
        => await InstallSelectedVersionCoreAsync(preferBackup: true);

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task RedownloadInstallSelectedVersionAsync()
        => await InstallSelectedVersionCoreAsync(preferBackup: false);

    private async Task InstallSelectedVersionCoreAsync(bool preferBackup)
    {
        if (Mod == null || SelectedRelease == null) return;

        if (!await ModInstallCompatibilityHelper.ConfirmInstallAsync(Mod, null, _settingsService, _melonLoaderService))
            return;

        try
        {
            IsInstalling = true;
            HasDeterminateProgress = false;
            ProgressValue = 0;
            StatusMessage = string.Format(Strings.Installing, Mod.DisplayName);

            // 安装前强制刷新,用最新 release 的资产 URL(避免 7 天缓存里 asset URL 失效)。保留用户已选版本。
            try
            {
                var freshReleases = await _modManagerService.GetModReleasesAsync(Mod.Id, forceRefresh: true);
                var matched = freshReleases.FirstOrDefault(r => r.TagName == SelectedRelease?.TagName);
                if (matched != null)
                    SelectedRelease = matched;
            }
            catch
            {
                // 刷新失败时使用已选版本继续
            }

            var progress = new Progress<DownloadProgress>(p =>
            {
                HasDeterminateProgress = true;
                ProgressValue = p.ProgressPercentage;
                StatusMessage = $"{string.Format(Strings.Installing, Mod.DisplayName)} - {p.GetProgressSuffix()}";
            });

            // 检查冲突
            if (Mod.Config?.Conflicts?.Any() == true)
            {
                var conflictingInstalled = _allMods
                    .Where(m => m.IsInstalled && Mod.Config.Conflicts.Contains(m.Id))
                    .Select(m => m.DisplayName)
                    .ToList();

                if (conflictingInstalled.Count > 0)
                {
                    IsInstalling = false;
                    var conflictNames = string.Join("\n• ", conflictingInstalled);
                    var msg = $"{Strings.ConflictDialogMessage}\n• {conflictNames}\n\n{Strings.ConflictInstallAnyway}";
                    var firstResult = MessageDialogHelper.Confirm(msg, Strings.ConflictDetectedOnLaunch);
                    if (!firstResult) return;

                    // 二次确认
                    var secondResult = MessageDialogHelper.Confirm(
                        Strings.ConflictInstallConfirm,
                        Strings.ConflictDetectedOnLaunch);
                    if (!secondResult) return;

                    IsInstalling = true;
                }
            }

            // 检查依赖
            bool skipDepCheck = false;
            if (Mod.Config?.Requirements?.Any() == true)
            {
                var (allSatisfied, missingIds) = await _modManagerService.CheckSingleModDependenciesAsync(Mod.Id);
                if (!allSatisfied && missingIds.Count > 0)
                {
                    IsInstalling = false;
                    // 用DisplayName替换modId，找不到则保留id
                    var missingNames = string.Join("\n• ", missingIds.Select(id =>
                        _allMods.FirstOrDefault(m => m.Id == id)?.DisplayName ?? id));
                    var msg = $"{Strings.DependencyDialogMessage}\n• {missingNames}";
                    var result = MessageDialogHelper.ConfirmOK(msg, Strings.DependencyDialogTitle);
                    if (!result) return;

                    // 跳转到第一个缺失依赖的详情页
                    var firstMissingId = missingIds.First();
                    NavigateToModRequested?.Invoke(this, firstMissingId);
                    return;
                }
                skipDepCheck = true;
            }

            var success = await _modManagerService.InstallModAsync(
                Mod.Config!, SelectedRelease.TagName, progress, skipDependencyCheck: skipDepCheck, skipConflictCheck: true, preferBackup: preferBackup,
                operationText: preferBackup ? null : string.Format(Strings.Reinstalling, Mod.DisplayName));

            StatusMessage = success
                ? string.Format(Strings.InstallSuccessful, Mod.DisplayName)
                : string.Format(Strings.InstallFailed, Mod.DisplayName);

            if (success)
            {
                RefreshRequested?.Invoke(this, EventArgs.Empty);
                // 刷新依赖状态
                LoadRelatedMods();
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.ModInstallError, Mod.Id);
            StatusMessage = string.Format(Strings.InstallFailed, Mod.DisplayName);
        }
        finally
        {
            HasDeterminateProgress = false;
            ProgressValue = 0;
            IsInstalling = false;
        }
    }

    [RelayCommand]
    private void GoBack()
    {
        GoBackRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void NavigateToDependency(string modId)
    {
        NavigateToModRequested?.Invoke(this, modId);
    }

    [RelayCommand]
    private void OpenGitHub()
    {
        var url = Mod?.GitHubRepositoryUrl;
        if (!string.IsNullOrEmpty(url))
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { _loggingService.LogError(ex, "OpenGitHub failed"); }
        }
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task UninstallAsync()
    {
        if (Mod == null) return;

        // Yes = 卸载并保留备份，No = 彻底删除，其他 = 取消
        var choice = MessageDialogHelper.ShowUninstallChoice(
            string.Format(Strings.ConfirmUninstallMod, Mod.DisplayName),
            Strings.ConfirmUninstall);
        if (choice != MessageDialogResult.Yes && choice != MessageDialogResult.No) return;
        var removeCompletely = choice == MessageDialogResult.No;

        try
        {
            IsInstalling = true;
            StatusMessage = string.Format(Strings.Uninstalling, Mod.DisplayName);
            var success = await _modManagerService.UninstallModAsync(Mod.Id);
            // 先正常卸载（失败时还有备份可回退），成功后再删除全部备份
            if (success && removeCompletely)
                success = await _modManagerService.DeleteAllModBackupsAsync(Mod.Id);
            StatusMessage = success
                ? string.Format(Strings.ModUninstalledSuccessfully, Mod.DisplayName)
                : string.Format(Strings.UninstallFailed, Mod.DisplayName);
            if (success) RefreshRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.ModUninstallError, Mod.Id);
            StatusMessage = string.Format(Strings.UninstallFailed, Mod.DisplayName);
        }
        finally { IsInstalling = false; }
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task UninstallManualModAsync()
    {
        if (Mod == null) return;

        // Yes = 卸载并保留备份，No = 彻底删除，其他 = 取消
        var choice = MessageDialogHelper.ShowUninstallChoice(
            string.Format(Strings.ConfirmUninstallMod, Mod.DisplayName),
            Strings.ConfirmUninstall);
        if (choice != MessageDialogResult.Yes && choice != MessageDialogResult.No) return;
        var removeCompletely = choice == MessageDialogResult.No;

        try
        {
            IsInstalling = true;
            StatusMessage = string.Format(Strings.Uninstalling, Mod.DisplayName);
            var success = await _modManagerService.UninstallManualModAsync(Mod.Id);
            // 先正常卸载（失败时还有备份可回退），成功后再删除全部备份
            if (success && removeCompletely)
                success = await _modManagerService.DeleteAllModBackupsAsync(Mod.Id);
            StatusMessage = success
                ? string.Format(Strings.ModUninstalledSuccessfully, Mod.DisplayName)
                : string.Format(Strings.UninstallFailed, Mod.DisplayName);
            if (success) RefreshRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.ModUninstallError, Mod.Id);
            StatusMessage = string.Format(Strings.UninstallFailed, Mod.DisplayName);
        }
        finally { IsInstalling = false; }
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task ToggleAsync()
    {
        if (Mod == null) return;

        try
        {
            IsInstalling = true;
            bool success;
            if (Mod.IsEnabled)
            {
                success = await _modManagerService.DisableModAsync(Mod.Id);
                StatusMessage = success
                    ? string.Format(Strings.ModDisabledStatus, Mod.DisplayName)
                    : string.Format(Strings.ModDisableFailed, Mod.DisplayName);
            }
            else
            {
                success = await _modManagerService.EnableModAsync(Mod.Id);
                StatusMessage = success
                    ? string.Format(Strings.ModEnabledStatus, Mod.DisplayName)
                    : string.Format(Strings.ModEnableFailed, Mod.DisplayName);
            }
            if (success) RefreshRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.ModToggleError, Mod.Id);
            StatusMessage = string.Format(Strings.StatusToggleFailed, Mod.DisplayName);
        }
        finally { IsInstalling = false; }
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task UpdateAsync()
    {
        if (Mod == null || !Mod.CanUpdate || SelectedRelease == null) return;

        try
        {
            IsInstalling = true;
            HasDeterminateProgress = false;
            ProgressValue = 0;
            StatusMessage = string.Format(Strings.UpdatingMod, Mod.DisplayName, Mod.InstalledVersion, SelectedRelease.TagName);

            // 更新前强制刷新,取最新发布版本(源头已按 PublishedAt 倒序)
            try
            {
                var freshReleases = await _modManagerService.GetModReleasesAsync(Mod.Id, forceRefresh: true);
                if (freshReleases.Count > 0)
                    SelectedRelease = freshReleases.FirstOrDefault() ?? SelectedRelease;
            }
            catch
            {
                // 刷新失败时使用已选版本继续
            }

            var progress = new Progress<DownloadProgress>(p =>
            {
                HasDeterminateProgress = true;
                ProgressValue = p.ProgressPercentage;
                StatusMessage = $"{string.Format(Strings.UpdatingMod, Mod.DisplayName, Mod.InstalledVersion, SelectedRelease.TagName)} - {p.GetProgressSuffix()}";
            });

            var success = await _modManagerService.UpdateModAsync(Mod.Id, SelectedRelease.TagName, progress);
            StatusMessage = success
                ? string.Format(Strings.ModUpdatedSuccessfully, Mod.DisplayName)
                : string.Format(Strings.ModUpdateFailed, Mod.DisplayName);
            if (success) RefreshRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.ModUpdateFailed, Mod.Id);
            StatusMessage = string.Format(Strings.ModUpdateFailed, Mod.DisplayName);
        }
        finally
        {
            HasDeterminateProgress = false;
            ProgressValue = 0;
            IsInstalling = false;
        }
    }

    /// <summary>
    /// 协议链接触发的安装/更新：先确认，再走详情页的正常安装或更新流程
    /// </summary>
    public async Task RunProtocolInstallOrUpdateAsync()
    {
        if (Mod == null) return;

        if (IsInstalling)
        {
            MessageDialogHelper.ShowInformation(Strings.ProtocolBusy, Strings.ProtocolDialogTitle);
            return;
        }

        var latestRelease = AvailableReleases.FirstOrDefault(r => r.TagName == Mod.LatestVersion)
                            ?? AvailableReleases.FirstOrDefault();
        if (latestRelease == null)
        {
            MessageDialogHelper.ShowWarning(string.Format(Strings.ProtocolNoRelease, Mod.DisplayName), Strings.ProtocolDialogTitle);
            return;
        }

        var isUpToDate = Mod.IsInstalled && !Mod.CanUpdate &&
                         !(Mod.IsManuallyInstalled && Mod.InstalledVersion == Strings.Manual);
        if (isUpToDate)
        {
            MessageDialogHelper.ShowInformation(
                string.Format(Strings.ProtocolAlreadyUpToDate, Mod.DisplayName, Mod.InstalledVersion),
                Strings.ProtocolDialogTitle);
            return;
        }

        // 游戏运行中不能改文件，只打开页面，关游戏后按钮会自动可用
        if (IsGameRunning)
        {
            MessageDialogHelper.ShowInformation(Strings.ProtocolGameRunning, Strings.ProtocolDialogTitle);
            return;
        }

        if (Mod.IsInstalled)
        {
            if (Mod.CanUpdate)
            {
                if (!MessageDialogHelper.Confirm(
                        string.Format(Strings.ProtocolConfirmUpdate, Mod.DisplayName, Mod.InstalledVersion, latestRelease.TagName),
                        Strings.ProtocolDialogTitle))
                    return;

                SelectedRelease = latestRelease;
                await UpdateAsync();
                return;
            }

            // 手动安装且读不到版本号：无法判断新旧，问用户是否用最新版替换（旧文件会备份）
            if (!MessageDialogHelper.Confirm(
                    string.Format(Strings.ProtocolConfirmReplaceManual, Mod.DisplayName, latestRelease.TagName),
                    Strings.ProtocolDialogTitle))
                return;

            SelectedRelease = latestRelease;
            await InstallSelectedVersionCoreAsync(preferBackup: true);
            return;
        }

        if (!MessageDialogHelper.Confirm(
                string.Format(Strings.ProtocolConfirmInstall, Mod.DisplayName, latestRelease.TagName),
                Strings.ProtocolDialogTitle))
            return;

        SelectedRelease = latestRelease;
        await InstallSelectedVersionCoreAsync(preferBackup: true);
    }

    // 通知MainViewModel打开配置窗口
    public event EventHandler<string>? ConfigureModRequested;

    [RelayCommand(CanExecute = nameof(CanManage))]
    private void Configure()
    {
        if (Mod != null)
            ConfigureModRequested?.Invoke(this, Mod.Id);
    }

    private bool CanInstall() => !IsInstalling && SelectedRelease != null && Mod != null && !IsGameRunning;
    private bool CanManage() => !IsInstalling && Mod != null && !IsGameRunning;

    partial void OnIsGameRunningChanged(bool value)
    {
        InstallSelectedVersionCommand.NotifyCanExecuteChanged();
        RedownloadInstallSelectedVersionCommand.NotifyCanExecuteChanged();
        UninstallCommand.NotifyCanExecuteChanged();
        UninstallManualModCommand.NotifyCanExecuteChanged();
        ToggleCommand.NotifyCanExecuteChanged();
        UpdateCommand.NotifyCanExecuteChanged();
        ConfigureCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsInstallingChanged(bool value)
    {
        InstallSelectedVersionCommand.NotifyCanExecuteChanged();
        RedownloadInstallSelectedVersionCommand.NotifyCanExecuteChanged();
        UninstallCommand.NotifyCanExecuteChanged();
        UninstallManualModCommand.NotifyCanExecuteChanged();
        ToggleCommand.NotifyCanExecuteChanged();
        UpdateCommand.NotifyCanExecuteChanged();
        ConfigureCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsLoadingReleasesChanged(bool value)
    {
        ViewReleaseNotesCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedReleaseChanged(GitHubRelease? value)
    {
        ViewReleaseNotesCommand.NotifyCanExecuteChanged();
        InstallSelectedVersionCommand.NotifyCanExecuteChanged();
        RedownloadInstallSelectedVersionCommand.NotifyCanExecuteChanged();
        _ = UpdateSelectedReleaseBackupStateAsync(value);
    }

    [RelayCommand(CanExecute = nameof(CanViewReleaseNotes))]
    private void ViewReleaseNotes()
    {
        if (SelectedRelease == null || string.IsNullOrWhiteSpace(SelectedRelease.Body))
            return;

        var repoUrl = Mod?.GitHubRepositoryUrl ?? "https://github.com/";
        var releaseUrl = $"{repoUrl}/releases/tag/{Uri.EscapeDataString(SelectedRelease.TagName)}";
        var window = new ReleaseNotesWindow(SelectedRelease.TagName, SelectedRelease.Body, releaseUrl)
        {
            Owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
                    ?? Application.Current?.MainWindow
        };
        window.ShowDialog();
    }

    private bool CanViewReleaseNotes() => !IsLoadingReleases && !string.IsNullOrWhiteSpace(SelectedRelease?.Body);

    [RelayCommand(CanExecute = nameof(CanViewReadme))]
    private async Task ViewReadmeAsync()
    {
        if (Mod == null || string.IsNullOrWhiteSpace(Mod.GitHubRepositoryUrl))
            return;

        try
        {
            IsLoadingReadme = true;
            var segments = new Uri(Mod.GitHubRepositoryUrl).AbsolutePath.Trim('/').Split('/');
            if (segments.Length < 2)
                throw new InvalidOperationException("GitHub repository URL is invalid.");

            var (content, baseUrl) = await _networkService.GetGitHubReadmeAsync(segments[0], segments[1]);
            var window = new ReleaseNotesWindow("README", content, baseUrl)
            {
                Owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
                        ?? Application.Current?.MainWindow
            };
            window.ShowDialog();
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, "Failed to load GitHub README for mod {0}", Mod.Id);
            StatusMessage = Strings.ReadmeLoadFailed;
        }
        finally
        {
            IsLoadingReadme = false;
        }
    }

    private bool CanViewReadme() => !IsLoadingReadme && !string.IsNullOrWhiteSpace(Mod?.GitHubRepositoryUrl);

    partial void OnIsLoadingReadmeChanged(bool value)
    {
        ViewReadmeCommand.NotifyCanExecuteChanged();
    }

    private async Task UpdateSelectedReleaseBackupStateAsync(GitHubRelease? release)
    {
        if (Mod == null || release == null)
        {
            HasSelectedReleaseBackup = false;
            return;
        }

        HasSelectedReleaseBackup = await _modBackupService.CheckModBackupExistsAsync(Mod.Id, release.TagName);
    }
}
