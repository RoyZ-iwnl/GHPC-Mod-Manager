using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using GHPC_Mod_Manager.Resources;

namespace GHPC_Mod_Manager.Views.Controls
{
    /// <summary>
    /// 自定义窗口标题栏控件，支持最小化、最大化/还原、关闭按钮
    /// </summary>
    public partial class WindowTitleBar : UserControl
    {
        private Window? _parentWindow;

        public WindowTitleBar()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _parentWindow = Window.GetWindow(this);
            if (_parentWindow != null)
            {
                _parentWindow.StateChanged += OnWindowStateChanged;
                UpdateMaximizeButtonState();
            }
        }

        /// <summary>
        /// 最小化按钮点击
        /// </summary>
        private void Minimize_Click(object sender, RoutedEventArgs e)
        {
            if (_parentWindow != null)
            {
                _parentWindow.WindowState = WindowState.Minimized;
            }
        }

        /// <summary>
        /// 最大化/还原按钮点击
        /// </summary>
        private void Maximize_Click(object sender, RoutedEventArgs e)
        {
            if (_parentWindow != null)
            {
                if (_parentWindow.WindowState == WindowState.Maximized)
                {
                    _parentWindow.WindowState = WindowState.Normal;
                }
                else
                {
                    _parentWindow.WindowState = WindowState.Maximized;
                }
            }
        }

        /// <summary>
        /// 关闭按钮点击
        /// </summary>
        private void Close_Click(object sender, RoutedEventArgs e)
        {
            if (_parentWindow != null)
            {
                _parentWindow.Close();
            }
        }

        /// <summary>
        /// 窗口状态改变时更新最大化按钮图标和ToolTip
        /// </summary>
        private void OnWindowStateChanged(object sender, EventArgs e)
        {
            UpdateMaximizeButtonState();
        }

        /// <summary>
        /// 更新最大化按钮图标和ToolTip
        /// </summary>
        private void UpdateMaximizeButtonState()
        {
            if (MaximizePath == null || MaximizeButton == null) return;

            if (_parentWindow?.WindowState == WindowState.Maximized)
            {
                // 还原图标：两个重叠的方块
                var iconRestore = App.Current.TryFindResource("IconRestore") as Geometry;
                if (iconRestore != null)
                {
                    MaximizePath.Data = iconRestore;
                }
                MaximizeButton.ToolTip = Strings.Restore;
            }
            else
            {
                // 最大化图标：单个方块
                var iconMaximize = App.Current.TryFindResource("IconMaximize") as Geometry;
                if (iconMaximize != null)
                {
                    MaximizePath.Data = iconMaximize;
                }
                MaximizeButton.ToolTip = Strings.Maximize;
            }
        }

        /// <summary>
        /// 鼠标左键按下时开始拖拽窗口
        /// </summary>
        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);

            if (_parentWindow != null)
            {
                // 双击切换最大化/还原
                if (e.ClickCount == 2)
                {
                    if (_parentWindow.WindowState == WindowState.Maximized)
                    {
                        _parentWindow.WindowState = WindowState.Normal;
                    }
                    else
                    {
                        _parentWindow.WindowState = WindowState.Maximized;
                    }
                    return;
                }

                // 拖拽移动窗口
                if (_parentWindow.WindowState == WindowState.Maximized)
                {
                    // 最大化状态下拖拽需要先还原窗口并调整位置
                    _parentWindow.WindowState = WindowState.Normal;
                }

                _parentWindow.DragMove();
            }
        }
    }
}