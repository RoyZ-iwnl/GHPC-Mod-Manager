using System.Windows.Controls;
using GHPC_Mod_Manager.Models;
using GHPC_Mod_Manager.ViewModels;

namespace GHPC_Mod_Manager.Views;

public partial class SaveEditorView : UserControl
{
    public SaveEditorView()
    {
        InitializeComponent();
    }

    private void TreeView_SelectedItemChanged(object sender, System.Windows.RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is SaveEditorViewModel viewModel)
        {
            viewModel.SelectedTreeNode = e.NewValue;
        }
    }
}