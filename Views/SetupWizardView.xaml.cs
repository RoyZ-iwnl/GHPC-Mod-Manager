using GHPC_Mod_Manager.ViewModels;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using GHPC_Mod_Manager.Resources;
using GHPC_Mod_Manager.Helpers;

namespace GHPC_Mod_Manager.Views;

public partial class SetupWizardView : UserControl
{
    public SetupWizardView(SetupWizardViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
    
    private async void CopyLogButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is Button button && button.Tag is string logContent)
            {
                if (!string.IsNullOrEmpty(logContent))
                {
                    Clipboard.SetText(logContent);
                    await MessageDialogHelper.ShowSuccessAsync(Strings.LogContentCopiedToClipboard, Strings.CopySuccess);
                }
                else
                {
                    await MessageDialogHelper.ShowWarningAsync(Strings.NoLogContentToCopy, Strings.Tip);
                }
            }
        }
        catch (System.Exception ex)
        {
            await MessageDialogHelper.ShowErrorAsync(string.Format(Strings.CopyFailed, ex.Message), Strings.Error);
        }
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}