using GHPC_Mod_Manager.Models;
using GHPC_Mod_Manager.Resources;
using System.IO;

namespace GHPC_Mod_Manager.Services;

public interface ITranslationBackupService
{
    Task<bool> IsTranslationInstalledAsync();
    Task<bool> IsTranslationEnabledAsync();
    Task<bool> DisableTranslationAsync();
    Task<bool> EnableTranslationAsync();
    Task<bool> InitializeTranslationBackupAsync();
}

public class TranslationBackupService : ITranslationBackupService
{
    private readonly ISettingsService _settingsService;
    private readonly ILoggingService _loggingService;
    
    private string _backupPath = string.Empty;

    public TranslationBackupService(ISettingsService settingsService, ILoggingService loggingService)
    {
        _settingsService = settingsService;
        _loggingService = loggingService;
    }

    public async Task<bool> InitializeTranslationBackupAsync()
    {
        try
        {
            var gameRootPath = _settingsService.Settings.GameRootPath;
            if (string.IsNullOrEmpty(gameRootPath))
                return false;

            // Create GHPCMM directory structure
            var ghpcmmPath = Path.Combine(gameRootPath, "GHPCMM");
            Directory.CreateDirectory(ghpcmmPath);

            _backupPath = Path.Combine(ghpcmmPath, "translation_backup");
            Directory.CreateDirectory(_backupPath);

            _loggingService.LogInfo(Strings.TranslationBackupDirectoryInitialized, _backupPath);
            return true;
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, "Failed to initialize translation backup directory");
            return false;
        }
    }

    public async Task<bool> IsTranslationInstalledAsync()
    {
        var gameRootPath = _settingsService.Settings.GameRootPath;
        if (string.IsNullOrEmpty(gameRootPath))
            return false;

        // Check if translation system is currently active
        var autoTranslatorPath = Path.Combine(gameRootPath, "Mods", "XUnity.AutoTranslator.Plugin.MelonMod.dll");
        if (File.Exists(autoTranslatorPath))
            return true;

        // Check if translation system is installed but disabled (backed up)
        await InitializeTranslationBackupAsync();
        var manifestPath = Path.Combine(_backupPath, "translation_backup_manifest.json");
        if (File.Exists(manifestPath))
            return true;

        // Check original installation manifest
        var installManifestPath = Path.Combine(gameRootPath, "app_data", "translation_install_manifest.json");
        return File.Exists(installManifestPath);
    }

    public async Task<bool> IsTranslationEnabledAsync()
    {
        var gameRootPath = _settingsService.Settings.GameRootPath;
        if (string.IsNullOrEmpty(gameRootPath))
            return false;

        // Translation is enabled if the main DLL file exists in the active location
        var autoTranslatorPath = Path.Combine(gameRootPath, "Mods", "XUnity.AutoTranslator.Plugin.MelonMod.dll");
        return File.Exists(autoTranslatorPath);
    }

    public async Task<bool> DisableTranslationAsync()
    {
        try
        {
            if (!await InitializeTranslationBackupAsync())
                return false;

            var gameRootPath = _settingsService.Settings.GameRootPath;
            
            // Get translation files from the existing translation install manifest
            var translationFiles = await GetInstalledTranslationFiles(gameRootPath);

            if (!translationFiles.Any())
            {
                _loggingService.LogWarning(Strings.NoTranslationFilesFoundToDisable);
                return false;
            }

            // Create backup manifest to track original file paths
            var backupManifest = new Dictionary<string, string>();

            // Move translation files to backup folder
            foreach (var relativePath in translationFiles)
            {
                var filePath = Path.Combine(gameRootPath, relativePath);
                if (File.Exists(filePath))
                {
                    var backupFileName = relativePath.Replace('\\', '_').Replace('/', '_');
                    var backupFilePath = Path.Combine(_backupPath, backupFileName);

                    // Store mapping in manifest
                    backupManifest[backupFileName] = relativePath;

                    // Ensure backup directory exists
                    var backupDir = Path.GetDirectoryName(backupFilePath);
                    if (!string.IsNullOrEmpty(backupDir))
                    {
                        Directory.CreateDirectory(backupDir);
                    }

                    File.Move(filePath, backupFilePath);
                    _loggingService.LogInfo(Strings.MovedTranslationFileToBackup, filePath, backupFilePath);
                }
            }

            // Save backup manifest
            var manifestPath = Path.Combine(_backupPath, "translation_backup_manifest.json");
            var manifestJson = System.Text.Json.JsonSerializer.Serialize(backupManifest, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(manifestPath, manifestJson);

            _loggingService.LogInfo(Strings.TranslationSystemDisabledAndBackedUp);
            return true;
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, "Failed to disable translation system");
            return false;
        }
    }

    public async Task<bool> EnableTranslationAsync()
    {
        try
        {
            if (!await InitializeTranslationBackupAsync())
                return false;

            var gameRootPath = _settingsService.Settings.GameRootPath;
            var manifestPath = Path.Combine(_backupPath, "translation_backup_manifest.json");

            // If no backup exists, check if translation is already enabled
            if (!File.Exists(manifestPath))
            {
                var autoTranslatorPath = Path.Combine(gameRootPath, "Mods", "XUnity.AutoTranslator.Plugin.MelonMod.dll");
                if (File.Exists(autoTranslatorPath))
                {
                    _loggingService.LogInfo(Strings.TranslationSystemAlreadyEnabled);
                    return true;
                }
                else
                {
                    _loggingService.LogWarning(Strings.NoTranslationBackupFoundPleaseInstall);
                    return false;
                }
            }

            // Load backup manifest
            var manifestJson = await File.ReadAllTextAsync(manifestPath);
            var backupManifest = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(manifestJson);

            if (backupManifest == null || !backupManifest.Any())
            {
                _loggingService.LogWarning(Strings.TranslationBackupManifestEmpty);
                return false;
            }

            // Restore files from backup
            foreach (var (backupFileName, originalRelativePath) in backupManifest)
            {
                var backupFilePath = Path.Combine(_backupPath, backupFileName);
                var originalFilePath = Path.Combine(gameRootPath, originalRelativePath);

                if (File.Exists(backupFilePath))
                {
                    // Ensure target directory exists
                    var targetDir = Path.GetDirectoryName(originalFilePath);
                    if (!string.IsNullOrEmpty(targetDir))
                    {
                        Directory.CreateDirectory(targetDir);
                    }

                    File.Move(backupFilePath, originalFilePath);
                    _loggingService.LogInfo(Strings.RestoredTranslationFile, backupFilePath, originalFilePath);
                }
            }

            // Clean up backup manifest after successful restore
            File.Delete(manifestPath);

            // Clean up backup folder if empty
            if (Directory.Exists(_backupPath) && !Directory.EnumerateFileSystemEntries(_backupPath).Any())
            {
                Directory.Delete(_backupPath);
            }

            _loggingService.LogInfo(Strings.TranslationSystemEnabledFromBackup);
            return true;
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, "Failed to enable translation system");
            return false;
        }
    }

    private async Task<List<string>> GetInstalledTranslationFiles(string gameRootPath)
    {
        var translationFiles = new List<string>();

        // Try to get files from translation install manifest
        var manifestPath = Path.Combine(gameRootPath, "app_data", "translation_install_manifest.json");
        if (File.Exists(manifestPath))
        {
            try
            {
                var manifestJson = await File.ReadAllTextAsync(manifestPath);
                var manifest = System.Text.Json.JsonSerializer.Deserialize<TranslationInstallManifest>(manifestJson);
                
                if (manifest != null)
                {
                    translationFiles.AddRange(manifest.XUnityAutoTranslatorFiles);
                    translationFiles.AddRange(manifest.TranslationRepoFiles);
                    return translationFiles;
                }
            }
            catch (Exception ex)
            {
                _loggingService.LogWarning(Strings.FailedToReadTranslationInstallManifest, ex.Message);
            }
        }

        // Fallback: manually detect translation files
        _loggingService.LogInfo(Strings.UsingFallbackMethodToDetectTranslationFiles);
        
        // Add XUnity AutoTranslator plugin file
        var autoTranslatorFile = Path.Combine("Mods", "XUnity.AutoTranslator.Plugin.MelonMod.dll");
        if (File.Exists(Path.Combine(gameRootPath, autoTranslatorFile)))
        {
            translationFiles.Add(autoTranslatorFile);
        }

        // Add AutoTranslator folder contents
        var autoTranslatorFolderPath = Path.Combine(gameRootPath, "AutoTranslator");
        if (Directory.Exists(autoTranslatorFolderPath))
        {
            var folderFiles = Directory.GetFiles(autoTranslatorFolderPath, "*", SearchOption.AllDirectories);
            translationFiles.AddRange(folderFiles.Select(f => Path.GetRelativePath(gameRootPath, f)));
        }

        return translationFiles;
    }
}