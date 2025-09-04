using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GHPC_Mod_Manager.Services;
using GHPC_Mod_Manager.Models;
using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Diagnostics;
using System.Reflection;
using GHPC_Mod_Manager.Resources;

namespace GHPC_Mod_Manager.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settingsService;
    private readonly INavigationService _navigationService;
    private readonly ILoggingService _loggingService;
    private readonly INetworkService _networkService;
    private readonly IModI18nService _modI18nService;
    private readonly IThemeService _themeService;
    private readonly IModBackupService _modBackupService;

    [ObservableProperty]
    private string _selectedLanguage = "zh-CN";

    partial void OnSelectedLanguageChanged(string value)
    {
        // Force disable GitHub proxy when language is not Chinese
        if (value != "zh-CN")
        {
            UseGitHubProxy = false;
        }
        
        // Notify that proxy visibility has changed
        OnPropertyChanged(nameof(IsGitHubProxyVisible));
    }

    [ObservableProperty]
    private string _gameRootPath = string.Empty;

    [ObservableProperty]
    private string _modConfigUrl = string.Empty;

    [ObservableProperty]
    private string _translationConfigUrl = string.Empty;

    [ObservableProperty]
    private string _modI18nUrl = string.Empty;

    [ObservableProperty]
    private bool _useGitHubProxy = false;

    [ObservableProperty]
    private GitHubProxyServer _gitHubProxyServer = GitHubProxyServer.GhDmrGg;

    [ObservableProperty]
    private List<GitHubProxyServer> _availableProxyServers = new() 
    { 
        GitHubProxyServer.GhDmrGg, 
        GitHubProxyServer.GhProxyCom, 
        GitHubProxyServer.HkGhProxyCom, 
        GitHubProxyServer.CdnGhProxyCom, 
        GitHubProxyServer.EdgeOneGhProxyCom 
    };

    [ObservableProperty]
    private List<string> _availableLanguages = new() { "zh-CN", "en-US" };

    [ObservableProperty]
    private string _refreshStatus = string.Empty;

    [ObservableProperty]
    private AppTheme _selectedTheme = AppTheme.Light;

    [ObservableProperty]
    private List<AppTheme> _availableThemes = new() { AppTheme.Light, AppTheme.Dark };

    public string ApplicationVersion => GetApplicationVersion();

    public bool IsGitHubProxyVisible => SelectedLanguage == "zh-CN";

    public SettingsViewModel(
        ISettingsService settingsService,
        INavigationService navigationService,
        ILoggingService loggingService,
        INetworkService networkService,
        IModI18nService modI18nService,
        IThemeService themeService,
        IModBackupService modBackupService)
    {
        _settingsService = settingsService;
        _navigationService = navigationService;
        _loggingService = loggingService;
        _networkService = networkService;
        _modI18nService = modI18nService;
        _themeService = themeService;
        _modBackupService = modBackupService;

        LoadSettings();
    }

    private void LoadSettings()
    {
        var settings = _settingsService.Settings;
        SelectedLanguage = settings.Language;
        GameRootPath = settings.GameRootPath;
        ModConfigUrl = settings.ModConfigUrl;
        TranslationConfigUrl = settings.TranslationConfigUrl;
        ModI18nUrl = settings.ModI18nUrl;
        UseGitHubProxy = settings.UseGitHubProxy;
        GitHubProxyServer = settings.GitHubProxyServer;
        SelectedTheme = settings.Theme; // 加载主题设置
    }

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        try
        {
            var settings = _settingsService.Settings;
            settings.Language = SelectedLanguage;
            settings.GameRootPath = GameRootPath;
            settings.ModConfigUrl = ModConfigUrl;
            settings.TranslationConfigUrl = TranslationConfigUrl;
            settings.ModI18nUrl = ModI18nUrl;
            settings.UseGitHubProxy = UseGitHubProxy;
            settings.GitHubProxyServer = GitHubProxyServer;
            settings.Theme = SelectedTheme; // 保存主题设置

            await _settingsService.SaveSettingsAsync();
            
            // 应用新主题
            _themeService.SetTheme(SelectedTheme);
            
            MessageBox.Show(Strings.SettingsSavedSuccessfully, Strings.Success, MessageBoxButton.OK, MessageBoxImage.Information);
            _loggingService.LogInfo(Strings.SettingsSaved);
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.SettingsSaveError);
            MessageBox.Show(Strings.SettingsSaveFailed, Strings.Error, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private async Task ForceRefreshConfigurationsAsync()
    {
        try
        {
            RefreshStatus = GHPC_Mod_Manager.Resources.Strings.Refreshing;
            
            // Clear all network caches
            _networkService.ClearCache();
            
            // Refresh ModI18n configuration
            await _modI18nService.RefreshModI18nConfigAsync();
            
            RefreshStatus = GHPC_Mod_Manager.Resources.Strings.ConfigurationsRefreshedSuccessfully;
            _loggingService.LogInfo(Strings.ConfigurationRefreshComplete);
            
            // Clear the status message after 3 seconds
            await Task.Delay(3000);
            RefreshStatus = string.Empty;
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.ConfigurationRefreshError);
            RefreshStatus = GHPC_Mod_Manager.Resources.Strings.RefreshFailedCheckLogs;
            
            await Task.Delay(3000);
            RefreshStatus = string.Empty;
        }
    }

    [RelayCommand]
    private void BrowseGameDirectory()
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
            }
            else
            {
                MessageBox.Show(Strings.PleaseSelectGHPCExe, Strings.Error, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    [RelayCommand]
    private async Task CleanTempFilesAsync()
    {
        try
        {
            var tempPath = _settingsService.TempPath;
            if (Directory.Exists(tempPath))
            {
                var files = Directory.GetFiles(tempPath);
                var directories = Directory.GetDirectories(tempPath);

                foreach (var file in files)
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch
                    {
                        // Ignore files that can't be deleted
                    }
                }

                foreach (var directory in directories)
                {
                    try
                    {
                        Directory.Delete(directory, true);
                    }
                    catch
                    {
                        // Ignore directories that can't be deleted
                    }
                }

                MessageBox.Show(Strings.TempFilesCleaned_, Strings.Success, MessageBoxButton.OK, MessageBoxImage.Information);
                _loggingService.LogInfo(Strings.TempFilesCleaned);
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.TempFilesCleanError);
            MessageBox.Show(Strings.TempFilesCleanFailed, Strings.Error, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private async Task CleanupModBackupsAsync()
    {
        try
        {
            var result = MessageBox.Show(
                Strings.ConfirmCleanupModBackups,
                Strings.ConfirmCleanup,
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                var freedBytes = await _modBackupService.CleanupModBackupsAsync();
                var freedMB = (freedBytes / (1024.0 * 1024.0)).ToString("F1");
                
                MessageBox.Show(
                    string.Format(Strings.CleanupModBackupsSuccessful, freedMB),
                    Strings.Success,
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.CleanupModBackupsFailed, ex.Message);
            MessageBox.Show(
                string.Format(Strings.CleanupModBackupsFailed, ex.Message),
                Strings.Error,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void ShowLogWindow()
    {
        var logWindow = new Views.LogWindow();
        logWindow.Show();
    }

    [RelayCommand]
    private void BackToMain()
    {
        _navigationService.NavigateToMainView();
    }

    [RelayCommand]
    private void ChangeTheme()
    {
        try
        {
            // 应用新主题
            _themeService.SetTheme(SelectedTheme);
            _loggingService.LogInfo(Strings.ThemeSwitched, SelectedTheme);
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.ThemeApplicationError);
        }
    }

    [RelayCommand]
    private void OpenProjectUrl()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://github.com/RoyZ-iwnl/GHPC-Mod-Manager/",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.OpenProjectUrlError);
        }
    }

    private static string GetApplicationVersion()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            return informationalVersion ?? assembly.GetName().Version?.ToString() ?? "Unknown";
        }
        catch
        {
            return "Unknown";
        }
    }
}