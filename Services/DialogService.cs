using GHPC_Mod_Manager.Helpers;
using GHPC_Mod_Manager.Models;

namespace GHPC_Mod_Manager.Services
{
    /// <summary>
    /// 对话框服务实现，通过依赖注入提供对话框功能
    /// </summary>
    public class DialogService : IDialogService
    {
        /// <inheritdoc />
        public void Show(string message, string title)
        {
            MessageDialogHelper.Show(message, title);
        }

        /// <inheritdoc />
        public bool Confirm(string message, string title)
        {
            return MessageDialogHelper.Confirm(message, title);
        }

        /// <inheritdoc />
        public bool ConfirmOK(string message, string title)
        {
            return MessageDialogHelper.ConfirmOK(message, title);
        }

        /// <inheritdoc />
        public void ShowInformation(string message, string title)
        {
            MessageDialogHelper.ShowInformation(message, title);
        }

        /// <inheritdoc />
        public void ShowWarning(string message, string title)
        {
            MessageDialogHelper.ShowWarning(message, title);
        }

        /// <inheritdoc />
        public void ShowError(string message, string title)
        {
            MessageDialogHelper.ShowError(message, title);
        }

        /// <inheritdoc />
        public void ShowSuccess(string message, string title)
        {
            MessageDialogHelper.ShowSuccess(message, title);
        }

        /// <inheritdoc />
        public MessageDialogResult Show(string message, string title, MessageDialogButton button, MessageDialogImage icon)
        {
            return MessageDialogHelper.Show(message, title, button, icon);
        }
    }
}