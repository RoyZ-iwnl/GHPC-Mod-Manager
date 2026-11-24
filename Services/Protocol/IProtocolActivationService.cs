namespace GHPC_Mod_Manager.Services.Protocol
{
    /// <summary>
    /// 协议激活服务接口
    /// </summary>
    public interface IProtocolActivationService
    {
        /// <summary>
        /// 处理协议URI激活
        /// </summary>
        /// <param name="protocolUri">协议URI (例如: ghpcmm://theme/unlock-endfield, ghpcmm://mod/install/ModId)</param>
        Task HandleAsync(string protocolUri);
    }

    /// <summary>
    /// ghpcmm://mod/... 链接的操作类型
    /// </summary>
    public enum ProtocolModAction
    {
        // 未安装时安装最新版，已安装但有新版时提示更新
        Install,
        // 与 Install 相同，供"有新版本"按钮使用
        Update,
        // 只打开详情页
        Open
    }
}
