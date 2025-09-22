using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;

namespace GHPC_Mod_Manager.Services;

public class TrackedFileOperations
{
    private readonly IFileOperationTracker _tracker;
    private readonly ILoggingService _loggingService;

    public TrackedFileOperations(IFileOperationTracker tracker, ILoggingService loggingService)
    {
        _tracker = tracker;
        _loggingService = loggingService;
        
        _loggingService.LogInfo("debugtemplog: TrackedFileOperations initialized");
    }

    // ZIP解压操作（适用于Mod和XUnity安装）
    public async Task ExtractZipAsync(byte[] zipData, string targetDirectory, string[]? excludePatterns = null)
    {
        _loggingService.LogInfo("debugtemplog: Starting ZIP extraction to {0}, data size: {1} bytes, exclude patterns: [{2}]", 
            targetDirectory, zipData.Length, excludePatterns != null ? string.Join(", ", excludePatterns) : "none");

        using var zipStream = new MemoryStream(zipData);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);

        _loggingService.LogInfo("debugtemplog: ZIP archive opened, total entries: {0}", archive.Entries.Count);

        int processedCount = 0;
        int skippedCount = 0;

        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) 
            {
                _loggingService.LogInfo("debugtemplog: Skipping directory entry: {0}", entry.FullName);
                continue;
            }
            
            // 检查排除模式
            bool shouldExclude = excludePatterns?.Any(pattern => entry.FullName.Contains(pattern)) == true;
            if (shouldExclude)
            {
                _loggingService.LogInfo("debugtemplog: Excluding file due to pattern: {0}", entry.FullName);
                skippedCount++;
                continue;
            }

            var destinationPath = Path.Combine(targetDirectory, entry.FullName);
            var destinationDir = Path.GetDirectoryName(destinationPath);
            
            _loggingService.LogInfo("debugtemplog: Processing ZIP entry: {0} -> {1}", entry.FullName, destinationPath);
            
            if (!string.IsNullOrEmpty(destinationDir))
            {
                Directory.CreateDirectory(destinationDir);
                _loggingService.LogInfo("debugtemplog: Created directory: {0}", destinationDir);
            }

            // 检查是否覆盖现有文件
            bool isOverwrite = File.Exists(destinationPath);
            string? originalHash = null;
            
            if (isOverwrite)
            {
                originalHash = await CalculateFileHashAsync(destinationPath);
                _loggingService.LogInfo("debugtemplog: Will overwrite existing file: {0} (original hash: {1})", 
                    destinationPath, originalHash);
            }
            else
            {
                _loggingService.LogInfo("debugtemplog: Creating new file: {0}", destinationPath);
            }

            // 解压文件
            try
            {
                entry.ExtractToFile(destinationPath, true);
                _loggingService.LogInfo("debugtemplog: Successfully extracted: {0} ({1} bytes)", 
                    destinationPath, entry.Length);
            }
            catch (Exception ex)
            {
                _loggingService.LogError("debugtemplog: Failed to extract {0}: {1}", entry.FullName, ex.Message);
                throw;
            }
            
            // 记录操作
            _tracker.RecordFileOperation(new FileOperation
            {
                Type = isOverwrite ? FileOperationType.Overwrite : FileOperationType.Create,
                SourcePath = entry.FullName,
                TargetPath = destinationPath,
                FileSize = entry.Length,
                OriginalHash = originalHash
            });
            
            processedCount++;
        }

        _loggingService.LogInfo("debugtemplog: ZIP extraction completed. Processed: {0}, Skipped: {1}, Total entries: {2}", 
            processedCount, skippedCount, archive.Entries.Count);
    }

    // 单文件复制（适用于DLL直接下载）
    public async Task CopyFileAsync(string sourcePath, string targetPath)
    {
        _loggingService.LogInfo("debugtemplog: Starting file copy: {0} -> {1}", sourcePath, targetPath);

        if (!File.Exists(sourcePath))
        {
            _loggingService.LogError("debugtemplog: Source file does not exist: {0}", sourcePath);
            throw new FileNotFoundException($"Source file not found: {sourcePath}");
        }

        bool isOverwrite = File.Exists(targetPath);
        string? originalHash = null;
        
        if (isOverwrite)
        {
            originalHash = await CalculateFileHashAsync(targetPath);
            _loggingService.LogInfo("debugtemplog: Will overwrite existing file: {0} (original hash: {1})", 
                targetPath, originalHash);
        }
        else
        {
            _loggingService.LogInfo("debugtemplog: Creating new file: {0}", targetPath);
        }

        // 确保目标目录存在
        var targetDir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(targetDir))
        {
            Directory.CreateDirectory(targetDir);
            _loggingService.LogInfo("debugtemplog: Ensured target directory exists: {0}", targetDir);
        }

        try
        {
            File.Copy(sourcePath, targetPath, true);
            var fileSize = new System.IO.FileInfo(targetPath).Length;
            _loggingService.LogInfo("debugtemplog: Successfully copied file: {0} ({1} bytes)", targetPath, fileSize);

            _tracker.RecordFileOperation(new FileOperation
            {
                Type = isOverwrite ? FileOperationType.Overwrite : FileOperationType.Create,
                SourcePath = sourcePath,
                TargetPath = targetPath,
                FileSize = fileSize,
                OriginalHash = originalHash
            });
        }
        catch (Exception ex)
        {
            _loggingService.LogError("debugtemplog: Failed to copy file {0} -> {1}: {2}", sourcePath, targetPath, ex.Message);
            throw;
        }
    }

    // 目录复制（适用于Git Clone后的文件复制）
    public async Task CopyDirectoryAsync(string sourceDir, string targetDir, string[]? excludePatterns = null)
    {
        _loggingService.LogInfo("debugtemplog: Starting directory copy: {0} -> {1}, exclude patterns: [{2}]", 
            sourceDir, targetDir, excludePatterns != null ? string.Join(", ", excludePatterns) : "none");

        if (!Directory.Exists(sourceDir))
        {
            _loggingService.LogError("debugtemplog: Source directory does not exist: {0}", sourceDir);
            throw new DirectoryNotFoundException($"Source directory not found: {sourceDir}");
        }

        await CopyDirectoryRecursiveAsync(sourceDir, targetDir, sourceDir, excludePatterns);
        
        _loggingService.LogInfo("debugtemplog: Directory copy completed: {0} -> {1}", sourceDir, targetDir);
    }

    private async Task CopyDirectoryRecursiveAsync(string sourceDir, string targetDir, string originalSourceRoot, string[]? excludePatterns)
    {
        _loggingService.LogInfo("debugtemplog: Processing directory: {0} -> {1}", sourceDir, targetDir);

        Directory.CreateDirectory(targetDir);

        // 复制文件
        var files = Directory.GetFiles(sourceDir);
        _loggingService.LogInfo("debugtemplog: Found {0} files in {1}", files.Length, sourceDir);

        foreach (var filePath in files)
        {
            var fileName = Path.GetFileName(filePath);
            var relativePath = Path.GetRelativePath(originalSourceRoot, filePath);
            
            // 检查排除模式
            bool shouldExclude = excludePatterns?.Any(pattern => relativePath.Contains(pattern)) == true;
            if (shouldExclude)
            {
                _loggingService.LogInfo("debugtemplog: Excluding file due to pattern: {0}", relativePath);
                continue;
            }

            var targetPath = Path.Combine(targetDir, fileName);
            _loggingService.LogInfo("debugtemplog: Copying file: {0} -> {1}", filePath, targetPath);
            
            await CopyFileAsync(filePath, targetPath);
        }

        // 递归复制子目录
        var subDirs = Directory.GetDirectories(sourceDir);
        _loggingService.LogInfo("debugtemplog: Found {0} subdirectories in {1}", subDirs.Length, sourceDir);

        foreach (var subDir in subDirs)
        {
            var dirName = Path.GetFileName(subDir);
            var relativePath = Path.GetRelativePath(originalSourceRoot, subDir);
            
            // 检查排除模式
            bool shouldExclude = excludePatterns?.Any(pattern => relativePath.Contains(pattern)) == true;
            if (shouldExclude)
            {
                _loggingService.LogInfo("debugtemplog: Excluding directory due to pattern: {0}", relativePath);
                continue;
            }

            var targetSubDir = Path.Combine(targetDir, dirName);
            _loggingService.LogInfo("debugtemplog: Recursing into subdirectory: {0} -> {1}", subDir, targetSubDir);
            
            await CopyDirectoryRecursiveAsync(subDir, targetSubDir, originalSourceRoot, excludePatterns);
        }
    }

    private async Task<string> CalculateFileHashAsync(string filePath)
    {
        _loggingService.LogInfo("debugtemplog: Calculating hash for file: {0}", filePath);
        
        try
        {
            using var sha256 = SHA256.Create();
            using var stream = File.OpenRead(filePath);
            var hashBytes = await sha256.ComputeHashAsync(stream);
            var hash = Convert.ToHexString(hashBytes).ToLowerInvariant();
            
            _loggingService.LogInfo("debugtemplog: Hash calculated for {0}: {1}", filePath, hash);
            return hash;
        }
        catch (Exception ex)
        {
            _loggingService.LogError("debugtemplog: Failed to calculate hash for {0}: {1}", filePath, ex.Message);
            throw;
        }
    }
}