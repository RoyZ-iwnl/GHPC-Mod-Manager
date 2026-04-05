using GHPC_Mod_Manager.Models;
using GHPC_Mod_Manager.Views;
using System.Windows;

namespace GHPC_Mod_Manager.Helpers
{
    /// <summary>
    /// 消息对话框辅助类，提供类似MessageBox的静态方法
    /// </summary>
    public static class MessageDialogHelper
    {
        /// <summary>
        /// 显示信息提示对话框
        /// </summary>
        /// <param name="message">消息内容</param>
        /// <param name="title">标题（可选）</param>
        public static MessageDialogResult Show(string message, string? title = null)
        {
            return Show(message, title ?? string.Empty, MessageDialogButton.OK, MessageDialogImage.Information);
        }

        /// <summary>
        /// 显示确认对话框（是/否）
        /// </summary>
        /// <param name="message">消息内容</param>
        /// <param name="title">标题（可选）</param>
        /// <returns>用户是否点击了"是"</returns>
        public static bool Confirm(string message, string? title = null)
        {
            var result = Show(message, title ?? string.Empty, MessageDialogButton.YesNo, MessageDialogImage.Question);
            return result == MessageDialogResult.Yes;
        }

        /// <summary>
        /// 显示确认对话框（确定/取消）
        /// </summary>
        /// <param name="message">消息内容</param>
        /// <param name="title">标题（可选）</param>
        /// <returns>用户是否点击了"确定"</returns>
        public static bool ConfirmOK(string message, string? title = null)
        {
            var result = Show(message, title ?? string.Empty, MessageDialogButton.OKCancel, MessageDialogImage.Question);
            return result == MessageDialogResult.OK;
        }

        /// <summary>
        /// 显示信息对话框
        /// </summary>
        /// <param name="message">消息内容</param>
        /// <param name="title">标题</param>
        public static void ShowInformation(string message, string title)
        {
            Show(message, title, MessageDialogButton.OK, MessageDialogImage.Information);
        }

        /// <summary>
        /// 显示警告对话框
        /// </summary>
        /// <param name="message">消息内容</param>
        /// <param name="title">标题</param>
        public static void ShowWarning(string message, string title)
        {
            Show(message, title, MessageDialogButton.OK, MessageDialogImage.Warning);
        }

        /// <summary>
        /// 显示错误对话框
        /// </summary>
        /// <param name="message">消息内容</param>
        /// <param name="title">标题</param>
        public static void ShowError(string message, string title)
        {
            Show(message, title, MessageDialogButton.OK, MessageDialogImage.Error);
        }

        /// <summary>
        /// 显示成功对话框
        /// </summary>
        /// <param name="message">消息内容</param>
        /// <param name="title">标题</param>
        public static void ShowSuccess(string message, string title)
        {
            Show(message, title, MessageDialogButton.OK, MessageDialogImage.Success);
        }

        /// <summary>
        /// 显示完整参数的对话框
        /// </summary>
        /// <param name="message">消息内容</param>
        /// <param name="title">标题</param>
        /// <param name="button">按钮类型</param>
        /// <param name="icon">图标类型</param>
        /// <returns>对话框返回结果</returns>
        public static MessageDialogResult Show(string message, string title, MessageDialogButton button, MessageDialogImage icon)
        {
            var dialog = new MessageDialog();
            dialog.Setup(message, title, button, icon);
            dialog.Owner = Application.Current.MainWindow;
            dialog.ShowDialog();
            return dialog.Result;
        }

        #region 异步方法

        /// <summary>
        /// 异步显示信息提示对话框
        /// </summary>
        public static Task<MessageDialogResult> ShowAsync(string message, string? title = null)
        {
            return Task.FromResult(Show(message, title ?? string.Empty, MessageDialogButton.OK, MessageDialogImage.Information));
        }

        /// <summary>
        /// 异步显示确认对话框（是/否）
        /// </summary>
        /// <returns>用户是否点击了"是"</returns>
        public static Task<bool> ConfirmAsync(string message, string? title = null)
        {
            var result = Show(message, title ?? string.Empty, MessageDialogButton.YesNo, MessageDialogImage.Question);
            return Task.FromResult(result == MessageDialogResult.Yes);
        }

        /// <summary>
        /// 异步显示确认对话框（确定/取消）
        /// </summary>
        /// <returns>用户是否点击了"确定"</returns>
        public static Task<bool> ConfirmOKAsync(string message, string? title = null)
        {
            var result = Show(message, title ?? string.Empty, MessageDialogButton.OKCancel, MessageDialogImage.Question);
            return Task.FromResult(result == MessageDialogResult.OK);
        }

        /// <summary>
        /// 异步显示信息对话框
        /// </summary>
        public static Task ShowInformationAsync(string message, string title)
        {
            Show(message, title, MessageDialogButton.OK, MessageDialogImage.Information);
            return Task.CompletedTask;
        }

        /// <summary>
        /// 异步显示警告对话框
        /// </summary>
        public static Task ShowWarningAsync(string message, string title)
        {
            Show(message, title, MessageDialogButton.OK, MessageDialogImage.Warning);
            return Task.CompletedTask;
        }

        /// <summary>
        /// 异步显示错误对话框
        /// </summary>
        public static Task ShowErrorAsync(string message, string title)
        {
            Show(message, title, MessageDialogButton.OK, MessageDialogImage.Error);
            return Task.CompletedTask;
        }

        /// <summary>
        /// 异步显示成功对话框
        /// </summary>
        public static Task ShowSuccessAsync(string message, string title)
        {
            Show(message, title, MessageDialogButton.OK, MessageDialogImage.Success);
            return Task.CompletedTask;
        }

        /// <summary>
        /// 显示打开目录/忽略对话框
        /// </summary>
        /// <param name="message">消息内容</param>
        /// <param name="title">标题</param>
        /// <returns>对话框返回结果</returns>
        public static MessageDialogResult ShowOpenFolderIgnore(string message, string? title = null)
        {
            return Show(message, title ?? string.Empty, MessageDialogButton.OpenFolderIgnore, MessageDialogImage.Warning);
        }

        /// <summary>
        /// 显示去设置更新/取消对话框
        /// </summary>
        /// <param name="message">消息内容</param>
        /// <param name="title">标题</param>
        /// <returns>用户是否点击了"去设置更新"</returns>
        public static bool ShowGoToSettingsCancel(string message, string? title = null)
        {
            var result = Show(message, title ?? string.Empty, MessageDialogButton.GoToSettingsCancel, MessageDialogImage.Warning);
            return result == MessageDialogResult.GoToSettings;
        }

        /// <summary>
        /// 异步显示完整参数的对话框
        /// </summary>
        /// <returns>对话框返回结果</returns>
        public static Task<MessageDialogResult> ShowAsync(string message, string title, MessageDialogButton button, MessageDialogImage icon)
        {
            return Task.FromResult(Show(message, title, button, icon));
        }

        #endregion
    }
}