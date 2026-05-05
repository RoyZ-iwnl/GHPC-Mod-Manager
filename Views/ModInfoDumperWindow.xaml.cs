using GHPC_Mod_Manager.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using System.Windows;
using System.Windows.Interop;

namespace GHPC_Mod_Manager.Views;

public partial class ModInfoDumperWindow : Window
{
    public ModInfoDumperWindow()
    {
        InitializeComponent();
        DataContext = App.GetService<ModInfoDumperViewModel>();
        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object sender, EventArgs e)
    {
        var handle = (new WindowInteropHelper(this)).Handle;
        var handleSource = HwndSource.FromHwnd(handle);
        handleSource?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_GETMINMAXINFO = 0x0024;

        if (msg == WM_GETMINMAXINFO)
        {
            var workArea = SystemParameters.WorkArea;
            var mmi = (MINMAXINFO)System.Runtime.InteropServices.Marshal.PtrToStructure(lParam, typeof(MINMAXINFO))!;

            mmi.ptMaxPosition.x = (int)workArea.Left;
            mmi.ptMaxPosition.y = (int)workArea.Top;
            mmi.ptMaxSize.x = (int)workArea.Width;
            mmi.ptMaxSize.y = (int)workArea.Height;

            System.Runtime.InteropServices.Marshal.StructureToPtr(mmi, lParam, true);
            handled = true;
        }

        return IntPtr.Zero;
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