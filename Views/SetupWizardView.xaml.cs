using GHPC_Mod_Manager.ViewModels;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using GHPC_Mod_Manager.Resources;

namespace GHPC_Mod_Manager.Views;

public partial class SetupWizardView : UserControl
{
    public SetupWizardView(SetupWizardViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
    
    private void CopyLogButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is Button button && button.Tag is string logContent)
            {
                if (!string.IsNullOrEmpty(logContent))
                {
                    Clipboard.SetText(logContent);
                    MessageBox.Show(Strings.LogContentCopiedToClipboard, Strings.CopySuccess, MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show(Strings.NoLogContentToCopy, Strings.Tip, MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }
        catch (System.Exception ex)
        {
            MessageBox.Show(string.Format(Strings.CopyFailed, ex.Message), Strings.Error, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}