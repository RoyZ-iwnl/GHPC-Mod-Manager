using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Windows;

namespace GHPC_Mod_Manager.Services
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
                    using var pipe = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.In,
                        maxNumberOfServerInstances: 1,
                        PipeTransmissionMode.Message,
                        PipeOptions.Asynchronous);

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

        public void Dispose()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }
    }
}
