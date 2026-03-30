using GHPC_Mod_Manager.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using System.Windows;

namespace GHPC_Mod_Manager.Views;

public partial class ModInfoDumperWindow : Window
{
    public ModInfoDumperWindow()
    {
        InitializeComponent();
        DataContext = App.GetService<ModInfoDumperViewModel>();
    }
}