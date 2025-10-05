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

            // Smart window sizing based on screen working area
            SetSmartWindowSize();

            // Ensure application shuts down when main window closes
            this.Closing += (s, e) =>
            {
                // Cancel any background tasks
                Application.Current.Shutdown();
            };
        }

        private void SetSmartWindowSize()
        {
            // Get screen working area (excludes taskbar)
            var workArea = SystemParameters.WorkArea;

            // Set window size to 90% of working area, capped at designed size
            var desiredWidth = 1600;
            var desiredHeight = 900;

            // Calculate maximum allowed size (90% of work area)
            var maxAllowedWidth = workArea.Width * 0.9;
            var maxAllowedHeight = workArea.Height * 0.9;

            // Use smaller of desired or maximum allowed
            Width = Math.Min(desiredWidth, maxAllowedWidth);
            Height = Math.Min(desiredHeight, maxAllowedHeight);

            // Set MaxWidth and MaxHeight to prevent window from exceeding screen
            MaxWidth = maxAllowedWidth;
            MaxHeight = maxAllowedHeight;
        }
    }
}