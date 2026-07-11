using GHPC_Mod_Manager.ViewModels;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace GHPC_Mod_Manager.Views;

public partial class FundingWindow : Window
{
    private Storyboard? _scrollStoryboard;
    private List<string> _sponsors = new();
    private DispatcherTimer? _resizeTimer;
    private TranslateTransform? _creditsTransform;

    public FundingWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
        DataContextChanged += OnDataContextChanged;
        SourceInitialized += OnSourceInitialized;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        // 关闭时清理：停止动画时钟、定时器、VM订阅，避免泄漏窗口可视树
        StopScroll();
        _resizeTimer?.Stop();
        if (DataContext is FundingWindowViewModel vm)
            vm.PropertyChanged -= OnViewModelPropertyChanged;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        // 挂钩 WM_GETMINMAXINFO，最大化时不遮挡任务栏
        var handle = new WindowInteropHelper(this).Handle;
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

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 窗口显示后启动滚动（此时布局已完成）
        TryStartScroll();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is FundingWindowViewModel oldVm)
            oldVm.PropertyChanged -= OnViewModelPropertyChanged;
        if (e.NewValue is FundingWindowViewModel newVm)
            newVm.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 名单加载完成（HasContent 翻转）时启动滚动（窗口可能已显示后异步加载）
        if (e.PropertyName == nameof(FundingWindowViewModel.HasContent))
            Dispatcher.BeginInvoke(new Action(TryStartScroll), DispatcherPriority.Background);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (!sizeInfo.HeightChanged) return;

        // 防抖：拖动resize逐像素触发，等停止后再重算，避免字幕闪回顶部
        _resizeTimer ??= new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };
        _resizeTimer.Stop();
        _resizeTimer.Tick -= OnResizeSettled;
        _resizeTimer.Tick += OnResizeSettled;
        _resizeTimer.Start();
    }

    private void OnResizeSettled(object? sender, EventArgs e)
    {
        _resizeTimer?.Stop();
        TryStartScroll();
    }

    /// <summary>
    /// 启动无缝向上循环滚动动画。
    /// 原理：Canvas不约束子元素高度，ItemsControl.ActualHeight=内容真实高度。
    /// 复制N份填入(总高≥2×视口)，动画平移一份高度后重置，因N份相同故视觉无缝循环。
    /// 用完整属性路径 (RenderTransform).(TranslateTransform.Y) 动画，参考 Endfield 主题动画范式。
    /// </summary>
    private void TryStartScroll()
    {
        StopScroll();

        if (DataContext is not FundingWindowViewModel vm) return;
        if (!vm.HasContent || vm.Sponsors == null || vm.Sponsors.Count == 0) return;

        var viewport = ScrollClipBorder.ActualHeight;
        if (viewport <= 0)
        {
            // 视口尚未布局，延后重试
            Dispatcher.BeginInvoke(new Action(TryStartScroll), DispatcherPriority.Background);
            return;
        }

        _sponsors = vm.Sponsors;

        // 确保 ItemsControl 有 TranslateTransform（参考 Endfield 动画范式）
        if (CreditsItems.RenderTransform is not TranslateTransform)
            CreditsItems.RenderTransform = new TranslateTransform();
        _creditsTransform = (TranslateTransform)CreditsItems.RenderTransform;

        // Canvas 不拉伸子元素，需手动设宽度让 ItemsControl 占满，内部 TextBlock HorizontalAlignment=Center 才能生效
        CreditsItems.Width = ScrollHost.ActualWidth;

        // 阶段1：设2份测量单份高度（Canvas给无限高度，ActualHeight=真实内容高度）
        CreditsItems.ItemsSource = BuildCopies(_sponsors, 2);
        CreditsItems.UpdateLayout();
        var measuredTotal = CreditsItems.ActualHeight;
        System.Diagnostics.Debug.WriteLine($"[FundingScroll] viewport={viewport}, measuredTotal(2copies)={measuredTotal}");
        if (measuredTotal <= 0)
        {
            // 容器可能尚未生成，延后重试
            Dispatcher.BeginInvoke(new Action(TryStartScroll), DispatcherPriority.Background);
            return;
        }
        var singleSetHeight = measuredTotal / 2.0;
        if (singleSetHeight <= 0) return;

        // 阶段2：复制足够份数，总高 ≥ 2×视口，保证循环时视口始终填满
        var copies = (int)Math.Ceiling(2.0 * viewport / singleSetHeight);
        copies = Math.Max(2, copies);
        if (copies != 2)
        {
            CreditsItems.ItemsSource = BuildCopies(_sponsors, copies);
            CreditsItems.UpdateLayout();
            singleSetHeight = CreditsItems.ActualHeight / copies;
            if (singleSetHeight <= 0) return;
        }
        System.Diagnostics.Debug.WriteLine($"[FundingScroll] copies={copies}, singleSetHeight={singleSetHeight}, duration={Math.Max(8, singleSetHeight / 60.0):F1}s");

        // Duration：按 60px/s 计算，最少8秒（ID少时慢速避免循环过快闪烁）
        const double speed = 60.0;
        var durationSec = singleSetHeight / speed;
        if (durationSec < 8) durationSec = 8;

        // 用完整属性路径动画（参考 Endfield：(UIElement.RenderTransform).(TranslateTransform.Y)）
        var anim = new DoubleAnimation(0, -singleSetHeight, TimeSpan.FromSeconds(durationSec))
        {
            RepeatBehavior = RepeatBehavior.Forever
        };
        Storyboard.SetTarget(anim, CreditsItems);
        Storyboard.SetTargetProperty(anim, new PropertyPath("(UIElement.RenderTransform).(TranslateTransform.Y)"));

        _scrollStoryboard = new Storyboard();
        _scrollStoryboard.Children.Add(anim);
        _scrollStoryboard.Begin();
    }

    /// <summary>
    /// 复制 sponsor 列表 copies 份
    /// </summary>
    private static List<string> BuildCopies(List<string> sponsors, int copies)
    {
        var display = new List<string>(sponsors.Count * copies);
        for (var i = 0; i < copies; i++)
            display.AddRange(sponsors);
        return display;
    }

    private void StopScroll()
    {
        _scrollStoryboard?.Stop();
        _scrollStoryboard = null;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
