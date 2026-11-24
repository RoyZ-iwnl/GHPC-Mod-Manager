using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Windows;

namespace GHPC_Mod_Manager.Services.Protocol
{
    /// <summary>
    /// Named Pipe协议IPC服务端实现
    /// </summary>
    public sealed class NamedPipeProtocolIpcServer : IProtocolIpcServer
    {
        private const string PipeName = "GHPC_Mod_Manager_ProtocolPipe";
        private readonly IProtocolActivationService _protocolActivationService;
        private readonly ILoggingService _loggingService;
        private CancellationTokenSource? _cts;

        public NamedPipeProtocolIpcServer(
            IProtocolActivationService protocolActivationService,
            ILoggingService loggingService)
        {
            _protocolActivationService = protocolActivationService;
            _loggingService = loggingService;
        }

        public void Start()
        {
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => ListenLoopAsync(_cts.Token));
        }

        private async Task ListenLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    // 显式允许当前用户写入：本实例以管理员身份运行时，普通权限的第二实例（点链接启动）也能连上
                    using var pipe = NamedPipeServerStreamAcl.Create(
                        PipeName,
                        PipeDirection.In,
                        maxNumberOfServerInstances: 1,
                        PipeTransmissionMode.Message,
                        PipeOptions.Asynchronous,
                        inBufferSize: 0,
                        outBufferSize: 0,
                        CreatePipeSecurity());

                    await pipe.WaitForConnectionAsync(token);
                    using var reader = new StreamReader(pipe, Encoding.UTF8);
                    var uri = await reader.ReadLineAsync();

                    if (!string.IsNullOrWhiteSpace(uri))
                    {
                        await _protocolActivationService.HandleAsync(uri);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _loggingService.LogError(ex, "Protocol IPC server error");
                }
            }
        }

        // 当前用户（不论是否提权都是同一个账户SID）可读写，管理员和SYSTEM完全控制
        private static PipeSecurity CreatePipeSecurity()
        {
            var security = new PipeSecurity();
            using var identity = WindowsIdentity.GetCurrent();
            if (identity.User != null)
                security.AddAccessRule(new PipeAccessRule(identity.User, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
            return security;
        }

        public void Dispose()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }
    }
}
