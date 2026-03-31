using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GHPC_Mod_Manager.Models;
using GHPC_Mod_Manager.Services;
using GHPC_Mod_Manager.Helpers;
using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Windows;
using GHPC_Mod_Manager.Resources;

namespace GHPC_Mod_Manager.ViewModels;

public partial class SetupWizardViewModel : ObservableObject
{
    private readonly ISettingsService _settingsService;
    private readonly INavigationService _navigationService;
    private readonly INetworkService _networkService;
    private readonly IMelonLoaderService _melonLoaderService;
    private readonly IProcessService _processService;
    private readonly ILoggingService _loggingService;
    private readonly ISteamGameFinderService _steamGameFinder;
    private readonly IMainConfigService _mainConfigService;
    private readonly IUpdateService _updateService;
    private readonly IPreviousInstallationService _previousInstallationService;
    private bool _isInitializing = true;
    private bool _hasShownBepInExWarning;
    private bool _hasShownSteamWarning;
    private bool _autoSearchAttempted;

    [ObservableProperty]
    private int _currentStep = 0;

    partial void OnCurrentStepChanged(int value)
    {
        // 进入代理设置步骤(步骤2)时，执行完整的网络检测流程
        if (value == 2)
        {
            _ = PerformNetworkDetectionAsync();
        }

        // 当步骤切换到游戏目录选择(步骤3)时，自动触发搜索
        if (value == 3 && !_autoSearchAttempted)
        {
            _autoSearchAttempted = true;
            _ = Task.Run(async () => await AutoSearchGHPCAsync());
        }

        OnPropertyChanged(nameof(ShowMelonLoaderReleaseLoadWarning));
        UpdateNavigationButtons();
    }

    [ObservableProperty]
    private bool _isNextEnabled = true;

    [ObservableProperty]
    private bool _isBackEnabled = false;

    [ObservableProperty]
    private string _selectedLanguage = "zh-CN";

    [ObservableProperty]
    private bool _isNetworkAvailable = true;

    [ObservableProperty]
    private string _gameRootPath = string.Empty;

    [ObservableProperty]
    private bool _isMelonLoaderInstalled;

    [ObservableProperty]
    private bool _areMelonLoaderDirsCreated;

    // 计算属性：只有当MelonLoader已安装但目录未创建时才显示警告
    public bool ShowMelonLoaderDirWarning => IsMelonLoaderInstalled && !AreMelonLoaderDirsCreated;

