using GHPC_Mod_Manager.ViewModels;
using System.Reflection;
using System.Windows;
using System.Windows.Interop;

namespace GHPC_Mod_Manager
{
    public partial class MainWindow : Window
    {
        public MainWindow(MainWindowViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;

            // 设置窗口标题，包含版本号
            var informationalVersion = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            Title = informationalVersion != null
                ? $"GHPC Mod Manager v{informationalVersion}"
                : "GHPC Mod Manager";

            // Smart window sizing based on screen working area
            SetSmartWindowSize();

            // Handle window state changes for proper maximization with custom title bar
            this.SourceInitialized += MainWindow_SourceInitialized;

            // Ensure application shuts down when main window closes
            this.Closing += (s, e) =>
            {
                // Cancel any background tasks
                Application.Current.Shutdown();
            };
        }

        private void MainWindow_SourceInitialized(object sender, System.EventArgs e)
        {
            // 获取窗口句柄并挂钩WM_GETMINMAXINFO消息，以正确处理最大化
            var handle = (new WindowInteropHelper(this)).Handle;
            var handleSource = HwndSource.FromHwnd(handle);
            handleSource?.AddHook(WndProc);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            // WM_GETMINMAXINFO = 0x0024
            const int WM_GETMINMAXINFO = 0x0024;

            if (msg == WM_GETMINMAXINFO)
            {
                // 获取工作区域（排除任务栏）
                var workArea = SystemParameters.WorkArea;

                // 获取MINMAXINFO结构
                var mmi = (MINMAXINFO)System.Runtime.InteropServices.Marshal.PtrToStructure(lParam, typeof(MINMAXINFO))!;

                // 设置最大化时的位置和大小
                mmi.ptMaxPosition.x = (int)workArea.Left;
                mmi.ptMaxPosition.y = (int)workArea.Top;
                mmi.ptMaxSize.x = (int)workArea.Width;
                mmi.ptMaxSize.y = (int)workArea.Height;

                // 写回结构
                System.Runtime.InteropServices.Marshal.StructureToPtr(mmi, lParam, true);
                handled = true;
            }

            return IntPtr.Zero;
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

            // Use smaller of desired or maximum allowed for initial size
            Width = Math.Min(desiredWidth, maxAllowedWidth);
            Height = Math.Min(desiredHeight, maxAllowedHeight);
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct POINT
        {
            public int x;
            public int y;
        }
    }
}