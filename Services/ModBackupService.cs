using GHPC_Mod_Manager.Models;
using GHPC_Mod_Manager.Resources;
using System.IO;

namespace GHPC_Mod_Manager.Services;

public interface IModBackupService
{
    Task<bool> InitializeBackupDirectoryAsync();
    Task<bool> DisableModWithBackupAsync(string modId, List<string> modFiles);
    Task<bool> EnableModFromBackupAsync(string modId);
    Task<bool> UninstallModWithBackupAsync(string modId, string version, List<string> modFiles);
    Task<bool> ReinstallModFromBackupAsync(string modId, string version);
    Task<bool> CheckModBackupExistsAsync(string modId, string version);
    Task<bool> DeleteModBackupAsync(string modId, string version);
    Task<long> CleanupModBackupsAsync();
    string GetBackupRootPath();
}

public class ModBackupService : IModBackupService
{
    private readonly ISettingsService _settingsService;
    private readonly ILoggingService _loggingService;
    
    private string _backupRootPath = string.Empty;
    private string _disabledPath = string.Empty;
    private string _uninstalledPath = string.Empty;

    public ModBackupService(ISettingsService settingsService, ILoggingService loggingService)
    {
        _settingsService = settingsService;
        _loggingService = loggingService;
    }

    public async Task<bool> InitializeBackupDirectoryAsync()
    {
        try
        {
            var gameRootPath = _settingsService.Settings.GameRootPath;
            if (string.IsNullOrEmpty(gameRootPath))
                return false;

            // Create GHPCMM directory
            var ghpcmmPath = Path.Combine(gameRootPath, "GHPCMM");
            Directory.CreateDirectory(ghpcmmPath);

            // Create workspace indicator file with i18n name
            var workspaceFileName = Strings.GHPCModManagerWorkingDirectory;
            var workspaceFilePath = Path.Combine(ghpcmmPath, workspaceFileName);
            if (!File.Exists(workspaceFilePath))
            {
                await File.WriteAllTextAsync(workspaceFilePath, "This directory is used by GHPC Mod Manager for backup and management operations.");
            }

            // Create modbackup directory structure
            _backupRootPath = Path.Combine(ghpcmmPath, "modbackup");
            Directory.CreateDirectory(_backupRootPath);

            _disabledPath = Path.Combine(_backupRootPath, "disabled");
            Directory.CreateDirectory(_disabledPath);

            _uninstalledPath = Path.Combine(_backupRootPath, "uninstalled");
            Directory.CreateDirectory(_uninstalledPath);

            _loggingService.LogInfo(Strings.ModBackupDirectoryCreated, _backupRootPath);
            return true;
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.ModBackupDirectoryCreateFailed, _settingsService.Settings.GameRootPath);
            return false;
        }
    }

    public async Task<bool> DisableModWithBackupAsync(string modId, List<string> modFiles)
    {
        try
        {
            if (!await InitializeBackupDirectoryAsync())
                return false;

            var gameRootPath = _settingsService.Settings.GameRootPath;
            var modBackupPath = Path.Combine(_disabledPath, modId);
            Directory.CreateDirectory(modBackupPath);
            
            // Create manifest to track original file paths
            var backupManifest = new Dictionary<string, string>();

            // If modFiles is empty, this indicates files couldn't be found in the upper layer
            // This should be rare since ToggleModAsync now handles file discovery for manual mods
            if (!modFiles.Any())
            {
                _loggingService.LogWarning(Strings.NoFilesProvidedForModBackup, modId);
            }

            // Move files to disabled backup, preserving directory structure
            foreach (var relativePath in modFiles)
            {
                var sourceFile = Path.Combine(gameRootPath, relativePath);
                if (File.Exists(sourceFile))
                {
                    // Preserve directory structure in backup by encoding paths
                    var backupFileName = relativePath.Replace('\\', '_').Replace('/', '_');
                    var backupFile = Path.Combine(modBackupPath, backupFileName);
                    
                    // Store mapping in manifest
                    backupManifest[backupFileName] = relativePath;
                    
                    File.Move(sourceFile, backupFile);
                }
            }

            // If no files were found/moved, this might indicate the mod is already disabled or not found
            if (!backupManifest.Any())
            {
                _loggingService.LogWarning(Strings.NoFilesFoundToDisableForMod, modId);
                return false;
            }
            
            // Save backup manifest
            var manifestPath = Path.Combine(modBackupPath, "backup_paths.json");
            var manifestJson = System.Text.Json.JsonSerializer.Serialize(backupManifest, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(manifestPath, manifestJson);

            _loggingService.LogInfo(Strings.ModDisabledAndBackedUp, modId);
            return true;
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.ModBackupFailed, modId);
            return false;
        }
    }

    public async Task<bool> EnableModFromBackupAsync(string modId)
    {
        try
        {
            if (!await InitializeBackupDirectoryAsync())
                return false;

            var gameRootPath = _settingsService.Settings.GameRootPath;
            var modBackupPath = Path.Combine(_disabledPath, modId);

            if (!Directory.Exists(modBackupPath))
            {
                _loggingService.LogWarning(Strings.ModBackupNotFound, modId);
                return false;
            }

            // Load backup manifest to get original file paths
            var manifestPath = Path.Combine(modBackupPath, "backup_paths.json");
            var backupManifest = new Dictionary<string, string>();
            
            if (File.Exists(manifestPath))
            {
                var manifestJson = await File.ReadAllTextAsync(manifestPath);
                backupManifest = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(manifestJson) ?? new Dictionary<string, string>();
            }
            
            // Get actual backup files (exclude json)
            var backupFiles = Directory.GetFiles(modBackupPath, "*", SearchOption.TopDirectoryOnly)
                .Where(f => Path.GetFileName(f) != "backup_paths.json")
                .ToList();
                
            // If no backup files exist, clean up and return false
            if (!backupFiles.Any())
            {
                _loggingService.LogWarning(Strings.NoBackupFilesFoundCleaningUp, modId);
                if (Directory.Exists(modBackupPath))
                {
                    Directory.Delete(modBackupPath, true);
                }
                return false;
            }
                
            foreach (var backupFile in backupFiles)
            {
                var backupFileName = Path.GetFileName(backupFile);
                string targetPath;
                
                // Try to get original path from manifest
                if (backupManifest.TryGetValue(backupFileName, out var originalPath))
                {
                    targetPath = Path.Combine(gameRootPath, originalPath);
                }
                else
                {
                    // Fallback: decode the encoded path (backward compatibility)
                    var decodedPath = backupFileName.Replace('_', Path.DirectorySeparatorChar);
                    targetPath = Path.Combine(gameRootPath, decodedPath);
                }
                
                // Ensure target directory exists
                var targetDir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                }

                File.Move(backupFile, targetPath);
                _loggingService.LogInfo(Strings.RestoredModFile, backupFile, targetPath);
            }

            // Clean up empty backup directory
            if (!Directory.EnumerateFileSystemEntries(modBackupPath).Any())
            {
                Directory.Delete(modBackupPath);
            }

            _loggingService.LogInfo(Strings.ModEnabledFromBackup, modId);
            return true;
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.ModRestoreFromBackupFailed, modId);
            return false;
        }
    }

    public async Task<bool> UninstallModWithBackupAsync(string modId, string version, List<string> modFiles)
    {
        try
        {
            if (!await InitializeBackupDirectoryAsync())
                return false;

            var gameRootPath = _settingsService.Settings.GameRootPath;
            var versionedBackupPath = Path.Combine(_uninstalledPath, $"{modId}_v{version}");
            Directory.CreateDirectory(versionedBackupPath);

            // Create backup manifest for this mod
            var backupManifest = new ModBackupManifest
            {
                ModId = modId,
                Version = version,
                BackupDate = DateTime.Now,
                OriginalFiles = modFiles
            };

            // Copy files to uninstalled backup, preserving directory structure
            var filePathMapping = new Dictionary<string, string>(); // backupFileName -> originalRelativePath
            
            foreach (var relativePath in modFiles)
            {
                var sourceFile = Path.Combine(gameRootPath, relativePath);
                if (File.Exists(sourceFile))
                {
                    // Preserve directory structure by encoding path separators
                    var encodedFileName = relativePath.Replace('\\', '_').Replace('/', '_');
                    var backupFile = Path.Combine(versionedBackupPath, encodedFileName);
                    
                    // Store the mapping for restoration
                    filePathMapping[encodedFileName] = relativePath;
                    
                    File.Copy(sourceFile, backupFile, true);
                }
            }
            
            // Update backup manifest to include file path mapping
            backupManifest.FilePathMapping = filePathMapping;

            // Save backup manifest
            var manifestPath = Path.Combine(versionedBackupPath, "backup_manifest.json");
            var manifestJson = System.Text.Json.JsonSerializer.Serialize(backupManifest, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(manifestPath, manifestJson);

            _loggingService.LogInfo(Strings.ModUninstalledAndBackedUp, modId, version);
            return true;
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.ModBackupFailed, modId);
            return false;
        }
    }

    public async Task<bool> ReinstallModFromBackupAsync(string modId, string version)
    {
        try
        {
            if (!await InitializeBackupDirectoryAsync())
                return false;

            var gameRootPath = _settingsService.Settings.GameRootPath;
            var modsPath = Path.Combine(gameRootPath, "Mods");
            var versionedBackupPath = Path.Combine(_uninstalledPath, $"{modId}_v{version}");

            if (!Directory.Exists(versionedBackupPath))
            {
                _loggingService.LogWarning(Strings.ModBackupNotFound, modId);
                return false;
            }

            // Load backup manifest
            var manifestPath = Path.Combine(versionedBackupPath, "backup_manifest.json");
            if (!File.Exists(manifestPath))
            {
                _loggingService.LogWarning(Strings.ModBackupNotFound, $"{modId} manifest");
                return false;
            }

            var manifestJson = await File.ReadAllTextAsync(manifestPath);
            var backupManifest = System.Text.Json.JsonSerializer.Deserialize<ModBackupManifest>(manifestJson);
            if (backupManifest == null)
            {
                return false;
            }

            Directory.CreateDirectory(modsPath);

            // Restore files from backup to their original locations
            var backupFiles = Directory.GetFiles(versionedBackupPath, "*", SearchOption.TopDirectoryOnly)
                .Where(f => Path.GetFileName(f) != "backup_manifest.json")
                .ToList();

            foreach (var backupFile in backupFiles)
            {
                var backupFileName = Path.GetFileName(backupFile);
                string targetPath;
                
                // Try to get original path from manifest file mapping
                if (backupManifest.FilePathMapping?.TryGetValue(backupFileName, out var originalRelativePath) == true)
                {
                    targetPath = Path.Combine(gameRootPath, originalRelativePath);
                }
                else
                {
                    // Fallback: try to decode from encoded filename
                    var decodedPath = backupFileName.Replace('_', Path.DirectorySeparatorChar);
                    targetPath = Path.Combine(gameRootPath, decodedPath);
                }
                
                // Ensure target directory exists
                var targetDir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                }

                File.Copy(backupFile, targetPath, true);
                _loggingService.LogInfo(Strings.RestoredModFile, backupFile, targetPath);
            }

            _loggingService.LogInfo(Strings.ModReinstalledFromBackup, modId, version);
            return true;
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.ModRestoreFromBackupFailed, modId);
            return false;
        }
    }

    public async Task<bool> CheckModBackupExistsAsync(string modId, string version)
    {
        if (!await InitializeBackupDirectoryAsync())
            return false;

        var versionedBackupPath = Path.Combine(_uninstalledPath, $"{modId}_v{version}");
        return Directory.Exists(versionedBackupPath) && 
               File.Exists(Path.Combine(versionedBackupPath, "backup_manifest.json"));
    }

    public async Task<long> CleanupModBackupsAsync()
    {
        long totalFreedBytes = 0;

        try
        {
            if (!await InitializeBackupDirectoryAsync())
                return 0;

            // 只清理 uninstalled 目录,保留 disabled 目录
            totalFreedBytes += await CleanupDirectoryAsync(_uninstalledPath);

            _loggingService.LogInfo(Strings.CleanupModBackupsSuccessful, (totalFreedBytes / (1024 * 1024)).ToString("F1"));
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.CleanupModBackupsFailed, ex.Message);
        }

        return totalFreedBytes;
    }

    public string GetBackupRootPath()
    {
        return _backupRootPath;
    }

    private async Task<long> CleanupDirectoryAsync(string directoryPath)
    {
        long freedBytes = 0;

        if (!Directory.Exists(directoryPath))
            return 0;

        try
        {
            var subdirectories = Directory.GetDirectories(directoryPath);
            foreach (var subdir in subdirectories)
            {
                freedBytes += await GetDirectorySizeAsync(subdir);
                Directory.Delete(subdir, true);
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.FailedToCleanupDirectory, directoryPath);
        }

        return freedBytes;
    }

    private async Task<long> GetDirectorySizeAsync(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
            return 0;

        return await Task.Run(() =>
        {
            try
            {
                return Directory.GetFiles(directoryPath, "*", SearchOption.AllDirectories)
                    .Sum(file => new System.IO.FileInfo(file).Length);
            }
            catch
            {
                return 0;
            }
        });
    }

    public async Task<bool> DeleteModBackupAsync(string modId, string version)
    {
        try
        {
            await InitializeBackupDirectoryAsync();
            
            var backupModPath = Path.Combine(_uninstalledPath, $"{modId}_v{version}");
            
            if (!Directory.Exists(backupModPath))
            {
                _loggingService.LogInfo(Strings.BackupDirectoryNotExists, backupModPath);
                return true; // Consider it successful if nothing to delete
            }

            Directory.Delete(backupModPath, true);
            _loggingService.LogInfo(Strings.DeletedModBackup, modId, version);
            
            return true;
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.FailedToDeleteModBackup, modId, version);
            return false;
        }
    }
}

// Backup manifest model
public class ModBackupManifest
{
    public string ModId { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public DateTime BackupDate { get; set; }
    public List<string> OriginalFiles { get; set; } = new();
    public Dictionary<string, string>? FilePathMapping { get; set; } = new(); // backupFileName -> originalRelativePath
}