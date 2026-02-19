using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GHPC_Mod_Manager.Models;
using GHPC_Mod_Manager.Services;
using Microsoft.Win32;
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
    private bool _isInitializing = true;
    private bool _hasShownBepInExWarning;
    private bool _hasShownSteamWarning;
    private bool _autoSearchAttempted;

    [ObservableProperty]
    private int _currentStep = 0;

    partial void OnCurrentStepChanged(int value)
    {
        // 当步骤切换到游戏目录选择(步骤3)时，自动触发搜索
        if (value == 3 && !_autoSearchAttempted)
        {
            _autoSearchAttempted = true;
            _ = Task.Run(async () => await AutoSearchGHPCAsync());
        }
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

    partial void OnUseGitHubProxyChanged(bool value)
    {
        // Skip auto-save during initial loading to prevent overwriting settings
        if (_isInitializing)
            return;
            
        // 启用代理时清空token
        if (value)
            GitHubApiToken = string.Empty;

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
    private GitHubProxyServer _gitHubProxyServer = GitHubProxyServer.EdgeOneGhProxyCom;

    partial void OnGitHubProxyServerChanged(GitHubProxyServer value)
    {
        // Skip auto-save during initial loading to prevent overwriting settings
        if (_isInitializing)
            return;

        // Automatically save proxy server settings when changed
        _ = Task.Run(async () =>
        {
            try
            {
                _settingsService.Settings.GitHubProxyServer = value;
                await _settingsService.SaveSettingsAsync();

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    var message = string.Format(Strings.ProxyServerChanged, value);
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
    private List<GitHubProxyServer> _availableProxyServers = new()
    {
        GitHubProxyServer.EdgeOneGhProxyCom,
        GitHubProxyServer.GhDmrGg,
        GitHubProxyServer.GhProxyCom,
        GitHubProxyServer.HkGhProxyCom,
        GitHubProxyServer.CdnGhProxyCom
    };

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
        ISteamGameFinderService steamGameFinder)
    {
        _settingsService = settingsService;
        _navigationService = navigationService;
        _networkService = networkService;
        _melonLoaderService = melonLoaderService;
        _processService = processService;
        _loggingService = loggingService;
        _steamGameFinder = steamGameFinder;

        _processService.GameRunningStateChanged += OnGameRunningStateChanged;
        
        InitializeAsync();
    }

    private async void InitializeAsync()
    {
        SelectedLanguage = _settingsService.Settings.Language;
        
        // Load existing proxy settings
        UseGitHubProxy = _settingsService.Settings.UseGitHubProxy;
        GitHubProxyServer = _settingsService.Settings.GitHubProxyServer;
        GitHubApiToken = _settingsService.Settings.GitHubApiToken;
        
        // Mark initialization as complete after setting all values
        _isInitializing = false;
        
        ClearNetworkLog(); // 清空网络日志
        await CheckNetworkAsync();
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
            var result = MessageBox.Show(
                Strings.LanguageChangedConfirmRestart, 
                Strings.LanguageChanged, 
                MessageBoxButton.YesNo, 
                MessageBoxImage.Question);
                
            if (result == MessageBoxResult.Yes)
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
                // 语言已经通过Apply按钮设置，这里只需要决定下一步
                CurrentStep = 1; // Go to Welcome Page
                break;

            case 1: // Welcome Page  
                if (SelectedLanguage == "zh-CN")
                {
                    _loggingService.LogInfo(Strings.EnteringNetworkCheck);
                    CurrentStep = 2; // Network Check
                }
                else
                {
                    _loggingService.LogInfo(Strings.SkippingNetworkCheck);
                    CurrentStep = 3; // Game Directory
                }
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
            // Handle special case: if we're on step 3 and we skipped step 2 (network check)
            // for English language, go back to step 1 instead of step 2
            if (CurrentStep == 3 && SelectedLanguage != "zh-CN")
            {
                CurrentStep = 1;
            }
            else
            {
                CurrentStep--;
            }
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
                MessageBox.Show(Strings.PleaseSelectGHPCExe, Strings.Error, MessageBoxButton.OK, MessageBoxImage.Error);
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
            
            // Settings are automatically saved by property change handlers
            // Just retry network check with new settings
            await CheckNetworkAsync();
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
        await CheckNetworkAsync();
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

    private async Task CheckNetworkAsync()
    {
        IsCheckingNetwork = true;
        ShowNetworkFailed = false;
        AddToNetworkLog(Strings.StartingNetworkConnectionCheck);
        
        try
        {
            _loggingService.LogInfo(Strings.StartingNetworkConnectionCheck);
            AddToNetworkLog(Strings.CheckingNetworkConnectionStatus);
            
            IsNetworkAvailable = await _networkService.CheckNetworkConnectionAsync();
            
            if (IsNetworkAvailable)
            {
                AddToNetworkLog(Strings.NetworkCheckSuccessful);
                _loggingService.LogInfo(Strings.NetworkCheckResult, Strings.ConnectionNormal);
                ShowNetworkFailed = false;
            }
            else
            {
                AddToNetworkLog(Strings.NetworkCheckFailed);
                AddToNetworkLog(Strings.PossibleReasons);
                AddToNetworkLog(Strings.UnstableNetworkConnection);
                AddToNetworkLog(Strings.DNSResolutionProblem);
                AddToNetworkLog(Strings.FirewallBlocking);
                AddToNetworkLog(Strings.ProxyServerConfigProblem);
                _loggingService.LogInfo(Strings.NetworkCheckResult, Strings.ConnectionFailed);
                ShowNetworkFailed = true;
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.NetworkCheckException);
            IsNetworkAvailable = false;
            ShowNetworkFailed = true;
            AddToNetworkLog(string.Format(Strings.NetworkCheckException, ex.Message));
            AddToNetworkLog(Strings.SuggestRetryOrCheckSettings);
        }
        finally
        {
            IsCheckingNetwork = false;
            AddToNetworkLog(string.Format(Strings.NetworkCheckComplete, IsNetworkAvailable ? Strings.ConnectionNormalResult : Strings.ConnectionAbnormalResult));
        }
    }

    private async Task<bool> ValidateGameDirectoryAsync()
    {
        if (string.IsNullOrEmpty(GameRootPath))
        {
            MessageBox.Show(Strings.PleaseSelectGameDirectory, Strings.Error, MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }

        var ghpcExe = Path.Combine(GameRootPath, "GHPC.exe");
        if (!File.Exists(ghpcExe))
        {
            MessageBox.Show(Strings.GHPCExeNotFoundInDirectory, Strings.Error, MessageBoxButton.OK, MessageBoxImage.Error);
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
                MessageBox.Show(Strings.BepInExDetectedMessage, Strings.Warning, MessageBoxButton.OK, MessageBoxImage.Warning);
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
        try
        {
            MelonLoaderReleases = await _melonLoaderService.GetMelonLoaderReleasesAsync();
            SelectedMelonLoaderVersion = MelonLoaderReleases.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.MelonLoaderReleasesLoadError);
            MessageBox.Show(Strings.CannotGetMelonLoaderVersions, Strings.Error, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task InstallMelonLoaderAsync()
    {
        if (SelectedMelonLoaderVersion == null)
        {
            MessageBox.Show(Strings.PleaseSelectMelonLoaderVersion, Strings.Error, MessageBoxButton.OK, MessageBoxImage.Error);
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
                MessageBox.Show(Strings.MelonLoaderInstallFailed, Strings.Error, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.MelonLoaderInstallError);
            MessageBox.Show(Strings.MelonLoaderInstallationError, Strings.Error, MessageBoxButton.OK, MessageBoxImage.Error);
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
                MessageBox.Show(Strings.GameLaunchFailed, Strings.Error, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.GameLaunchError);
            MessageBox.Show(Strings.GameLaunchErrorOccurred, Strings.Error, MessageBoxButton.OK, MessageBoxImage.Error);
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
                    MessageBox.Show(Strings.SetupIncompleteWarning, Strings.Warning, MessageBoxButton.OK, MessageBoxImage.Warning);
                    StatusMessage = Strings.PleaseRunGameAgain;
                }
            });
        }
    }

    private async Task CompleteSetupAsync()
    {
        _settingsService.Settings.IsFirstRun = false;
        await _settingsService.SaveSettingsAsync();
        
        StatusMessage = Strings.SetupComplete;
        
        await Task.Delay(2000);
        _navigationService.NavigateToMainView();
    }

    private void UpdateNavigationButtons()
    {
        IsBackEnabled = CurrentStep > 0;
        IsNextEnabled = CurrentStep switch
        {
            0 => true, // Language Selection - always allow next
            1 => true, // Welcome page - always allow next
            3 => !string.IsNullOrEmpty(GameRootPath),
            4 => true, // 总是允许进入下一步：已安装则继续，未安装则进入安装
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
            MessageBox.Show(Strings.PleaseSelectGameDirectory, Strings.Error, MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }

        var ghpcExe = Path.Combine(GameRootPath, "GHPC.exe");
        if (!File.Exists(ghpcExe))
        {
            MessageBox.Show(Strings.GHPCExeNotFoundInDirectory, Strings.Error, MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }

        // 检查是否为Steam版本
        if (!await IsSteamVersionAsync(GameRootPath))
        {
            if (!_hasShownSteamWarning)
            {
                _hasShownSteamWarning = true;
                var result = MessageBox.Show(
                    Strings.NonSteamVersionWarning,
                    Strings.VersionWarningTitle,
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Warning);

                if (result == MessageBoxResult.Cancel)
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