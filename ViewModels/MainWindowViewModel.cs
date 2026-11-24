using CommunityToolkit.Mvvm.ComponentModel;
using GHPC_Mod_Manager.Services;
using GHPC_Mod_Manager.Views;
using System.Windows.Controls;
using System.Reflection;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using System.Windows;

namespace GHPC_Mod_Manager.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly INavigationService _navigationService;
    private readonly ISettingsService _settingsService;
    private readonly IAnnouncementService _announcementService;
    private readonly IServiceProvider _serviceProvider;

    [ObservableProperty]
    private UserControl? _currentView;

    [ObservableProperty]
    private string _title = GetWindowTitle();

    public MainWindowViewModel(
        INavigationService navigationService, 
        ISettingsService settingsService,
        IAnnouncementService announcementService,
        IServiceProvider serviceProvider)
    {
        _navigationService = navigationService;
        _settingsService = settingsService;
        _announcementService = announcementService;
        _serviceProvider = serviceProvider;
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
            // 公告只在有新内容时由 MainViewModel 弹出，不再每次启动都弹（之前新公告会弹两次）
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

            var parts = versionString.Split('+');
            var version = parts[0];
            var hash = parts.Length > 1 ? parts[1][..Math.Min(7, parts[1].Length)] : null;

            return hash != null
                ? $"{GHPC_Mod_Manager.Resources.Strings.GHPCModManager} v{version} ({hash})"
                : $"{GHPC_Mod_Manager.Resources.Strings.GHPCModManager} v{version}";
        }
        catch
        {
            return GHPC_Mod_Manager.Resources.Strings.GHPCModManager;
        }
    }
}