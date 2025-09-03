using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Globalization;
using System.Resources;
using System.IO;

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
    private readonly string _logsDirectory;
    private readonly object _fileLock = new();
    private string _currentLogFile = string.Empty;

    public event EventHandler<LogEventArgs>? LogReceived;

    public LoggingService(ILogger<LoggingService> logger)
    {
        _logger = logger;
        _resourceManager = new ResourceManager("GHPC_Mod_Manager.Resources.Strings", typeof(App).Assembly);
        
        // Initialize logs directory
        var appDataPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app_data");
        _logsDirectory = Path.Combine(appDataPath, "logs");
        Directory.CreateDirectory(_logsDirectory);
        
        // Initialize daily log file
        InitializeDailyLogFile();
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

        // Write to daily log file
        WriteToDailyLogFile(entry);

        LogReceived?.Invoke(this, new LogEventArgs(entry));
    }

    private void InitializeDailyLogFile()
    {
        var today = DateTime.Now;
        var fileName = $"GHPC_Mod_Manager_{today:yyyy-MM-dd}.log";
        _currentLogFile = Path.Combine(_logsDirectory, fileName);
        
        // Clean up old log files (keep last 30 days)
        CleanupOldLogFiles();
        
        // Write session start marker
        var startMessage = $"=== Session started at {today:yyyy-MM-dd HH:mm:ss} ===";
        WriteToLogFile(startMessage);
    }

    private void WriteToDailyLogFile(LogEntry entry)
    {
        try
        {
            // Check if we need to switch to a new daily log file
            var today = DateTime.Now.Date;
            var currentFileDate = File.Exists(_currentLogFile) ? 
                File.GetCreationTime(_currentLogFile).Date : 
                DateTime.MinValue.Date;
                
            if (today != currentFileDate)
            {
                InitializeDailyLogFile();
            }

            var logLine = $"[{entry.Timestamp:HH:mm:ss}] [{entry.Level}] {entry.Message}";
            WriteToLogFile(logLine);
        }
        catch
        {
            // Ignore file write errors to prevent recursion
        }
    }

    private void WriteToLogFile(string message)
    {
        lock (_fileLock)
        {
            try
            {
                File.AppendAllText(_currentLogFile, message + Environment.NewLine);
            }
            catch
            {
                // Ignore file write errors
            }
        }
    }

    private void CleanupOldLogFiles()
    {
        try
        {
            var cutoffDate = DateTime.Now.AddDays(-30);
            var logFiles = Directory.GetFiles(_logsDirectory, "GHPC_Mod_Manager_*.log");
            
            foreach (var logFile in logFiles)
            {
                var fileDate = File.GetCreationTime(logFile);
                if (fileDate < cutoffDate)
                {
                    File.Delete(logFile);
                }
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
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