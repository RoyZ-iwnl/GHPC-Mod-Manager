using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GHPC_Mod_Manager.Models;
using GHPC_Mod_Manager.Services;
using GHPC_Mod_Manager.Views;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.ObjectModel;
using System.Windows;
using GHPC_Mod_Manager.Resources;

namespace GHPC_Mod_Manager.ViewModels;

public partial class InstalledModsViewModel : ObservableObject
{
    private readonly IModManagerService _modManagerService;
    private readonly ILoggingService _loggingService;
    private readonly IServiceProvider _serviceProvider;
    private readonly ISettingsService _settingsService;

    // 所有已安装MOD（原始数据，由MainViewModel注入）
    private List<ModViewModel> _allInstalledMods = new();

    [ObservableProperty]
    private ObservableCollection<ModViewModel> _filteredInstalledMods = new();

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _installedModCount = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isDownloading;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    // 通知MainViewModel导航到详情页
    public event EventHandler<ModViewModel>? NavigateToDetailRequested;

    // 通知MainViewModel刷新数据
    public event EventHandler? RefreshRequested;

    // 通知MainViewModel执行检查更新（true=只检查已安装）
    public event EventHandler<bool>? CheckForUpdatesRequested;

    public InstalledModsViewModel(IModManagerService modManagerService, ILoggingService loggingService, IServiceProvider serviceProvider, ISettingsService settingsService)
    {
        _modManagerService = modManagerService;
        _loggingService = loggingService;
        _serviceProvider = serviceProvider;
        _settingsService = settingsService;
    }

    partial void OnSearchTextChanged(string value) => FilterMods();

    /// <summary>
    /// 由MainViewModel在数据加载完成后调用，注入已安装MOD列表
    /// </summary>
    public void SetModsSource(IEnumerable<ModViewModel> allMods)
    {
        _allInstalledMods = allMods.Where(m => m.IsInstalled).ToList();
        FilterMods();

        // 用settings中的语言，避免异步上下文中CultureInfo不可靠
        var lang = _settingsService.Settings.Language;
        foreach (var mod in _allInstalledMods)
            mod.RefreshLocalization(lang);

        UpdateDependencyStatusAsync();
    }

    private void FilterMods()
    {
        var filtered = string.IsNullOrWhiteSpace(SearchText)
            ? _allInstalledMods
            : _allInstalledMods.Where(m =>
                m.DisplayName.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                m.Id.Contains(SearchText, StringComparison.OrdinalIgnoreCase)).ToList();

        FilteredInstalledMods = new ObservableCollection<ModViewModel>(filtered);
        InstalledModCount = string.Format(Strings.InstalledModsCount, _allInstalledMods.Count);
    }

    /// <summary>
    /// 后台更新所有已安装MOD的依赖状态图标
    /// </summary>
    private async void UpdateDependencyStatusAsync()
    {
        foreach (var mod in _allInstalledMods.Where(m => m.Config?.Requirements?.Any() == true))
        {
            try
            {
                var (allSatisfied, _) = await _modManagerService.CheckSingleModDependenciesAsync(mod.Id);
                mod.DependencyStatus = allSatisfied ? DependencyStatus.Satisfied : DependencyStatus.Missing;
            }
            catch
            {
                mod.DependencyStatus = DependencyStatus.Unknown;
            }
        }
        // 刷新列表以更新图标显示
        FilterMods();
    }

    [RelayCommand(CanExecute = nameof(CanExecuteModOps))]
    private async Task UpdateModAsync(ModViewModel mod)
    {
        if (mod.LatestVersion == null) return;
        try
        {
            IsDownloading = true;
            StatusMessage = string.Format(Strings.Updating, mod.DisplayName);

            var progress = new Progress<DownloadProgress>(p =>
            {
                StatusMessage = $"{string.Format(Strings.Updating, mod.DisplayName)} - {p.ProgressPercentage:F1}% - {p.GetFormattedSpeed()}";
            });

            var success = await _modManagerService.UpdateModAsync(mod.Id, mod.LatestVersion, progress);
            StatusMessage = success
                ? string.Format(Strings.UpdateSuccessful, mod.DisplayName)
                : string.Format(Strings.UpdateFailed, mod.DisplayName);

            if (success) RefreshRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.ModUpdateError, mod.Id);
            StatusMessage = string.Format(Strings.UpdateFailed, mod.DisplayName);
        }
        finally
        {
            IsDownloading = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanExecuteModOps))]
    private async Task UninstallModAsync(ModViewModel mod)
    {
        var result = MessageBox.Show(
            string.Format(Strings.ConfirmUninstallMod, mod.DisplayName),
            Strings.ConfirmUninstall,
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

        try
        {
            IsDownloading = true;
            StatusMessage = string.Format(Strings.Uninstalling, mod.DisplayName);

            var success = mod.IsManuallyInstalled && mod.IsUnsupportedManualMod
                ? await _modManagerService.UninstallManualModAsync(mod.Id)
                : await _modManagerService.UninstallModAsync(mod.Id);

            StatusMessage = success
                ? string.Format(Strings.UninstallSuccessful, mod.DisplayName)
                : string.Format(Strings.UninstallFailed, mod.DisplayName);

            if (success) RefreshRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.ModUninstallError, mod.Id);
            StatusMessage = string.Format(Strings.UninstallFailed, mod.DisplayName);
        }
        finally
        {
            IsDownloading = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanExecuteModOps))]
    private async Task ToggleModAsync(ModViewModel mod)
    {
        try
        {
            var success = mod.IsEnabled
                ? await _modManagerService.DisableModAsync(mod.Id)
                : await _modManagerService.EnableModAsync(mod.Id);

            if (success) RefreshRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.ModToggleError, mod.Id);
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
        }
    }

    [RelayCommand]
    private void NavigateToDetail(ModViewModel mod)
    {
        NavigateToDetailRequested?.Invoke(this, mod);
    }

    [RelayCommand]
    private void CheckForUpdates()
    {
        // 已安装页只检查已安装的MOD
        CheckForUpdatesRequested?.Invoke(this, true);
    }

    [RelayCommand]
    private void Refresh()
    {
        RefreshRequested?.Invoke(this, EventArgs.Empty);
    }

    private bool CanExecuteModOps() => !IsDownloading;

    partial void OnIsDownloadingChanged(bool value)
    {
        UpdateModCommand.NotifyCanExecuteChanged();
        UninstallModCommand.NotifyCanExecuteChanged();
        ToggleModCommand.NotifyCanExecuteChanged();
    }
}
