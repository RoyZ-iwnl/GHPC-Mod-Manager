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

// 配置项视图模型
public partial class ConfigurationItem : ObservableObject
{
    [ObservableProperty]
    private string _key = string.Empty;
    
    [ObservableProperty] 
    private object _value = string.Empty;
    
    public ConfigurationItem(string key, object value)
    {
        Key = key;
        Value = value;
    }
}

public partial class MainViewModel : ObservableObject
{
    private readonly IModManagerService _modManagerService;
    private readonly ITranslationManagerService _translationManagerService;
    private readonly IProcessService _processService;
    private readonly INavigationService _navigationService;
    private readonly ILoggingService _loggingService;
    private readonly IServiceProvider _serviceProvider;
    private readonly ISettingsService _settingsService;

    [ObservableProperty]
    private ObservableCollection<ModViewModel> _mods = new();

    [ObservableProperty]
    private ModViewModel? _selectedMod;

    [ObservableProperty]
    private bool _isGameRunning;

    [ObservableProperty]
    private bool _isTranslationInstalled;

    [ObservableProperty]
    private List<string> _availableLanguages = new();

    [ObservableProperty]
    private string _currentTranslationLanguage = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isDownloading;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private int _selectedTabIndex = 0;

    public MainViewModel(
        IModManagerService modManagerService,
        ITranslationManagerService translationManagerService,
        IProcessService processService,
        INavigationService navigationService,
        ILoggingService loggingService,
        IServiceProvider serviceProvider,
        ISettingsService settingsService)
    {
        _modManagerService = modManagerService;
        _translationManagerService = translationManagerService;
        _processService = processService;
        _navigationService = navigationService;
        _loggingService = loggingService;
        _serviceProvider = serviceProvider;
        _settingsService = settingsService;

        _processService.GameRunningStateChanged += OnGameRunningStateChanged;
        
        InitializeAsync();
    }

    private async void InitializeAsync()
    {
        await RefreshDataAsync();
    }

