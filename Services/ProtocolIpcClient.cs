using System.IO;
using System.IO.Pipes;
using System.Text;

namespace GHPC_Mod_Manager.Services
{
    /// <summary>
    /// 协议IPC客户端（第二实例使用）
    /// </summary>
    public static class ProtocolIpcClient
    {
        private const string PipeName = "GHPC_Mod_Manager_ProtocolPipe";

        /// <summary>
        /// 发送协议URI到主实例
        /// </summary>
        public static async Task<bool> SendAsync(string protocolUri, CancellationToken cancellationToken = default)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.Asynchronous);
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(2));

                await pipe.ConnectAsync(timeoutCts.Token);
                await using var writer = new StreamWriter(pipe, Encoding.UTF8) { AutoFlush = true };
                await writer.WriteLineAsync(protocolUri);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
