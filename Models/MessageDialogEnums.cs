namespace GHPC_Mod_Manager.Models
{
    /// <summary>
    /// 消息对话框按钮类型
    /// </summary>
    public enum MessageDialogButton
    {
        /// <summary>仅显示确认按钮</summary>
        OK,
        /// <summary>显示确认和取消按钮</summary>
        OKCancel,
        /// <summary>显示是和否按钮</summary>
        YesNo,
        /// <summary>显示是、否和取消按钮</summary>
        YesNoCancel,
        /// <summary>显示"打开目录"和"忽略"按钮</summary>
        OpenFolderIgnore
    }

    /// <summary>
    /// 消息对话框返回结果
    /// </summary>
    public enum MessageDialogResult
    {
        /// <summary>用户点击了确认按钮</summary>
        OK,
        /// <summary>用户点击了取消按钮</summary>
        Cancel,
        /// <summary>用户点击了是按钮</summary>
        Yes,
        /// <summary>用户点击了否按钮</summary>
        No,
        /// <summary>对话框被关闭但没有点击任何按钮</summary>
        None,
        /// <summary>用户点击了"打开目录"按钮</summary>
        OpenFolder,
        /// <summary>用户点击了"忽略"按钮</summary>
        Ignore
    }

    /// <summary>
    /// 消息对话框图标类型
    /// </summary>
    public enum MessageDialogImage
    {
        /// <summary>信息图标（蓝色圆圈+i）</summary>
        Information,
        /// <summary>警告图标（黄色三角形+!）</summary>
        Warning,
        /// <summary>错误图标（红色圆圈+X）</summary>
        Error,
        /// <summary>成功图标（绿色圆圈+勾）</summary>
        Success,
        /// <summary>询问图标（蓝色圆圈+?）</summary>
        Question
    }
}