    [RelayCommand]
    private async Task RefreshDataAsync()
    {
        try
        {
            IsLoading = true;
            StatusMessage = Strings.RefreshingData;

            // Load mods
            var modList = await _modManagerService.GetModListAsync();
            Mods.Clear();
            foreach (var mod in modList)
            {
                Mods.Add(mod);
            }

            // Check translation status
            IsTranslationInstalled = await _translationManagerService.IsTranslationInstalledAsync();

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

    [RelayCommand]
    private async Task InstallModAsync(ModViewModel mod)
    {
        if (mod.LatestVersion == null || mod.LatestVersion == "Unknown")
        {
            MessageBox.Show(Strings.CannotGetModVersionInfo, Strings.Error, MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        try
        {
            IsDownloading = true;
            StatusMessage = string.Format(Strings.Installing, mod.DisplayName);
            
            var progress = new Progress<DownloadProgress>(downloadProgress =>
            {
                var speedText = downloadProgress.GetFormattedSpeed();
                var progressText = downloadProgress.GetFormattedProgress();
                var percentage = downloadProgress.ProgressPercentage;
                
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
            IsDownloading = false;
        }
    }

    [RelayCommand]
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
            StatusMessage = string.Format(Strings.UninstallingMod_, mod.DisplayName);
            
            if (mod.IsManuallyInstalled)
            {
                // Show warning for manual mods with option to proceed
                var manualUninstallResult = MessageBox.Show(
                    string.Format(Strings.ManualModUninstallWarning, mod.DisplayName),
                    Strings.ManualModUninstall,
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                    
                if (manualUninstallResult != MessageBoxResult.Yes) return;
                
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
            _loggingService.LogWarning("Cannot update mod: {0}, CanUpdate is false", mod.Id);
            return;
        }

        try
        {
            IsDownloading = true;
            StatusMessage = string.Format(Strings.UpdatingMod, mod.DisplayName, mod.InstalledVersion, mod.LatestVersion);
            
            var progress = new Progress<DownloadProgress>(downloadProgress =>
            {
                var speedText = downloadProgress.GetFormattedSpeed();
                var progressText = downloadProgress.GetFormattedProgress();
                var percentage = downloadProgress.ProgressPercentage;
                
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
            IsDownloading = false;
        }
    }

    [RelayCommand]
    private async Task InstallTranslationAsync()
    {
        try
        {
            // Show version selection dialog (simplified for now)
            var releases = await _translationManagerService.GetXUnityReleasesAsync();
            var latestVersion = releases.FirstOrDefault()?.TagName;

            if (string.IsNullOrEmpty(latestVersion))
            {
                MessageBox.Show(Strings.CannotGetTranslationVersions, Strings.Error, MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            IsDownloading = true;
            StatusMessage = Strings.InstallingTranslationSystem;
            
            var progress = new Progress<DownloadProgress>(downloadProgress =>
            {
                var speedText = downloadProgress.GetFormattedSpeed();
                var progressText = downloadProgress.GetFormattedProgress();
                var percentage = downloadProgress.ProgressPercentage;
                
                StatusMessage = $"{Strings.InstallingTranslationSystem} - {percentage:F1}% ({progressText}) - {speedText}";
            });
            
            var success = await _translationManagerService.InstallTranslationAsync(latestVersion, progress);

            if (success)
            {
                StatusMessage = Strings.TranslationSystemInstallSuccessful;
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
            IsDownloading = false;
        }
    }

    [RelayCommand]
    private async Task UpdateTranslationAsync()
    {
        try
        {
            StatusMessage = Strings.UpdatingTranslationFiles;
            var success = await _translationManagerService.UpdateTranslationFilesAsync();

            if (success)
            {
                StatusMessage = Strings.TranslationFilesUpdateSuccessful;
                await RefreshDataAsync();
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
    }

    [RelayCommand]
    private async Task UninstallTranslationAsync()
    {
        var result = MessageBox.Show(
            Strings.ConfirmUninstallTranslation,
            Strings.ConfirmUninstall,
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

        try
        {
            StatusMessage = Strings.UninstallingTranslationSystem;
            var success = await _translationManagerService.UninstallTranslationAsync();

            if (success)
            {
                StatusMessage = Strings.TranslationSystemUninstalledSuccessfully;
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

    [RelayCommand]
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

    [RelayCommand(CanExecute = nameof(CanExecuteWhenNotDownloading))]
    private async Task LaunchGameAsync()
    {
        try
        {
            var gameRootPath = _settingsService.Settings.GameRootPath;
            if (string.IsNullOrEmpty(gameRootPath))
            {
                MessageBox.Show(Strings.GamePathNotSet, Strings.Error, MessageBoxButton.OK, MessageBoxImage.Error);
                return;
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

    [RelayCommand]
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

    [RelayCommand(CanExecute = nameof(CanExecuteWhenNotDownloading))]
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
            MessageBox.Show(string.Format(Strings.UnableToOpenGitHubPage, ex.Message), Strings.Error, MessageBoxButton.OK, MessageBoxImage.Error);
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
                MessageBox.Show(Strings.GameDirectoryNotSetOrNotFound, Strings.Error, MessageBoxButton.OK, MessageBoxImage.Error);
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
            MessageBox.Show(string.Format(Strings.UnableToOpenGameDirectory, ex.Message), Strings.Error, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnGameRunningStateChanged(object? sender, bool isRunning)
    {
        IsGameRunning = isRunning;
        StatusMessage = isRunning ? Strings.GameRunningCannotOperate : Strings.Ready;
    }

    private bool CanExecuteWhenNotDownloading()
    {
        return !IsDownloading;
    }

    partial void OnIsDownloadingChanged(bool value)
    {
        LaunchGameCommand.NotifyCanExecuteChanged();
        OpenSettingsCommand.NotifyCanExecuteChanged();
    }
}