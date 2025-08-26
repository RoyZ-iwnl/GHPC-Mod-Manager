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

    [ObservableProperty]
    private int _currentStep = 1;

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

    public SetupWizardViewModel(
        ISettingsService settingsService,
        INavigationService navigationService,
        INetworkService networkService,
        IMelonLoaderService melonLoaderService,
        IProcessService processService,
        ILoggingService loggingService)
    {
        _settingsService = settingsService;
        _navigationService = navigationService;
        _networkService = networkService;
        _melonLoaderService = melonLoaderService;
        _processService = processService;
        _loggingService = loggingService;

        _processService.GameRunningStateChanged += OnGameRunningStateChanged;
        
        InitializeAsync();
    }

    private async void InitializeAsync()
    {
        SelectedLanguage = _settingsService.Settings.Language;
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
            case 1: // Language Selection
                // 语言已经通过Apply按钮设置，这里只需要决定下一步
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
                if (await ValidateGameDirectoryAsync())
                {
                    CurrentStep = 4;
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
        if (CurrentStep > 1)
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
                FileName = "https://www.dogfight360.com/blog/18682/",
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
                _loggingService.LogInfo(Strings.NetworkCheckResult, "Connection normal");
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
                _loggingService.LogInfo(Strings.NetworkCheckResult, "Connection failed");
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
        IsBackEnabled = CurrentStep > 1;
        IsNextEnabled = CurrentStep switch
        {
            3 => !string.IsNullOrEmpty(GameRootPath),
            4 => true, // 总是允许进入下一步：已安装则继续，未安装则进入安装
            5 => SelectedMelonLoaderVersion != null && !IsInstalling,
            6 => !IsGameRunning,
            7 => false,
            _ => true
        };
    }
}