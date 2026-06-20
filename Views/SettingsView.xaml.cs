using GHPC_Mod_Manager.ViewModels;
using GHPC_Mod_Manager.Models;
using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Navigation;

namespace GHPC_Mod_Manager.Views;

public partial class SettingsView : UserControl
{
    public SettingsView(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private void VersionText_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
        {
            vm.HandleVersionClick();
        }
    }

    private void ProxyServerItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is System.Windows.FrameworkElement element &&
            element.DataContext is ProxyServerSpeedTestResult result &&
            DataContext is SettingsViewModel vm)
        {
            vm.SelectedSpeedTestResult = result;
        }
    }
}