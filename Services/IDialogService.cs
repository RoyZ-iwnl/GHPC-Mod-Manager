using GHPC_Mod_Manager.Models;

namespace GHPC_Mod_Manager.Services
{
    /// <summary>
    /// 对话框服务接口，用于显示自定义消息对话框
    /// </summary>
    public interface IDialogService
    {
        /// <summary>
        /// 显示信息提示对话框
        /// </summary>
        /// <param name="message">消息内容</param>
        /// <param name="title">标题</param>
        void Show(string message, string title);

        /// <summary>
        /// 显示确认对话框（是/否）
        /// </summary>
        /// <param name="message">消息内容</param>
        /// <param name="title">标题</param>
        /// <returns>用户是否点击了"是"</returns>
        bool Confirm(string message, string title);

        /// <summary>
        /// 显示确认对话框（确定/取消）
        /// </summary>
        /// <param name="message">消息内容</param>
        /// <param name="title">标题</param>
        /// <returns>用户是否点击了"确定"</returns>
        bool ConfirmOK(string message, string title);

        /// <summary>
        /// 显示信息对话框
        /// </summary>
        void ShowInformation(string message, string title);

        /// <summary>
        /// 显示警告对话框
        /// </summary>
        void ShowWarning(string message, string title);

        /// <summary>
        /// 显示错误对话框
        /// </summary>
        void ShowError(string message, string title);

        /// <summary>
        /// 显示成功对话框
        /// </summary>
        void ShowSuccess(string message, string title);

        /// <summary>
        /// 显示完整参数的对话框
        /// </summary>
        /// <param name="message">消息内容</param>
        /// <param name="title">标题</param>
        /// <param name="button">按钮类型</param>
        /// <param name="icon">图标类型</param>
        /// <returns>对话框返回结果</returns>
        MessageDialogResult Show(string message, string title, MessageDialogButton button, MessageDialogImage icon);
    }
}