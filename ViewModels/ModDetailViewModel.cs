using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GHPC_Mod_Manager.Models;
using GHPC_Mod_Manager.Services;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using GHPC_Mod_Manager.Resources;

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
}

public partial class ModDetailViewModel : ObservableObject
{
    private readonly IModManagerService _modManagerService;
    private readonly ILoggingService _loggingService;
    private readonly ISettingsService _settingsService;

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
    private bool _isInstalling;

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

    public ModDetailViewModel(IModManagerService modManagerService, ILoggingService loggingService, ISettingsService settingsService)
    {
        _modManagerService = modManagerService;
        _loggingService = loggingService;
        _settingsService = settingsService;
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
            Dependencies.Add(new RelatedModInfo
            {
                ModId = reqId,
                DisplayName = relatedMod?.DisplayName ?? reqId,
                IsInstalled = relatedMod?.IsInstalled ?? false,
                IsEnabled = relatedMod?.IsEnabled ?? false
            });
        }

        // 构建冲突列表
        foreach (var conflictId in Mod.Config.Conflicts ?? new List<string>())
        {
            var relatedMod = _allMods.FirstOrDefault(m => m.Id == conflictId);
            Conflicts.Add(new RelatedModInfo
            {
                ModId = conflictId,
                DisplayName = relatedMod?.DisplayName ?? conflictId,
                IsInstalled = relatedMod?.IsInstalled ?? false,
                IsEnabled = relatedMod?.IsEnabled ?? false
            });
        }

        HasDependencies = Dependencies.Count > 0;
        HasConflicts = Conflicts.Count > 0;
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
    {
        if (Mod == null || SelectedRelease == null) return;

        try
        {
            IsInstalling = true;
            StatusMessage = string.Format(Strings.Installing, Mod.DisplayName);

            var progress = new Progress<DownloadProgress>(p =>
            {
                StatusMessage = $"{string.Format(Strings.Installing, Mod.DisplayName)} - {p.ProgressPercentage:F1}% - {p.GetFormattedSpeed()}";
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
                    var firstResult = MessageBox.Show(msg, Strings.ConflictDialogTitle,
                        MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (firstResult != MessageBoxResult.Yes) return;

                    // 二次确认
                    var secondResult = MessageBox.Show(
                        Strings.ConflictInstallConfirm,
                        Strings.ConflictDialogTitle,
                        MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (secondResult != MessageBoxResult.Yes) return;

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
                    var missingNames = string.Join("\n• ", missingIds);
                    var msg = $"{Strings.DependencyDialogMessage}\n• {missingNames}";
                    var result = MessageBox.Show(msg, Strings.DependencyDialogTitle,
                        MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                    if (result != MessageBoxResult.OK) return;

                    // 跳转到第一个缺失依赖的详情页
                    var firstMissingId = missingIds.First();
                    NavigateToModRequested?.Invoke(this, firstMissingId);
                    return;
                }
                skipDepCheck = true;
            }

            var success = await _modManagerService.InstallModAsync(
                Mod.Config!, SelectedRelease.TagName, progress, skipDependencyCheck: skipDepCheck);

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

        var result = MessageBox.Show(
            string.Format(Strings.ConfirmUninstallMod, Mod.DisplayName),
            Strings.ConfirmUninstall,
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        try
        {
            IsInstalling = true;
            StatusMessage = string.Format(Strings.UninstallingMod_, Mod.DisplayName);
            var success = await _modManagerService.UninstallModAsync(Mod.Id);
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
            StatusMessage = string.Format(Strings.UpdatingMod, Mod.DisplayName, Mod.InstalledVersion, SelectedRelease.TagName);

            var progress = new Progress<DownloadProgress>(p =>
            {
                StatusMessage = $"{string.Format(Strings.UpdatingMod, Mod.DisplayName, Mod.InstalledVersion, SelectedRelease.TagName)} - {p.ProgressPercentage:F1}% - {p.GetFormattedSpeed()}";
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
        finally { IsInstalling = false; }
    }

    // 通知MainViewModel打开配置窗口
    public event EventHandler<string>? ConfigureModRequested;

    [RelayCommand(CanExecute = nameof(CanManage))]
    private void Configure()
    {
        if (Mod != null)
            ConfigureModRequested?.Invoke(this, Mod.Id);
    }

    private bool CanInstall() => !IsInstalling && SelectedRelease != null && Mod != null;
    private bool CanManage() => !IsInstalling && Mod != null;

    partial void OnIsInstallingChanged(bool value)
    {
        InstallSelectedVersionCommand.NotifyCanExecuteChanged();
        UninstallCommand.NotifyCanExecuteChanged();
        ToggleCommand.NotifyCanExecuteChanged();
        UpdateCommand.NotifyCanExecuteChanged();
        ConfigureCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedReleaseChanged(GitHubRelease? value) => InstallSelectedVersionCommand.NotifyCanExecuteChanged();
}
