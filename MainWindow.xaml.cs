using GHPC_Mod_Manager.ViewModels;
using System.Windows;

namespace GHPC_Mod_Manager
{
    public partial class MainWindow : Window
    {
        public MainWindow(MainWindowViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }
    }
}