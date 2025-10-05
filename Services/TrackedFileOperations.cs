using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using GHPC_Mod_Manager.Resources;

namespace GHPC_Mod_Manager.Services;

public class TrackedFileOperations
{
    private readonly IFileOperationTracker _tracker;
    private readonly ILoggingService _loggingService;

    public TrackedFileOperations(IFileOperationTracker tracker, ILoggingService loggingService)
    {
        _tracker = tracker;
        _loggingService = loggingService;

        _loggingService.LogInfo(Strings.TrackedFileOperationsInit);
    }

    // ZIP解压操作（适用于Mod和XUnity安装）
    public async Task ExtractZipAsync(byte[] zipData, string targetDirectory, string[]? excludePatterns = null)
    {
        _loggingService.LogInfo(Strings.ZipExtractionStarting,
            targetDirectory, zipData.Length, excludePatterns != null ? string.Join(", ", excludePatterns) : "none");

        using var zipStream = new MemoryStream(zipData);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);

        _loggingService.LogInfo(Strings.ZipArchiveOpened, archive.Entries.Count);

        int processedCount = 0;
        int skippedCount = 0;

        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                _loggingService.LogInfo(Strings.ZipSkippingDirectory, entry.FullName);
                continue;
            }

            // 检查排除模式
            bool shouldExclude = excludePatterns?.Any(pattern => entry.FullName.Contains(pattern)) == true;
            if (shouldExclude)
            {
                _loggingService.LogInfo(Strings.ZipExcludingByPattern, entry.FullName);
                skippedCount++;
                continue;
            }

            var destinationPath = Path.Combine(targetDirectory, entry.FullName);
            var destinationDir = Path.GetDirectoryName(destinationPath);

            _loggingService.LogInfo(Strings.ZipProcessingEntry, entry.FullName, destinationPath);

            if (!string.IsNullOrEmpty(destinationDir))
            {
                Directory.CreateDirectory(destinationDir);
                _loggingService.LogInfo(Strings.ZipCreatedDirectory, destinationDir);
            }

            // 检查是否覆盖现有文件
            bool isOverwrite = File.Exists(destinationPath);
            string? originalHash = null;

            if (isOverwrite)
            {
                originalHash = await CalculateFileHashAsync(destinationPath);
                _loggingService.LogInfo(Strings.ZipOverwritingFile,
                    destinationPath, originalHash);
            }
            else
            {
                _loggingService.LogInfo(Strings.ZipCreatingNewFile, destinationPath);
            }

            // 解压文件
            try
            {
                entry.ExtractToFile(destinationPath, true);
                _loggingService.LogInfo(Strings.ZipExtractedSuccessfully,
                    destinationPath, entry.Length);
            }
            catch (Exception ex)
            {
                _loggingService.LogError(Strings.ZipExtractFailed, entry.FullName, ex.Message);
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

        _loggingService.LogInfo(Strings.ZipExtractionCompleted,
            processedCount, skippedCount, archive.Entries.Count);
    }

    // 单文件复制（适用于DLL直接下载）
    public async Task CopyFileAsync(string sourcePath, string targetPath)
    {
        _loggingService.LogInfo(Strings.FileCopyStarting, sourcePath, targetPath);

        if (!File.Exists(sourcePath))
        {
            _loggingService.LogError(Strings.FileCopySourceNotFound, sourcePath);
            throw new FileNotFoundException($"Source file not found: {sourcePath}");
        }

        bool isOverwrite = File.Exists(targetPath);
        string? originalHash = null;

        if (isOverwrite)
        {
            originalHash = await CalculateFileHashAsync(targetPath);
            _loggingService.LogInfo(Strings.FileCopyOverwriting,
                targetPath, originalHash);
        }
        else
        {
            _loggingService.LogInfo(Strings.FileCopyCreatingNew, targetPath);
        }

        // 确保目标目录存在
        var targetDir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(targetDir))
        {
            Directory.CreateDirectory(targetDir);
            _loggingService.LogInfo(Strings.FileCopyEnsuredDirectory, targetDir);
        }

        try
        {
            File.Copy(sourcePath, targetPath, true);
            var fileSize = new System.IO.FileInfo(targetPath).Length;
            _loggingService.LogInfo(Strings.FileCopySuccess, targetPath, fileSize);

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
            _loggingService.LogError(Strings.FileCopyFailed, sourcePath, targetPath, ex.Message);
            throw;
        }
    }

    // 目录复制（适用于Git Clone后的文件复制）
    public async Task CopyDirectoryAsync(string sourceDir, string targetDir, string[]? excludePatterns = null)
    {
        _loggingService.LogInfo(Strings.DirectoryCopyStarting,
            sourceDir, targetDir, excludePatterns != null ? string.Join(", ", excludePatterns) : "none");

        if (!Directory.Exists(sourceDir))
        {
            _loggingService.LogError(Strings.DirectoryCopySourceNotFound, sourceDir);
            throw new DirectoryNotFoundException($"Source directory not found: {sourceDir}");
        }

        await CopyDirectoryRecursiveAsync(sourceDir, targetDir, sourceDir, excludePatterns);

        _loggingService.LogInfo(Strings.DirectoryCopyCompleted, sourceDir, targetDir);
    }

    private async Task CopyDirectoryRecursiveAsync(string sourceDir, string targetDir, string originalSourceRoot, string[]? excludePatterns)
    {
        _loggingService.LogInfo(Strings.DirectoryCopyProcessing, sourceDir, targetDir);

        Directory.CreateDirectory(targetDir);

        // 复制文件
        var files = Directory.GetFiles(sourceDir);
        _loggingService.LogInfo(Strings.DirectoryCopyFoundFiles, files.Length, sourceDir);

        foreach (var filePath in files)
        {
            var fileName = Path.GetFileName(filePath);
            var relativePath = Path.GetRelativePath(originalSourceRoot, filePath);

            // 检查排除模式
            bool shouldExclude = excludePatterns?.Any(pattern => relativePath.Contains(pattern)) == true;
            if (shouldExclude)
            {
                _loggingService.LogInfo(Strings.DirectoryCopyExcludingFile, relativePath);
                continue;
            }

            var targetPath = Path.Combine(targetDir, fileName);
            _loggingService.LogInfo(Strings.DirectoryCopyingFile, filePath, targetPath);

            await CopyFileAsync(filePath, targetPath);
        }

        // 递归复制子目录
        var subDirs = Directory.GetDirectories(sourceDir);
        _loggingService.LogInfo(Strings.DirectoryCopyFoundSubdirs, subDirs.Length, sourceDir);

        foreach (var subDir in subDirs)
        {
            var dirName = Path.GetFileName(subDir);
            var relativePath = Path.GetRelativePath(originalSourceRoot, subDir);

            // 检查排除模式
            bool shouldExclude = excludePatterns?.Any(pattern => relativePath.Contains(pattern)) == true;
            if (shouldExclude)
            {
                _loggingService.LogInfo(Strings.DirectoryCopyExcludingDir, relativePath);
                continue;
            }

            var targetSubDir = Path.Combine(targetDir, dirName);
            _loggingService.LogInfo(Strings.DirectoryCopyRecursing, subDir, targetSubDir);

            await CopyDirectoryRecursiveAsync(subDir, targetSubDir, originalSourceRoot, excludePatterns);
        }
    }

    private async Task<string> CalculateFileHashAsync(string filePath)
    {
        _loggingService.LogInfo(Strings.HashCalculating, filePath);

        try
        {
            using var sha256 = SHA256.Create();
            using var stream = File.OpenRead(filePath);
            var hashBytes = await sha256.ComputeHashAsync(stream);
            var hash = Convert.ToHexString(hashBytes).ToLowerInvariant();

            _loggingService.LogInfo(Strings.HashCalculated, filePath, hash);
            return hash;
        }
        catch (Exception ex)
        {
            _loggingService.LogError(Strings.HashCalculationFailed, filePath, ex.Message);
            throw;
        }
    }
}