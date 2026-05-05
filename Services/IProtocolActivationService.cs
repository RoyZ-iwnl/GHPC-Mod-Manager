namespace GHPC_Mod_Manager.Services
{
    /// <summary>
    /// 协议激活服务接口
    /// </summary>
    public interface IProtocolActivationService
    {
        /// <summary>
        /// 处理协议URI激活
        /// </summary>
        /// <param name="protocolUri">协议URI (例如: ghpcmm://unlock-endfield)</param>
        Task HandleAsync(string protocolUri);
    }
}
