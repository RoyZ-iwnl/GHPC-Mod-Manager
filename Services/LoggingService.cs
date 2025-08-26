using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Globalization;
using System.Resources;

namespace GHPC_Mod_Manager.Services;

public interface ILoggingService
{
    void LogInfo(string key, params object[] args);
    void LogWarning(string key, params object[] args);
    void LogError(string key, params object[] args);
    void LogError(Exception ex, string key, params object[] args);
    event EventHandler<LogEventArgs> LogReceived;
    IEnumerable<LogEntry> GetRecentLogs();
}

public class LoggingService : ILoggingService
{
    private readonly ILogger<LoggingService> _logger;
    private readonly ResourceManager _resourceManager;
    private readonly ConcurrentQueue<LogEntry> _logQueue = new();
    private readonly int _maxLogEntries = 1000;

    public event EventHandler<LogEventArgs>? LogReceived;

    public LoggingService(ILogger<LoggingService> logger)
    {
        _logger = logger;
        _resourceManager = new ResourceManager("GHPC_Mod_Manager.Resources.Strings", typeof(App).Assembly);
    }

    public void LogInfo(string key, params object[] args)
    {
        var message = GetLocalizedMessage(key, args);
        _logger.LogInformation(message);
        AddLogEntry(LogLevel.Information, message);
    }

    public void LogWarning(string key, params object[] args)
    {
        var message = GetLocalizedMessage(key, args);
        _logger.LogWarning(message);
        AddLogEntry(LogLevel.Warning, message);
    }

    public void LogError(string key, params object[] args)
    {
        var message = GetLocalizedMessage(key, args);
        _logger.LogError(message);
        AddLogEntry(LogLevel.Error, message);
    }

    public void LogError(Exception ex, string key, params object[] args)
    {
        var message = GetLocalizedMessage(key, args);
        _logger.LogError(ex, message);
        AddLogEntry(LogLevel.Error, $"{message}: {ex.Message}");
    }

    private string GetLocalizedMessage(string key, params object[] args)
    {
        try
        {
            var format = _resourceManager.GetString(key, CultureInfo.CurrentUICulture) ?? key;
            return args.Length > 0 ? string.Format(format, args) : format;
        }
        catch
        {
            return key;
        }
    }

    private void AddLogEntry(LogLevel level, string message)
    {
        var entry = new LogEntry
        {
            Timestamp = DateTime.Now,
            Level = level,
            Message = message
        };

        _logQueue.Enqueue(entry);

        while (_logQueue.Count > _maxLogEntries)
        {
            _logQueue.TryDequeue(out _);
        }

        LogReceived?.Invoke(this, new LogEventArgs(entry));
    }

    public IEnumerable<LogEntry> GetRecentLogs()
    {
        return _logQueue.ToArray().Reverse();
    }
}

public class LogEntry
{
    public DateTime Timestamp { get; set; }
    public LogLevel Level { get; set; }
    public string Message { get; set; } = string.Empty;
}

public class LogEventArgs : EventArgs
{
    public LogEntry LogEntry { get; }

    public LogEventArgs(LogEntry logEntry)
    {
        LogEntry = logEntry;
    }
}