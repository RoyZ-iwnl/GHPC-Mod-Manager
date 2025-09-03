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

    private async void InitializeAsync()
    {
        if (_settingsService.Settings.IsFirstRun)
        {
            _navigationService.NavigateToSetupWizard();
        }
        else
        {
            _navigationService.NavigateToMainView();
            
            // Show startup features after main view is loaded
            _ = Task.Run(async () =>
            {
                await Task.Delay(1000); // Give UI time to load
                await ShowStartupFeaturesAsync();
            });
        }
    }

    private async Task ShowStartupFeaturesAsync()
    {
        try
        {
            var currentLanguage = CultureInfo.CurrentUICulture.Name;
            
            // Show announcement
            await ShowAnnouncementAsync(currentLanguage);
        }
        catch (Exception ex)
        {
            // Don't let startup features crash the app
            System.Diagnostics.Debug.WriteLine($"Error in startup features: {ex.Message}");
        }
    }

    private async Task ShowAnnouncementAsync(string language)
    {
        try
        {
            await Application.Current.Dispatcher.InvokeAsync(async () =>
            {
                var announcementViewModel = _serviceProvider.GetRequiredService<AnnouncementViewModel>();
                
                // Load announcement content first
                await announcementViewModel.LoadAnnouncementAsync(language);
                
                // Debug: Log status
                System.Diagnostics.Debug.WriteLine($"Announcement HasContent: {announcementViewModel.HasContent}");
                System.Diagnostics.Debug.WriteLine($"Announcement IsLoading: {announcementViewModel.IsLoading}");
                System.Diagnostics.Debug.WriteLine($"Announcement ErrorMessage: {announcementViewModel.ErrorMessage}");
                
                // Only show window if there's content
                if (announcementViewModel.HasContent)
                {
                    var announcementWindow = new AnnouncementWindow
                    {
                        DataContext = announcementViewModel,
                        WindowStartupLocation = WindowStartupLocation.CenterScreen
                    };
                    
                    // Set owner if main window is available
                    if (Application.Current.MainWindow != null)
                    {
                        announcementWindow.Owner = Application.Current.MainWindow;
                        announcementWindow.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                    }
                    
                    announcementWindow.ShowDialog();
                }
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error showing announcement: {ex.Message}");
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