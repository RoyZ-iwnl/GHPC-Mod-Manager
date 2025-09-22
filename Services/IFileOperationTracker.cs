using System.IO;
using GHPC_Mod_Manager.Resources;

namespace GHPC_Mod_Manager.Services;

public interface IFileOperationTracker
{
    void StartTracking(string operationId, string targetDirectory);
    void RecordFileOperation(FileOperation operation);
    void StopTracking();
    FileOperationResult GetResult();
}

public enum FileOperationType
{
    Create,     // 创建新文件
    Overwrite,  // 覆盖现有文件
    Move,       // 移动文件
    Copy,       // 复制文件
    Delete      // 删除文件
}

public class FileOperation
{
    public FileOperationType Type { get; set; }
    public string SourcePath { get; set; } = string.Empty;
    public string TargetPath { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty; // 相对于游戏根目录
    public DateTime Timestamp { get; set; }
    public long FileSize { get; set; }
    public string? OriginalHash { get; set; } // 被覆盖文件的原始哈希
}

public class FileOperationResult
{
    public string OperationId { get; set; } = string.Empty;
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public List<FileOperation> Operations { get; set; } = new();
    
    public List<string> GetAllProcessedFiles() 
        => Operations.Select(op => op.RelativePath).Distinct().ToList();
    
    public List<string> GetNewFiles() 
        => Operations.Where(op => op.Type == FileOperationType.Create)
                     .Select(op => op.RelativePath).ToList();
    
    public List<string> GetOverwrittenFiles() 
        => Operations.Where(op => op.Type == FileOperationType.Overwrite)
                     .Select(op => op.RelativePath).ToList();
}

public class FileOperationTracker : IFileOperationTracker
{
    private readonly ILoggingService _loggingService;
    private readonly ISettingsService _settingsService;
    
    private string? _operationId;
    private string? _targetDirectory;
    private DateTime _startTime;
    private readonly List<FileOperation> _operations = new();
    
    public FileOperationTracker(ILoggingService loggingService, ISettingsService settingsService)
    {
        _loggingService = loggingService;
        _settingsService = settingsService;
        
        _loggingService.LogInfo("debugtemplog: FileOperationTracker initialized");
    }

    public void StartTracking(string operationId, string targetDirectory)
    {
        _operationId = operationId;
        _targetDirectory = targetDirectory;
        _startTime = DateTime.Now;
        _operations.Clear();
        
        _loggingService.LogInfo("debugtemplog: Started file operation tracking: {0} in {1}", operationId, targetDirectory);
    }

    public void RecordFileOperation(FileOperation operation)
    {
        if (_operationId == null) 
        {
            _loggingService.LogError("debugtemplog: Cannot record operation - tracking not started");
            throw new InvalidOperationException("Tracking not started");
        }
        
        // 确保RelativePath是相对于游戏根目录的
        var gameRootPath = _settingsService.Settings.GameRootPath;
        if (Path.IsPathRooted(operation.TargetPath))
        {
            operation.RelativePath = Path.GetRelativePath(gameRootPath, operation.TargetPath);
        }
        else
        {
            operation.RelativePath = operation.TargetPath;
        }
        
        operation.Timestamp = DateTime.Now;
        _operations.Add(operation);
        
        _loggingService.LogInfo("debugtemplog: Recorded file operation #{0}: {1} {2} -> {3} (size: {4} bytes)", 
            _operations.Count, operation.Type, operation.SourcePath, operation.RelativePath, operation.FileSize);
    }

    public void StopTracking()
    {
        _loggingService.LogInfo("debugtemplog: Stopped file operation tracking: {0}, recorded {1} operations in {2}ms", 
            _operationId, _operations.Count, (DateTime.Now - _startTime).TotalMilliseconds);
        
        // debugtemplog: 详细列出所有记录的操作
        _loggingService.LogInfo("debugtemplog: All recorded operations for {0}:", _operationId);
        for (int i = 0; i < _operations.Count; i++)
        {
            var op = _operations[i];
            _loggingService.LogInfo("debugtemplog:   [{0}] {1}: {2} ({3} bytes)", 
                i + 1, op.Type, op.RelativePath, op.FileSize);
        }
    }

    public FileOperationResult GetResult()
    {
        var result = new FileOperationResult
        {
            OperationId = _operationId ?? "",
            StartTime = _startTime,
            EndTime = DateTime.Now,
            Operations = _operations.ToList()
        };
        
        _loggingService.LogInfo("debugtemplog: GetResult called for {0}: {1} total files, {2} new files, {3} overwritten files", 
            result.OperationId, 
            result.GetAllProcessedFiles().Count,
            result.GetNewFiles().Count, 
            result.GetOverwrittenFiles().Count);
            
        return result;
    }
}