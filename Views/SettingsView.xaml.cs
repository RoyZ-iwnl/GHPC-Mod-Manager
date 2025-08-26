using GHPC_Mod_Manager.ViewModels;
using System.Windows.Controls;

namespace GHPC_Mod_Manager.Views;

public partial class SettingsView : UserControl
{
    public SettingsView(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}