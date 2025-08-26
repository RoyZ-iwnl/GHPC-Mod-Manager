using System.Windows.Controls;

namespace GHPC_Mod_Manager.Services;

public interface INavigationService
{
    event EventHandler<string> NavigationRequested;
    void NavigateTo(string viewName);
    void NavigateToSetupWizard();
    void NavigateToMainView();
    void NavigateToSettings();
}

public class NavigationService : INavigationService
{
    public event EventHandler<string>? NavigationRequested;

    public void NavigateTo(string viewName)
    {
        NavigationRequested?.Invoke(this, viewName);
    }

    public void NavigateToSetupWizard()
    {
        NavigateTo("SetupWizard");
    }

    public void NavigateToMainView()
    {
        NavigateTo("MainView");
    }

    public void NavigateToSettings()
    {
        NavigateTo("Settings");
    }
}