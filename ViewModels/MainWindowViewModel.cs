using CommunityToolkit.Mvvm.ComponentModel;
using GHPC_Mod_Manager.Services;
using System.Windows.Controls;
using System.Reflection;

namespace GHPC_Mod_Manager.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly INavigationService _navigationService;
    private readonly ISettingsService _settingsService;

    [ObservableProperty]
    private UserControl? _currentView;

    [ObservableProperty]
    private string _title = GetWindowTitle();

    public MainWindowViewModel(INavigationService navigationService, ISettingsService settingsService)
    {
        _navigationService = navigationService;
        _settingsService = settingsService;
        _navigationService.NavigationRequested += OnNavigationRequested;
        
        InitializeAsync();
    }

    private void InitializeAsync()
    {
        if (_settingsService.Settings.IsFirstRun)
        {
            _navigationService.NavigateToSetupWizard();
        }
        else
        {
            _navigationService.NavigateToMainView();
        }
    }

    private void OnNavigationRequested(object? sender, string viewName)
    {
        CurrentView = viewName switch
        {
            "SetupWizard" => App.GetService<Views.SetupWizardView>(),
            "MainView" => App.GetService<Views.MainView>(),
            "Settings" => App.GetService<Views.SettingsView>(),
            _ => CurrentView
        };
    }

    private static string GetWindowTitle()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var versionAttribute = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            var versionString = versionAttribute?.InformationalVersion ?? assembly.GetName().Version?.ToString() ?? "Unknown";
            
            // Remove the git hash if present (anything after '+')
            var cleanVersion = versionString.Split('+')[0];
            
            return $"{GHPC_Mod_Manager.Resources.Strings.GHPCModManager} v{cleanVersion}";
        }
        catch
        {
            return GHPC_Mod_Manager.Resources.Strings.GHPCModManager;
        }
    }
}