using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GHPC_Mod_Manager.Models;
using GHPC_Mod_Manager.Services;
using GHPC_Mod_Manager.Views;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.ObjectModel;
using System.Windows;
using GHPC_Mod_Manager.Resources;
using System.Linq;

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
    // Static flag to ensure startup update check runs only once per application session
    private static bool _hasPerformedStartupUpdateCheck = false;
    private static readonly object _updateCheckLock = new object();

    // Instance flag to prevent re-initialization when navigating back from settings
    private bool _isInitialized = false;

    private readonly IModManagerService _modManagerService;
    private readonly ITranslationManagerService _translationManagerService;
    private readonly ITranslationBackupService _translationBackupService;
    private readonly IProcessService _processService;
    private readonly INavigationService _navigationService;
    private readonly ILoggingService _loggingService;
    private readonly IServiceProvider _serviceProvider;
    private readonly ISettingsService _settingsService;
    private readonly INetworkService _networkService;
    private readonly IUpdateService _updateService;

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

    public MainViewModel(
        IModManagerService modManagerService,
        ITranslationManagerService translationManagerService,
        ITranslationBackupService translationBackupService,
        IProcessService processService,
        INavigationService navigationService,
        ILoggingService loggingService,
        IServiceProvider serviceProvider,
        ISettingsService settingsService,
        INetworkService networkService,
        IUpdateService updateService)
    {
        _modManagerService = modManagerService;
        _translationManagerService = translationManagerService;
        _translationBackupService = translationBackupService;
        _processService = processService;
        _navigationService = navigationService;
        _loggingService = loggingService;
        _serviceProvider = serviceProvider;
        _settingsService = settingsService;
        _networkService = networkService;
        _updateService = updateService;

        _processService.GameRunningStateChanged += OnGameRunningStateChanged;
        
        InitializeAsync();
        
        // Subscribe to search text changes
        PropertyChanged += (sender, e) => 
        {
            if (e.PropertyName == nameof(SearchText))
            {
                FilterAndSortMods();
            }
        };
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

        // 先加载数据,不阻塞
        await RefreshDataAsync();

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
                    await Task.Delay(2000); // Brief delay to avoid slowing down initial load
                    await CheckForAppUpdatesSilentlyAsync();
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
            var (hasUpdate, latestVersion, downloadUrl) = await _updateService.CheckForUpdatesAsync();

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

    [RelayCommand]
    private async Task RefreshDataAsync()
    {
        try
        {
            IsLoading = true;
            StatusMessage = Strings.RefreshingData;

            // Clear any rate limit blocks to allow fresh API requests
            _networkService.ClearRateLimitBlocks();

            // Load mods
            var modList = await _modManagerService.GetModListAsync();
            Mods.Clear();
            foreach (var mod in modList)
            {
                Mods.Add(mod);
            }
            
            // Apply filtering and sorting
            FilterAndSortMods();

            // Check translation status
            IsTranslationInstalled = await _translationManagerService.IsTranslationInstalledAsync();
            IsTranslationManuallyInstalled = await _translationManagerService.IsTranslationManuallyInstalledAsync();
            IsTranslationPluginEnabled = await _translationManagerService.IsTranslationPluginEnabledAsync();

            // 分别检查翻译插件和资源更新（使用默认缓存行为）
            IsTranslationPluginUpdateAvailable = await _translationManagerService.IsXUnityUpdateAvailableAsync();
            IsTranslationResourceUpdateAvailable = await _translationManagerService.IsTranslationUpdateAvailableAsync();
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
            _loggingService.LogWarning(Strings.CannotUpdateModCanUpdateFalse, mod.Id);
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
    private async Task ReinstallModAsync(ModViewModel mod)
    {
        if (!mod.CanReinstall)
        {
            _loggingService.LogWarning(Strings.CannotReinstallModCanReinstallFalse, mod.Id);
            return;
        }

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
            
            // Reinstall the mod using the regular installation method
            // This will convert it from manually installed to managed
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

    [RelayCommand(CanExecute = nameof(CanExecuteModOperations))]
    private async Task UpdateTranslationAsync()
    {
        // Check if translation was manually installed
        if (IsTranslationManuallyInstalled)
        {
            MessageBox.Show(
                Strings.CannotUpdateManuallyInstalledTranslation,
                Strings.Error,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        try
        {
            IsTranslationUpdating = true;
            StatusMessage = Strings.UpdatingTranslationFiles;

            var progress = new Progress<DownloadProgress>(downloadProgress =>
            {
                var speedText = downloadProgress.GetFormattedSpeed();
                var progressText = downloadProgress.GetFormattedProgress();
                var percentage = downloadProgress.ProgressPercentage;

                StatusMessage = $"{Strings.UpdatingTranslationFiles} - {percentage:F1}% ({progressText}) - {speedText}";
            });

            var success = await _translationManagerService.UpdateTranslationFilesAsync(progress);

            if (success)
            {
                StatusMessage = Strings.TranslationFilesUpdateSuccessful;
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
            IsTranslationUpdating = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanExecuteModOperations))]
    private async Task UpdateTranslationPluginAsync()
    {
        // Check if translation was manually installed
        if (IsTranslationManuallyInstalled)
        {
            MessageBox.Show(
                Strings.CannotUpdateManuallyInstalledTranslation,
                Strings.Error,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        try
        {
            IsTranslationUpdating = true;
            StatusMessage = Strings.UpdatingTranslationPlugin;

            var progress = new Progress<DownloadProgress>(downloadProgress =>
            {
                var speedText = downloadProgress.GetFormattedSpeed();
                var progressText = downloadProgress.GetFormattedProgress();
                var percentage = downloadProgress.ProgressPercentage;

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
            IsTranslationUpdating = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanExecuteModOperations))]
    private async Task UninstallTranslationAsync()
    {
        // Check if translation was manually installed
        if (IsTranslationManuallyInstalled)
        {
            MessageBox.Show(
                Strings.CannotUninstallManuallyInstalledTranslation,
                Strings.Error,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

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
                MessageBox.Show(Strings.GamePathNotSet, Strings.Error, MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            StatusMessage = Strings.CheckingModConflictsAndDependencies;

            // 检查MOD冲突
            var (hasConflicts, conflicts) = await _modManagerService.CheckEnabledModsConflictsAsync();
            if (hasConflicts)
            {
                var conflictMessages = conflicts.Select(c =>
                    string.Format(Strings.ModConflictsWith,
                        GetModDisplayName(c.ModId),
                        GetModDisplayName(c.ConflictsWith)))
                    .ToList();

                var conflictText = string.Join("\n", conflictMessages);
                var result = MessageBox.Show(
                    string.Format(Strings.ConflictWarningMessage, conflictText),
                    Strings.ConflictDetectedOnLaunch,
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result == MessageBoxResult.No)
                {
                    StatusMessage = Strings.Ready;
                    return;
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
                var result = MessageBox.Show(
                    string.Format(Strings.DependencyWarningMessage, depText),
                    Strings.DependencyMissingOnLaunch,
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result == MessageBoxResult.No)
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

    [RelayCommand]
    private void GoToTranslationManagement()
    {
        SelectedTabIndex = 1; // Translation Management tab index
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
                MessageBox.Show($"{string.Format(Strings.ModUpdatesFound, updatesFound)}\n\n{updatesList}",
                    Strings.Information, MessageBoxButton.OK, MessageBoxImage.Information);
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
            var (hasUpdate, latestVersion, downloadUrl) = await _updateService.CheckForUpdatesAsync();

            if (hasUpdate && !string.IsNullOrEmpty(latestVersion) && !string.IsNullOrEmpty(downloadUrl))
            {
                var result = MessageBox.Show(
                    $"New version available: {latestVersion}\nCurrent version: {currentVersion}\n\nDo you want to download and install the update?",
                    "Update Available",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);

                if (result == MessageBoxResult.Yes)
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

                    var success = await _updateService.DownloadAndInstallUpdateAsync(downloadUrl, latestVersion, progress);

                    if (!success)
                    {
                        StatusMessage = "Update download failed";
                        MessageBox.Show("Failed to download or install the update. Please try again later.", Strings.Error, MessageBoxButton.OK, MessageBoxImage.Error);
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
                MessageBox.Show($"You are using the latest version ({currentVersion})", "No Updates", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.ApplicationUpdateCheckFailed);
            StatusMessage = "Update check failed";
            MessageBox.Show($"Failed to check for updates: {ex.Message}", Strings.Error, MessageBoxButton.OK, MessageBoxImage.Error);
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

            var result = MessageBox.Show(
                $"{Strings.NewVersionAvailable} {LatestAppVersion}\n{Strings.ApplicationVersion}: {_updateService.GetCurrentVersion()}\n\n{Strings.DoYouWantToDownloadAndInstall}",
                Strings.CheckForUpdates,
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);

            if (result == MessageBoxResult.Yes)
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
                    MessageBox.Show(Strings.FailedToDownloadOrInstall, Strings.Error, MessageBoxButton.OK, MessageBoxImage.Error);
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
            MessageBox.Show(Strings.FailedToDownloadOrInstall, Strings.Error, MessageBoxButton.OK, MessageBoxImage.Error);
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
            // 尝试连接到 GHPC.DMR.GG
            var isConnected = await _networkService.CheckNetworkConnectionAsync();

            if (!isConnected)
            {
                IsOfflineMode = true;
                _loggingService.LogWarning(Strings.EnteringOfflineMode);

                // 弹窗提示用户
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    MessageBox.Show(
                        Strings.OfflineModeMessage,
                        Strings.OfflineModeTitle,
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
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
    }

    partial void OnIsTranslationUpdatingChanged(bool value)
    {
        UpdateAllCommandsCanExecute();
    }

    partial void OnIsCheckingUpdatesChanged(bool value)
    {
        UpdateAllCommandsCanExecute();
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
        
        // Sort: Installed mods first, then by display name
        var sortedMods = allMods
            .OrderByDescending(m => m.IsInstalled)
            .ThenBy(m => m.DisplayName)
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