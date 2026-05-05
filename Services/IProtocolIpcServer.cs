namespace GHPC_Mod_Manager.Services
{
    /// <summary>
    /// 协议IPC服务端接口（主实例）
    /// </summary>
    public interface IProtocolIpcServer : IDisposable
    {
        /// <summary>
        /// 启动IPC服务端，监听协议URI
        /// </summary>
        void Start();
    }
}
