using System.Diagnostics;
using System.IO;
using GHPC_Mod_Manager.Resources;

namespace GHPC_Mod_Manager.Services;

public interface IProcessService
{
    bool IsGameRunning { get; }
    event EventHandler<bool> GameRunningStateChanged;
    Task StartMonitoringAsync();
    void StopMonitoring();
    Task<bool> LaunchGameAsync(string gamePath);
    Task<bool> StopGameAsync();
    Task WaitForGameExitAsync();
}

public class ProcessService : IProcessService
{
    private readonly ILoggingService _loggingService;
    private readonly Timer _monitorTimer;
    private bool _isGameRunning;
    private bool _isMonitoring;

    public bool IsGameRunning
    {
        get => _isGameRunning;
        private set
        {
            if (_isGameRunning != value)
            {
                _isGameRunning = value;
                GameRunningStateChanged?.Invoke(this, value);
                _loggingService.LogInfo(value ? Strings.GameStarted : Strings.GameStopped);
            }
        }
    }

    public event EventHandler<bool>? GameRunningStateChanged;

    public ProcessService(ILoggingService loggingService)
    {
        _loggingService = loggingService;
        _monitorTimer = new Timer(CheckGameProcess, null, Timeout.Infinite, Timeout.Infinite);
    }

    public Task StartMonitoringAsync()
    {
        if (!_isMonitoring)
        {
            _isMonitoring = true;
            // 立即检查一次游戏进程状态
            CheckGameProcess(null);
            // 然后每2秒检查一次
            _monitorTimer.Change(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
            _loggingService.LogInfo(Strings.ProcessMonitoringStarted);
        }
        return Task.CompletedTask;
    }

    public void StopMonitoring()
    {
        if (_isMonitoring)
        {
            _isMonitoring = false;
            _monitorTimer.Change(Timeout.Infinite, Timeout.Infinite);
            _loggingService.LogInfo(Strings.ProcessMonitoringStopped);
        }
    }

    private void CheckGameProcess(object? state)
    {
        try
        {
            var processes = Process.GetProcessesByName("GHPC");
            var isRunning = processes.Length > 0;

            // 确保状态正确更新
            IsGameRunning = isRunning;

            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.ProcessCheckError);
            // 发生错误时,假设游戏未运行
            IsGameRunning = false;
        }
    }

    public async Task<bool> LaunchGameAsync(string gamePath)
    {
        try
        {
            var exePath = Path.Combine(gamePath, "GHPC.exe");
            
            if (!File.Exists(exePath))
            {
                _loggingService.LogError(Strings.GameExeNotFound, exePath);
                return false;
            }

            _loggingService.LogInfo(Strings.LaunchingGame, exePath);
            
            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = gamePath,
                UseShellExecute = true
            };

            Process.Start(startInfo);
            
            await Task.Delay(3000);
            
            return true;
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.GameLaunchError, gamePath);
            return false;
        }
    }

    public async Task<bool> StopGameAsync()
    {
        try
        {
            var processes = Process.GetProcessesByName("GHPC");
            
            if (processes.Length == 0)
            {
                _loggingService.LogInfo(Strings.NoGameProcessToStop);
                return true;
            }

            _loggingService.LogInfo(Strings.StoppingGame);
            
            foreach (var process in processes)
            {
                try
                {
                    // First try graceful shutdown
                    if (!process.CloseMainWindow())
                    {
                        // If graceful shutdown fails, force kill
                        process.Kill();
                    }
                    process.Dispose();
                }
                catch (Exception ex)
                {
                    _loggingService.LogError(ex, Strings.ProcessStopError, process.Id.ToString());
                }
            }
            
            // Wait a moment for processes to exit
            await Task.Delay(2000);
            
            // Verify all processes are stopped
            var remainingProcesses = Process.GetProcessesByName("GHPC");
            foreach (var process in remainingProcesses)
            {
                process.Dispose();
            }
            
            bool success = remainingProcesses.Length == 0;
            _loggingService.LogInfo(success ? Strings.GameStoppedSuccessfully : Strings.GameStopFailed);
            return success;
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.GameStopError);
            return false;
        }
    }

    public async Task WaitForGameExitAsync()
    {
        while (IsGameRunning)
        {
            await Task.Delay(1000);
        }
        
        await Task.Delay(10000);
    }

    public void Dispose()
    {
        StopMonitoring();
        _monitorTimer?.Dispose();
    }
}