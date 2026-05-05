using GHPC_Mod_Manager.Models;
using GHPC_Mod_Manager.Resources;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace GHPC_Mod_Manager.Views
{
    /// <summary>
    /// 自定义消息对话框，支持深色/浅色主题
    /// </summary>
    public partial class MessageDialog : Window
    {
        private MessageDialogResult _result = MessageDialogResult.None;
        private MessageDialogButton _buttons = MessageDialogButton.OK;

        /// <summary>
        /// 对话框返回结果
        /// </summary>
        public MessageDialogResult Result => _result;

        /// <summary>
        /// 创建消息对话框
        /// </summary>
        public MessageDialog()
        {
            InitializeComponent();
        }

        /// <summary>
        /// 设置对话框内容
        /// </summary>
        /// <param name="message">消息内容</param>
        /// <param name="title">标题</param>
        /// <param name="button">按钮类型</param>
        /// <param name="icon">图标类型</param>
        public void Setup(string message, string title, MessageDialogButton button, MessageDialogImage icon)
        {
            // 设置标题和消息
            TitleText.Text = title;
            MessageText.Text = message;

            // 设置按钮
            _buttons = button;
            SetupButtons(button);

            // 设置图标
            SetupIcon(icon);

            // 设置焦点
            Loaded += (s, e) =>
            {
                // 默认焦点在主按钮上
                var primaryButton = GetPrimaryButton(button);
                if (primaryButton != null)
                {
                    primaryButton.Focus();
                }
            };
        }

        /// <summary>
        /// 根据按钮类型配置按钮显示
        /// </summary>
        private void SetupButtons(MessageDialogButton button)
        {
            // 隐藏所有按钮
            OKButton.Visibility = Visibility.Collapsed;
            CancelButton.Visibility = Visibility.Collapsed;
            YesButton.Visibility = Visibility.Collapsed;
            NoButton.Visibility = Visibility.Collapsed;
            OpenFolderButton.Visibility = Visibility.Collapsed;
            IgnoreButton.Visibility = Visibility.Collapsed;
            GoToSettingsButton.Visibility = Visibility.Collapsed;
            LaunchAppButton.Visibility = Visibility.Collapsed;

            // 根据类型显示按钮
            switch (button)
            {
                case MessageDialogButton.OK:
                    OKButton.Visibility = Visibility.Visible;
                    OKButton.Content = Strings.OK;
                    break;
                case MessageDialogButton.OKCancel:
                    CancelButton.Visibility = Visibility.Visible;
                    CancelButton.Content = Strings.Cancel;
                    OKButton.Visibility = Visibility.Visible;
                    OKButton.Content = Strings.OK;
                    OKButton.Margin = new Thickness(8, 0, 0, 0);
                    break;
                case MessageDialogButton.YesNo:
                    NoButton.Visibility = Visibility.Visible;
                    NoButton.Content = Strings.No;
                    YesButton.Visibility = Visibility.Visible;
                    YesButton.Content = Strings.Yes;
                    YesButton.Margin = new Thickness(8, 0, 0, 0);
                    break;
                case MessageDialogButton.YesNoCancel:
                    CancelButton.Visibility = Visibility.Visible;
                    CancelButton.Content = Strings.Cancel;
                    NoButton.Visibility = Visibility.Visible;
                    NoButton.Content = Strings.No;
                    NoButton.Margin = new Thickness(8, 0, 0, 0);
                    YesButton.Visibility = Visibility.Visible;
                    YesButton.Content = Strings.Yes;
                    YesButton.Margin = new Thickness(8, 0, 0, 0);
                    break;
                case MessageDialogButton.OpenFolderIgnore:
                    IgnoreButton.Visibility = Visibility.Visible;
                    IgnoreButton.Content = Strings.PreviousInstallationIgnore;
                    OpenFolderButton.Visibility = Visibility.Visible;
                    OpenFolderButton.Content = Strings.PreviousInstallationOpenFolder;
                    OpenFolderButton.Margin = new Thickness(8, 0, 0, 0);
                    break;
                case MessageDialogButton.GoToSettingsCancel:
                    CancelButton.Visibility = Visibility.Visible;
                    CancelButton.Content = Strings.Cancel;
                    GoToSettingsButton.Visibility = Visibility.Visible;
                    GoToSettingsButton.Content = Strings.CheckForUpdates;
                    GoToSettingsButton.Margin = new Thickness(8, 0, 0, 0);
                    break;
                case MessageDialogButton.LaunchAppIgnore:
                    IgnoreButton.Visibility = Visibility.Visible;
                    IgnoreButton.Content = Strings.PreviousInstallationIgnore;
                    LaunchAppButton.Visibility = Visibility.Visible;
                    LaunchAppButton.Content = Strings.PreviousInstallationLaunchApp;
                    LaunchAppButton.Margin = new Thickness(8, 0, 0, 0);
                    break;
            }
        }

        /// <summary>
        /// 根据图标类型配置图标显示
        /// </summary>
        private void SetupIcon(MessageDialogImage icon)
        {
            // 隐藏所有图标
            InformationIcon.Visibility = Visibility.Collapsed;
            WarningIcon.Visibility = Visibility.Collapsed;
            ErrorIcon.Visibility = Visibility.Collapsed;
            SuccessIcon.Visibility = Visibility.Collapsed;
            QuestionIcon.Visibility = Visibility.Collapsed;
            IconContainer.Visibility = Visibility.Collapsed;

            // 根据类型显示图标
            switch (icon)
            {
                case MessageDialogImage.Information:
                    InformationIcon.Visibility = Visibility.Visible;
                    IconContainer.Visibility = Visibility.Visible;
                    break;
                case MessageDialogImage.Warning:
                    WarningIcon.Visibility = Visibility.Visible;
                    IconContainer.Visibility = Visibility.Visible;
                    break;
                case MessageDialogImage.Error:
                    ErrorIcon.Visibility = Visibility.Visible;
                    IconContainer.Visibility = Visibility.Visible;
                    break;
                case MessageDialogImage.Success:
                    SuccessIcon.Visibility = Visibility.Visible;
                    IconContainer.Visibility = Visibility.Visible;
                    break;
                case MessageDialogImage.Question:
                    QuestionIcon.Visibility = Visibility.Visible;
                    IconContainer.Visibility = Visibility.Visible;
                    break;
            }
        }

        /// <summary>
        /// 获取主按钮（用于设置焦点）
        /// </summary>
        private Button? GetPrimaryButton(MessageDialogButton button)
        {
            return button switch
            {
                MessageDialogButton.OK => OKButton,
                MessageDialogButton.OKCancel => OKButton,
                MessageDialogButton.YesNo => YesButton,
                MessageDialogButton.YesNoCancel => YesButton,
                MessageDialogButton.OpenFolderIgnore => OpenFolderButton,
                MessageDialogButton.LaunchAppIgnore => LaunchAppButton,
                MessageDialogButton.GoToSettingsCancel => GoToSettingsButton,
                _ => null
            };
        }

        /// <summary>
        /// 确认按钮点击
        /// </summary>
        private void OK_Click(object sender, RoutedEventArgs e)
        {
            _result = MessageDialogResult.OK;
            Close();
        }

        /// <summary>
        /// 取消按钮点击
        /// </summary>
        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            _result = MessageDialogResult.Cancel;
            Close();
        }

        /// <summary>
        /// 是按钮点击
        /// </summary>
        private void Yes_Click(object sender, RoutedEventArgs e)
        {
            _result = MessageDialogResult.Yes;
            Close();
        }

        /// <summary>
        /// 否按钮点击
        /// </summary>
        private void No_Click(object sender, RoutedEventArgs e)
        {
            _result = MessageDialogResult.No;
            Close();
        }

        /// <summary>
        /// 打开目录按钮点击
        /// </summary>
        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            _result = MessageDialogResult.OpenFolder;
            Close();
        }

        /// <summary>
        /// 忽略按钮点击
        /// </summary>
        private void Ignore_Click(object sender, RoutedEventArgs e)
        {
            _result = MessageDialogResult.Ignore;
            Close();
        }

        /// <summary>
        /// 去设置更新按钮点击
        /// </summary>
        private void GoToSettings_Click(object sender, RoutedEventArgs e)
        {
            _result = MessageDialogResult.GoToSettings;
            Close();
        }

        /// <summary>
        /// 启动旧应用按钮点击
        /// </summary>
        private void LaunchApp_Click(object sender, RoutedEventArgs e)
        {
            _result = MessageDialogResult.LaunchApp;
            Close();
        }

        /// <summary>
        /// 标题栏拖拽移动窗口
        /// </summary>
        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 1)
            {
                DragMove();
            }
        }

        /// <summary>
        /// 键盘快捷键处理
        /// </summary>
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);

            // ESC键取消
            if (e.Key == Key.Escape)
            {
                if (_buttons == MessageDialogButton.OK)
                {
                    _result = MessageDialogResult.OK;
                }
                else
                {
                    _result = MessageDialogResult.Cancel;
                }
                Close();
            }

            // Enter键确认
            if (e.Key == Key.Enter)
            {
                if (_buttons == MessageDialogButton.OK || _buttons == MessageDialogButton.OKCancel)
                {
                    _result = MessageDialogResult.OK;
                }
                else if (_buttons == MessageDialogButton.YesNo || _buttons == MessageDialogButton.YesNoCancel)
                {
                    _result = MessageDialogResult.Yes;
                }
                else if (_buttons == MessageDialogButton.OpenFolderIgnore)
                {
                    _result = MessageDialogResult.OpenFolder;
                }
                else if (_buttons == MessageDialogButton.LaunchAppIgnore)
                {
                    _result = MessageDialogResult.LaunchApp;
                }
                else if (_buttons == MessageDialogButton.GoToSettingsCancel)
                {
                    _result = MessageDialogResult.GoToSettings;
                }
                Close();
            }
        }
    }
}