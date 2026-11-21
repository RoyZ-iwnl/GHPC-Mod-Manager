using GHPC_Mod_Manager.ViewModels;
using System.Windows;
using System.Windows.Input;

namespace GHPC_Mod_Manager.Views;

public partial class ModUpdateSelectDialog : Window
{
    public bool Confirmed { get; private set; }

    public ModUpdateSelectDialog(ModUpdateSelectViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        SourceInitialized += (_, _) =>
        {
            // 居中到 Owner
            if (Owner != null)
            {
                Left = Owner.Left + (Owner.Width - Width) / 2;
                Top = Owner.Top + (Owner.Height - Height) / 2;
            }
        };
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => DragMove();

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = false;
        Close();
    }
}