    // 重写属性设置器以触发计算属性通知
    partial void OnIsMelonLoaderInstalledChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowMelonLoaderDirWarning));
    }

    partial void OnAreMelonLoaderDirsCreatedChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowMelonLoaderDirWarning));
    }

    [ObservableProperty]
    private List<GitHubRelease> _melonLoaderReleases = new();

    [ObservableProperty]
    private GitHubRelease? _selectedMelonLoaderVersion;

    [ObservableProperty]
    private bool _isLoadingMelonLoaderReleases;

    [ObservableProperty]
    private bool _melonLoaderReleasesLoadFailed;

    [ObservableProperty]
    private bool _isInstalling;

    [ObservableProperty]
    private double _installProgress;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isGameRunning;

    [ObservableProperty]
    private bool _isCheckingNetwork;

    [ObservableProperty]
    private string _networkCheckLog = string.Empty;

    [ObservableProperty]
    private bool _showNetworkLog;

    [ObservableProperty]
    private bool _showNetworkFailed;

    [ObservableProperty]
    private bool _useGitHubProxy = false;

    public bool ShowMelonLoaderReleaseLoadWarning => CurrentStep == 5 && MelonLoaderReleasesLoadFailed && !IsLoadingMelonLoaderReleases;

    public string MelonLoaderReleaseLoadWarningMessage =>
        SelectedLanguage == "zh-CN"
            ? Strings.ResourceManager.GetString("MelonLoaderReleaseLoadWarningZh", Strings.Culture) ?? "MelonLoader 版本列表加载失败，请检查网络连接。可以尝试使用代理加速或切换节点后重试。"
            : Strings.ResourceManager.GetString("MelonLoaderReleaseLoadWarning", Strings.Culture) ?? "Failed to load the MelonLoader version list. Please check your network connection and try again.";

    partial void OnSelectedLanguageChanged(string value)
    {
        OnPropertyChanged(nameof(MelonLoaderReleaseLoadWarningMessage));
    }

    partial void OnIsLoadingMelonLoaderReleasesChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowMelonLoaderReleaseLoadWarning));
    }

    partial void OnMelonLoaderReleasesLoadFailedChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowMelonLoaderReleaseLoadWarning));
    }

    partial void OnUseGitHubProxyChanged(bool value)
    {
        // Skip auto-save during initial loading to prevent overwriting settings
        if (_isInitializing)
            return;

        // 启用代理时清空token，并选中第一个节点
        if (value)
        {
            GitHubApiToken = string.Empty;

            if (AvailableProxyServers.Count > 0)
                SelectedProxyServer = AvailableProxyServers[0];
        }

        UpdateNavigationButtons();

        // Automatically save proxy settings when changed
        _ = Task.Run(async () =>
        {
            try
            {
                _settingsService.Settings.UseGitHubProxy = value;
                await _settingsService.SaveSettingsAsync();

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    var message = value ? Strings.GitHubProxyEnabled : Strings.GitHubProxyDisabled;
                    AddToNetworkLog(message);
                });
            }
            catch (Exception ex)
            {
                _loggingService.LogError(ex, Strings.FailedToSaveProxySettingChange);
            }
        });
    }

    [ObservableProperty]
    private List<ProxyServerItem> _availableProxyServers = ProxyServerItem.BuildFallback();

    [ObservableProperty]
    private ProxyServerItem? _selectedProxyServer;

    partial void OnSelectedProxyServerChanged(ProxyServerItem? value)
    {
        if (_isInitializing || value == null || SelectedLanguage != "zh-CN")
            return;

        UpdateNavigationButtons();

        // 同步保存枚举值到设置
        _ = Task.Run(async () =>
        {
            try
            {
                _settingsService.Settings.GitHubProxyServer = value.EnumValue;
                await _settingsService.SaveSettingsAsync();

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    var message = string.Format(Strings.ProxyServerChanged, value.EnumValue);
                    AddToNetworkLog(message);
                });
            }
            catch (Exception ex)
            {
                _loggingService.LogError(ex, Strings.FailedToSaveProxyServerChange);
            }
        });
    }

    [ObservableProperty]
    private string _gitHubApiToken = string.Empty;

    partial void OnGitHubApiTokenChanged(string value)
    {
        if (_isInitializing)
            return;

        // 配置token时禁用代理（已在UI线程，直接赋值）
        if (!string.IsNullOrWhiteSpace(value))
            UseGitHubProxy = false;

        _ = Task.Run(async () =>
        {
            try
            {
                _settingsService.Settings.GitHubApiToken = value;
                await _settingsService.SaveSettingsAsync();
            }
            catch (Exception ex)
            {
                _loggingService.LogError(ex, Strings.FailedToSaveProxySettingChange);
            }
        });
    }

    public SetupWizardViewModel(
        ISettingsService settingsService,
        INavigationService navigationService,
        INetworkService networkService,
        IMelonLoaderService melonLoaderService,
        IProcessService processService,
        ILoggingService loggingService,
        ISteamGameFinderService steamGameFinder,
        IMainConfigService mainConfigService,
        IUpdateService updateService,
        IPreviousInstallationService previousInstallationService)
    {
        _settingsService = settingsService;
        _navigationService = navigationService;
        _networkService = networkService;
        _melonLoaderService = melonLoaderService;
        _processService = processService;
        _loggingService = loggingService;
        _steamGameFinder = steamGameFinder;
        _mainConfigService = mainConfigService;
        _updateService = updateService;
        _previousInstallationService = previousInstallationService;

        _processService.GameRunningStateChanged += OnGameRunningStateChanged;
        
        InitializeAsync();
    }

    private async void InitializeAsync()
    {
        SelectedLanguage = _settingsService.Settings.Language;

        // Load existing proxy settings
        UseGitHubProxy = _settingsService.Settings.UseGitHubProxy;
        GitHubApiToken = _settingsService.Settings.GitHubApiToken;

        // 代理列表在进入步骤2时通过 RefreshProxyServersAsync 拉取，这里先用兜底列表
        var fallback = ProxyServerItem.BuildFallback();
        AvailableProxyServers = fallback;
        var savedEnum = _settingsService.Settings.GitHubProxyServer;
        SelectedProxyServer = fallback.FirstOrDefault(p => p.EnumValue == savedEnum) ?? fallback[0];

        // Mark initialization as complete after setting all values
        _isInitializing = false;
    }

    [RelayCommand]
    private async Task ApplyLanguageAsync()
    {
        try
        {
            _loggingService.LogInfo(string.Format(Strings.LanguageSelected, SelectedLanguage));
            _settingsService.Settings.Language = SelectedLanguage;
            await _settingsService.SaveSettingsAsync();
            _settingsService.ApplyLanguageSetting();
            
            // 显示重启提示
            var result = MessageDialogHelper.Confirm(
                Strings.LanguageChangedConfirmRestart,
                Strings.LanguageChanged);

            if (result)
            {
                // 重启应用程序
                RestartApplication();
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.ApplyLanguageError);
            StatusMessage = Strings.FailedToApplyLanguageSetting;
        }
    }
    
    private void RestartApplication()
    {
        try
        {
            // 获取当前应用程序的执行文件路径
            var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            
            if (!string.IsNullOrEmpty(exePath))
            {
                // 启动新的应用程序实例
                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exePath,
                    UseShellExecute = true,
                    WorkingDirectory = Environment.CurrentDirectory
                };
                
                System.Diagnostics.Process.Start(startInfo);
                
                // 关闭当前应用程序
                Application.Current.Shutdown();
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.RestartApplicationError);
            StatusMessage = Strings.FailedToRestartApplication;
        }
    }

    [RelayCommand]
    private async Task NextAsync()
    {
        switch (CurrentStep)
        {
            case 0: // Language Selection
                // 语言选择完成后，进入步骤1前检测旧安装
                if (_settingsService.Settings.IsFirstRun
                    && !CommandLineArgs.ShowLogWindow
                    && _previousInstallationService.HasPreviousInstallation())
                {
                    var previousPath = _previousInstallationService.GetPreviousAppPath()!;
                    var result = MessageDialogHelper.Show(
                        Strings.PreviousInstallationDetectedMessage,
                        Strings.PreviousInstallationDetectedTitle,
                        MessageDialogButton.OpenFolderIgnore,
                        MessageDialogImage.Warning);

                    if (result == MessageDialogResult.OpenFolder)
                    {
                        // 打开旧目录，不退出程序（让用户自己决定）
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = previousPath,
                            UseShellExecute = true,
                            Verb = "open"
                        });
                        return; // 不继续跳转
                    }
                    // Ignore → 继续正常进入步骤1
                }
                // 语言已经通过Apply按钮设置，这里只需要决定下一步
                CurrentStep = 1; // Go to Welcome Page
                break;

            case 1: // Welcome Page
                _loggingService.LogInfo(Strings.EnteringNetworkCheck);
                CurrentStep = 2; // Network Check
                break;

            case 2: // Network Check
                CurrentStep = 3;
                break;

            case 3: // Game Directory
                // 验证游戏目录并继续下一步
                if (await ValidateGameDirectoryWithSteamCheckAsync())
                {
                    CurrentStep = 4;
                    // Start preloading MelonLoader releases in background
                    _ = Task.Run(async () => await LoadMelonLoaderReleasesAsync());
                    await CheckMelonLoaderAsync();
                }
                break;

            case 4: // MelonLoader Check
                if (IsMelonLoaderInstalled)
                {
                    if (AreMelonLoaderDirsCreated)
                    {
                        // MelonLoader已安装且必要目录已创建，直接完成设置
                        await CompleteSetupAsync();
                    }
                    else
                    {
                        // MelonLoader已安装但必要目录未创建，需要运行游戏
                        StatusMessage = Strings.NeedRunGameToGenerateDirs;
                        CurrentStep = 6; // Wait for first run
                    }
                }
                else
                {
                    // MelonLoader未安装，进入安装步骤
                    CurrentStep = 5; // Install MelonLoader
                    await LoadMelonLoaderReleasesAsync();
                }
                break;

            case 5: // Install MelonLoader
                await InstallMelonLoaderAsync();
                break;

            case 6: // First Run
                await LaunchGameAsync();
                break;

            case 7: // Waiting for game exit
                break;
        }

        UpdateNavigationButtons();
    }

    [RelayCommand]
    private async Task BackAsync()
    {
        if (CurrentStep > 0)
        {
            CurrentStep--;
            UpdateNavigationButtons();
        }
    }

    [RelayCommand]
    private async Task BrowseGameDirectoryAsync()
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
                _hasShownBepInExWarning = false;
                _hasShownSteamWarning = false; // 重置Steam版本警告状态
                _settingsService.Settings.GameRootPath = GameRootPath;
                await _settingsService.SaveSettingsAsync();
                UpdateNavigationButtons();
            }
            else
            {
                MessageDialogHelper.ShowError(Strings.PleaseSelectGHPCExe, Strings.Error);
            }
        }
    }

    [RelayCommand]
    private async Task OpenNetworkHelpAsync()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://GHPC.DMR.gg/github.html",
                UseShellExecute = true
            });
            AddToNetworkLog(Strings.NetworkHelpPageOpened);
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.NetworkHelpOpenError);
            AddToNetworkLog(Strings.NetworkHelpPageOpenFailed);
        }
    }

    [RelayCommand]
    private async Task ApplyProxySettingsAsync()
    {
        try
        {
            AddToNetworkLog(Strings.ApplyingProxyAndRetesting);
            await PerformNetworkDetectionAsync();
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.FailedToApplyProxySettings);
            var errorMessage = string.Format(Strings.ApplyProxySettingsFailed, ex.Message);
            AddToNetworkLog(errorMessage);
        }
    }

    [RelayCommand]
    private async Task RetryNetworkCheckAsync()
    {
        AddToNetworkLog(Strings.UserClickedRetryNetworkCheck);
        await PerformNetworkDetectionAsync();
    }

    private void AddToNetworkLog(string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        NetworkCheckLog += $"[{timestamp}] {message}\n";
        ShowNetworkLog = true;
    }

    private void ClearNetworkLog()
    {
        NetworkCheckLog = string.Empty;
        ShowNetworkLog = false;
    }

    private async Task PerformNetworkDetectionAsync()
    {
        IsCheckingNetwork = true;
        ShowNetworkFailed = false;
        ClearNetworkLog();
        AddToNetworkLog(Strings.StartingNetworkDetection);

        try
        {
            // Phase 1: 测试主配置连通性
            AddToNetworkLog(Strings.FetchingMainConfig);
            var mainConfigResult = await _mainConfigService.TestMainConfigConnectivityAsync();

            if (mainConfigResult.Success)
            {
                AddToNetworkLog(string.Format(Strings.MainConfigFetchedFrom, mainConfigResult.SuccessfulUrl));
            }
            else
            {
                AddToNetworkLog(Strings.MainConfigFetchFailed);
                foreach (var failedUrl in mainConfigResult.FailedUrls)
                {
                    AddToNetworkLog($"  ✗ {failedUrl}");
                }
                AddToNetworkLog(Strings.WillUseFallbackProxyList);
            }

            // Phase 2: 更新代理服务器列表
            AddToNetworkLog(Strings.UpdatingProxyServerList);
            await UpdateProxyServerListAsync(mainConfigResult.Config);

            // Phase 3: 测试GitHub连通性
            AddToNetworkLog(Strings.TestingGitHubConnectivity);
            IsNetworkAvailable = await _networkService.CheckGitHubConnectionAsync();

            if (IsNetworkAvailable)
            {
                AddToNetworkLog(Strings.NetworkCheckSuccessful);
                ShowNetworkFailed = false;
            }
            else
            {
                AddToNetworkLog(Strings.GitHubConnectivityTestFailed);
                AddToNetworkLog(Strings.PossibleReasons);
                AddToNetworkLog(Strings.UnstableNetworkConnection);
                AddToNetworkLog(Strings.DNSResolutionProblem);
                AddToNetworkLog(Strings.FirewallBlocking);
                AddToNetworkLog(Strings.ProxyServerConfigProblem);
                ShowNetworkFailed = true;
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.NetworkDetectionException);
            IsNetworkAvailable = false;
            ShowNetworkFailed = true;
            AddToNetworkLog(string.Format(Strings.NetworkDetectionException, ex.Message));
        }
        finally
        {
            IsCheckingNetwork = false;
            AddToNetworkLog(string.Format(Strings.NetworkDetectionComplete,
                IsNetworkAvailable ? Strings.ConnectionNormalResult : Strings.ConnectionAbnormalResult));
            UpdateNavigationButtons();
        }
    }

    private async Task UpdateProxyServerListAsync(MainConfig? remoteConfig)
    {
        var remote = remoteConfig?.ProxyServers;
        var lang = _settingsService.Settings.Language;
        var newList = remote != null && remote.Count > 0
            ? ProxyServerItem.BuildFromRemote(remote, lang)
            : ProxyServerItem.BuildFallback();

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            AvailableProxyServers = newList;
            var savedEnum = _settingsService.Settings.GitHubProxyServer;
            SelectedProxyServer = newList.FirstOrDefault(p => p.EnumValue == savedEnum) ?? newList[0];
        });

        AddToNetworkLog(string.Format(Strings.ProxyServerListUpdated, newList.Count));
    }

    private async Task<bool> ValidateGameDirectoryAsync()
    {
        if (string.IsNullOrEmpty(GameRootPath))
        {
            MessageDialogHelper.ShowError(Strings.PleaseSelectGameDirectory, Strings.Error);
            return false;
        }

        var ghpcExe = Path.Combine(GameRootPath, "GHPC.exe");
        if (!File.Exists(ghpcExe))
        {
            MessageDialogHelper.ShowError(Strings.GHPCExeNotFoundInDirectory, Strings.Error);
            return false;
        }

        return true;
    }

    private async Task CheckMelonLoaderAsync()
    {
        IsMelonLoaderInstalled = await _melonLoaderService.IsMelonLoaderInstalledAsync(GameRootPath);

        if (IsMelonLoaderInstalled)
        {
            AreMelonLoaderDirsCreated = await _melonLoaderService.AreMelonLoaderDirectoriesCreatedAsync(GameRootPath);
        }

        CheckBepInExInstallation();
    }

    private void CheckBepInExInstallation()
    {
        if (string.IsNullOrEmpty(GameRootPath))
        {
            return;
        }

        var bepInExDirectory = Path.Combine(GameRootPath, "BepInEx");
        var winhttpPath = Path.Combine(GameRootPath, "winhttp.dll");
        var hasBepInEx = Directory.Exists(bepInExDirectory) || File.Exists(winhttpPath);

        if (hasBepInEx)
        {
            if (!_hasShownBepInExWarning)
            {
                _loggingService.LogWarning(Strings.BepInExDetectedLog, GameRootPath);
                MessageDialogHelper.ShowWarning(Strings.BepInExDetectedMessage, Strings.Warning);
                _hasShownBepInExWarning = true;
            }
        }
        else
        {
            _hasShownBepInExWarning = false;
        }
    }

    private async Task LoadMelonLoaderReleasesAsync()
    {
        IsLoadingMelonLoaderReleases = true;
        MelonLoaderReleasesLoadFailed = false;

        try
        {
            MelonLoaderReleases = await _melonLoaderService.GetMelonLoaderReleasesAsync();
            SelectedMelonLoaderVersion = MelonLoaderReleases.FirstOrDefault();

            if (SelectedMelonLoaderVersion == null)
            {
                MelonLoaderReleasesLoadFailed = true;
                StatusMessage = MelonLoaderReleaseLoadWarningMessage;
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.MelonLoaderReleasesLoadError);
            MelonLoaderReleasesLoadFailed = true;
            StatusMessage = MelonLoaderReleaseLoadWarningMessage;
        }
        finally
        {
            IsLoadingMelonLoaderReleases = false;
        }
    }

    private async Task InstallMelonLoaderAsync()
    {
        if (SelectedMelonLoaderVersion == null)
        {
            MessageDialogHelper.ShowError(Strings.PleaseSelectMelonLoaderVersion, Strings.Error);
            return;
        }

        try
        {
            IsInstalling = true;
            StatusMessage = Strings.InstallingMelonLoader;

            var progress = new Progress<DownloadProgress>(p =>
            {
                InstallProgress = p.ProgressPercentage;
                StatusMessage = $"{Strings.InstallingMelonLoader} - {p.ProgressPercentage:F1}% ({p.GetFormattedProgress()}) - {p.GetFormattedSpeed()}";
            });

            var success = await _melonLoaderService.InstallMelonLoaderAsync(
                GameRootPath, 
                SelectedMelonLoaderVersion.TagName, 
                progress);

            if (success)
            {
                StatusMessage = Strings.MelonLoaderInstallSuccessNeedGameRun;
                CurrentStep = 6;
            }
            else
            {
                MessageDialogHelper.ShowError(Strings.MelonLoaderInstallFailed, Strings.Error);
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.MelonLoaderInstallError);
            MessageDialogHelper.ShowError(Strings.MelonLoaderInstallationError, Strings.Error);
        }
        finally
        {
            IsInstalling = false;
        }
    }

    private async Task LaunchGameAsync()
    {
        try
        {
            StatusMessage = Strings.StartingGame;
            var success = await _processService.LaunchGameAsync(GameRootPath);
            
            if (success)
            {
                StatusMessage = Strings.GameStartedRunToMainMenu;
                CurrentStep = 7;
            }
            else
            {
                MessageDialogHelper.ShowError(Strings.GameLaunchFailed, Strings.Error);
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.GameLaunchError);
            MessageDialogHelper.ShowError(Strings.GameLaunchErrorOccurred, Strings.Error);
        }
    }

    private void OnGameRunningStateChanged(object? sender, bool isRunning)
    {
        IsGameRunning = isRunning;
        
        if (CurrentStep == 7 && !isRunning)
        {
            Application.Current.Dispatcher.BeginInvoke(async () =>
            {
                StatusMessage = Strings.GameExitedVerifyingInstall;
                await Task.Delay(10000); // Wait for file handles to release
                
                AreMelonLoaderDirsCreated = await _melonLoaderService.AreMelonLoaderDirectoriesCreatedAsync(GameRootPath);
                
                if (AreMelonLoaderDirsCreated)
                {
                    await CompleteSetupAsync();
                }
                else
                {
                    MessageDialogHelper.ShowWarning(Strings.SetupIncompleteWarning, Strings.Warning);
                    StatusMessage = Strings.PleaseRunGameAgain;
                }
            });
        }
    }

    private async Task CompleteSetupAsync()
    {
        _settingsService.Settings.IsFirstRun = false;
        // 初次安装无需清理旧版本文件，跳过清理
        _settingsService.Settings.CleanupDoneForVersion = _updateService.GetCurrentVersion().TrimStart('v');
        await _settingsService.SaveSettingsAsync();

        // 保存当前程序路径到注册表
        _previousInstallationService.SaveCurrentAppPath();

        StatusMessage = Strings.SetupComplete;
        
        await Task.Delay(2000);
        _navigationService.NavigateToMainView();
    }

    private void UpdateNavigationButtons()
    {
        IsBackEnabled = CurrentStep > 0;
        IsNextEnabled = CurrentStep switch
        {
            0 => true,
            1 => true,
            // 步骤2: 必须完成网络检测且GitHub连通性测试通过
            2 => !IsCheckingNetwork && IsNetworkAvailable && (!UseGitHubProxy || SelectedProxyServer != null),
            3 => !string.IsNullOrEmpty(GameRootPath),
            4 => true,
            5 => SelectedMelonLoaderVersion != null && !IsInstalling,
            6 => !IsGameRunning,
            7 => false,
            _ => true
        };
    }

    private async Task AutoSearchGHPCAsync()
    {
        try
        {
            StatusMessage = Strings.AutoSearchingGHPC;
            _loggingService.LogInfo(Strings.AutoSearchStarted);

            var foundPaths = await _steamGameFinder.FindGHPCGamePathsAsync();

            if (foundPaths.Count > 0)
            {
                // 如果找到多个路径，使用第一个（通常是最可能的）
                GameRootPath = foundPaths[0];
                _settingsService.Settings.GameRootPath = GameRootPath;
                await _settingsService.SaveSettingsAsync();
                UpdateNavigationButtons();

                _loggingService.LogInfo(string.Format(Strings.MultiplePathsFound, foundPaths.Count));
                _loggingService.LogInfo(string.Format(Strings.SteamInstallationPath, GameRootPath));
            }
            else
            {
                _loggingService.LogInfo(Strings.NoPathsFound);
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.AutoSearchError);
        }
        finally
        {
            StatusMessage = string.Empty;
        }
    }

    private async Task<bool> ValidateGameDirectoryWithSteamCheckAsync()
    {
        if (string.IsNullOrEmpty(GameRootPath))
        {
            MessageDialogHelper.ShowError(Strings.PleaseSelectGameDirectory, Strings.Error);
            return false;
        }

        var ghpcExe = Path.Combine(GameRootPath, "GHPC.exe");
        if (!File.Exists(ghpcExe))
        {
            MessageDialogHelper.ShowError(Strings.GHPCExeNotFoundInDirectory, Strings.Error);
            return false;
        }

        // 检查是否为Steam版本
        if (!await IsSteamVersionAsync(GameRootPath))
        {
            if (!_hasShownSteamWarning)
            {
                _hasShownSteamWarning = true;
                var result = MessageDialogHelper.ConfirmOK(
                    Strings.NonSteamVersionWarning,
                    Strings.VersionWarningTitle);

                if (!result)
                {
                    return false;
                }

                _loggingService.LogInfo(Strings.NonSteamVersionDetected);
            }
        }
        else
        {
            _loggingService.LogInfo(Strings.SteamVersionDetected);
        }

        return true;
    }

    private async Task<bool> IsSteamVersionAsync(string gamePath)
    {
        try
        {
            // 检查是否存在Steam相关的文件或目录结构
            var gameBinPath = Path.GetDirectoryName(gamePath);
            var gameRootPath = Path.GetDirectoryName(gameBinPath);

            if (string.IsNullOrEmpty(gameRootPath))
                return false;

            // 方法1: 检查是否存在Steam游戏manifest文件
            var steamAppsPath = Path.Combine(gameRootPath, "..", "..", "steamapps");
            if (Directory.Exists(steamAppsPath))
            {
                var manifestPath = Path.Combine(steamAppsPath, "appmanifest_665650.acf");
                if (File.Exists(manifestPath))
                    return true;
            }

            // 方法2: 检查父目录是否包含"steamapps\common"
            if (gameBinPath.Contains("steamapps") && gameBinPath.Contains("common"))
            {
                return true;
            }

            // 方法3: 检查是否为标准Steam安装路径结构
            var typicalSteamPath = Path.Combine(gameRootPath, "steamapps", "common", "Gunner, HEAT, PC!");
            if (Directory.Exists(typicalSteamPath) && typicalSteamPath.Contains("steamapps"))
            {
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.AutoSearchError);
            return false;
        }
    }
}
