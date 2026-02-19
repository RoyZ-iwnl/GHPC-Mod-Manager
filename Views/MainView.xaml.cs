using GHPC_Mod_Manager.ViewModels;
using System.Windows.Controls;

namespace GHPC_Mod_Manager.Views;

public partial class MainView : UserControl
{
    public MainView(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) viewModel.RefreshMelonLoaderState();
        };
    }
}
