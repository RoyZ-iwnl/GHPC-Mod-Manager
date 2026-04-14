using System.Windows.Controls;

namespace GHPC_Mod_Manager.Services;

public interface INavigationService
{
    event EventHandler<string> NavigationRequested;
    event EventHandler<string> PageNavigationRequested;
    void NavigateTo(string viewName);
    void NavigateToSetupWizard();
    void NavigateToMainView();
    void NavigateToSettings();
    void NavigateToPage(string pageName);
    void NavigateToModBrowser();
    void NavigateToInstalledMods();
    void NavigateToTranslation();
}

public class NavigationService : INavigationService
{
    public event EventHandler<string>? NavigationRequested;
    public event EventHandler<string>? PageNavigationRequested;

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

    /// <summary>
    /// 导航到MainView内部的页面
    /// </summary>
    public void NavigateToPage(string pageName)
    {
        PageNavigationRequested?.Invoke(this, pageName);
    }

    public void NavigateToModBrowser()
    {
        NavigateToPage("ModBrowser");
    }

    public void NavigateToInstalledMods()
    {
        NavigateToPage("InstalledMods");
    }

    public void NavigateToTranslation()
    {
        NavigateToPage("Translation");
    }
}