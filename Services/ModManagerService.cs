using GHPC_Mod_Manager.Models;
using GHPC_Mod_Manager.ViewModels;
using Newtonsoft.Json;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using GHPC_Mod_Manager.Resources;

namespace GHPC_Mod_Manager.Services;

public interface IModManagerService
{
    Task<List<ModViewModel>> GetModListAsync(bool forceRefresh = false);
    Task<bool> InstallModAsync(ModConfig modConfig, string version, IProgress<DownloadProgress>? progress = null);
    Task<bool> UpdateModAsync(string modId, string newVersion, IProgress<DownloadProgress>? progress = null);
    Task<bool> UninstallModAsync(string modId);
    Task<bool> UninstallManualModAsync(string modId);
    Task<bool> EnableModAsync(string modId);
    Task<bool> DisableModAsync(string modId);
    Task<Dictionary<string, object>> GetModConfigurationAsync(string modId);
    Task<Dictionary<string, string>> GetModConfigurationCommentsAsync(string modId);
    Task<List<string>> GetModConfigurationStandaloneCommentsAsync(string modId);
    Task<List<ConfigurationItemViewModel>> GetModConfigurationOrderedAsync(string modId);
    Task<bool> SaveModConfigurationAsync(string modId, Dictionary<string, object> configuration);
    Task RefreshModListAsync();
    string GetLocalizedConfigLabel(string modId, string configKey);
    string GetLocalizedConfigComment(string modId, string commentKey);
}

public class ModManagerService : IModManagerService
{
    private readonly ISettingsService _settingsService;
    private readonly INetworkService _networkService;
    private readonly ILoggingService _loggingService;
    private readonly IModI18nService _modI18nService;
    private readonly IModBackupService _modBackupService;
    private List<ModConfig> _availableMods = new();
    private ModInstallManifest _installManifest = new();
    private Dictionary<string, Dictionary<string, string>> _configComments = new(); // Store comments for each mod
    private Dictionary<string, List<string>> _standaloneComments = new(); // Store standalone comments for each mod

    public ModManagerService(ISettingsService settingsService, INetworkService networkService, ILoggingService loggingService, IModI18nService modI18nService, IModBackupService modBackupService)
    {
        _settingsService = settingsService;
        _networkService = networkService;
        _loggingService = loggingService;
        _modI18nService = modI18nService;
        _modBackupService = modBackupService;
    }

    public async Task<List<ModViewModel>> GetModListAsync(bool forceRefresh = false)
    {
        await LoadManifestAsync();
        await LoadAvailableModsAsync();

        var result = new List<ModViewModel>();
        var gameRootPath = _settingsService.Settings.GameRootPath;
        var modsPath = Path.Combine(gameRootPath, "Mods");

        foreach (var modConfig in _availableMods)
        {
            var viewModel = new ModViewModel
            {
                Id = modConfig.Id,
                DisplayName = GetLocalizedName(modConfig),
                Config = modConfig
            };

            var installInfo = _installManifest.InstalledMods.GetValueOrDefault(modConfig.Id);
            if (installInfo != null)
            {
                viewModel.IsInstalled = true;
                viewModel.InstalledVersion = installInfo.Version;
                viewModel.IsEnabled = IsModEnabled(modConfig.MainBinaryFileName, modsPath);
            }
            else
            {
                // Check if mod exists but not in manifest (manually installed)
                if (IsModInstalled(modConfig.MainBinaryFileName, modsPath))
                {
                    viewModel.IsInstalled = true;
                    viewModel.IsManuallyInstalled = true;
                    viewModel.IsSupportedManualMod = true;  // This was missing!
                    viewModel.InstalledVersion = GHPC_Mod_Manager.Resources.Strings.Manual;
                    viewModel.IsEnabled = IsModEnabled(modConfig.MainBinaryFileName, modsPath);
                }
            }

            try
            {
                var releases = await _networkService.GetGitHubReleasesAsync(
                    GetRepoOwner(modConfig.ReleaseUrl),
                    GetRepoName(modConfig.ReleaseUrl),
                    forceRefresh
                );
                viewModel.LatestVersion = releases.FirstOrDefault()?.TagName ?? GHPC_Mod_Manager.Resources.Strings.Unknown;
            }
            catch
            {
                viewModel.LatestVersion = GHPC_Mod_Manager.Resources.Strings.Unknown;
            }

            result.Add(viewModel);
        }

        await ScanManualModsAsync(result, modsPath);

        return result;
    }


    private string GetLanguageSpecificName(string chineseName, string englishName)
    {
        var language = _settingsService.Settings.Language;
        return language == "zh-CN" ? chineseName : englishName;
    }

    private async Task ScanManualModsAsync(List<ModViewModel> modList, string modsPath)
    {
        if (!Directory.Exists(modsPath)) return;

        try
        {
            var gameRootPath = _settingsService.Settings.GameRootPath;
            var ghpcmmPath = Path.Combine(gameRootPath, "GHPCMM");
            var disabledPath = Path.Combine(ghpcmmPath, "modbackup", "disabled");
            
            // Scan active mods in Mods folder
            var dllFiles = Directory.GetFiles(modsPath, "*.dll", SearchOption.AllDirectories);
            
            // Use only active mod files for scanning (no .bak files)
            var allModFiles = new List<string>();
            allModFiles.AddRange(dllFiles);
            
            var uniqueModFiles = allModFiles.Distinct().ToList();
            
            // Also scan disabled mods in GHPCMM backup folder
            var disabledModFiles = new List<string>();
            if (Directory.Exists(disabledPath))
            {
                var disabledModDirs = Directory.GetDirectories(disabledPath);
                foreach (var disabledModDir in disabledModDirs)
                {
                    var manifestPath = Path.Combine(disabledModDir, "backup_paths.json");
                    if (File.Exists(manifestPath))
                    {
                        try
                        {
                            var manifestJson = await File.ReadAllTextAsync(manifestPath);
                            var backupManifest = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(manifestJson);
                            if (backupManifest != null)
                            {
                                // Add the original paths of disabled mods
                                disabledModFiles.AddRange(backupManifest.Values.Select(relativePath => 
                                    Path.Combine(gameRootPath, relativePath)));
                            }
                        }
                        catch
                        {
                            // Fallback: decode from directory name
                            var modId = Path.GetFileName(disabledModDir);
                            var backupFiles = Directory.GetFiles(disabledModDir, "*.dll", SearchOption.TopDirectoryOnly);
                            foreach (var backupFile in backupFiles)
                            {
                                var fileName = Path.GetFileName(backupFile);
                                var decodedPath = fileName.Replace('_', Path.DirectorySeparatorChar);
                                disabledModFiles.Add(Path.Combine(gameRootPath, decodedPath));
                            }
                        }
                    }
                }
            }
            
            // Combine active and disabled mods for scanning
            var allFoundMods = uniqueModFiles.Concat(disabledModFiles).Distinct().ToList();
            
            foreach (var modFile in allFoundMods)
            {
                var fileName = Path.GetFileName(modFile);
                
                // Skip if already in available mods list
                if (modList.Any(m => m.Config.MainBinaryFileName == fileName))
                    continue;

                // Skip if already in install manifest
                if (_installManifest.InstalledMods.Values.Any(m => m.InstalledFiles.Contains(Path.GetRelativePath(_settingsService.Settings.GameRootPath, modFile))))
                    continue;

                // Skip XUnity AutoTranslator (translation plugin) and its backup
                if (fileName == "XUnity.AutoTranslator.Plugin.MelonMod.dll" || fileName == "XUnity.AutoTranslator.Plugin.MelonMod.dllbak")
                    continue;

                // Check if this manual mod matches any supported mod configuration
                var matchingConfig = _availableMods.FirstOrDefault(m => m.MainBinaryFileName == fileName);
                
                // Determine if mod is currently enabled (exists in Mods folder)
                var isEnabled = uniqueModFiles.Contains(modFile);
                
                if (matchingConfig != null)
                {
                    // This is a supported manual mod - create with full config
                    var supportedManualMod = new ModViewModel
                    {
                        Id = matchingConfig.Id,
                        DisplayName = GetLocalizedName(matchingConfig),
                        Config = matchingConfig,
                        IsInstalled = true,
                        IsManuallyInstalled = true,
                        IsSupportedManualMod = true,
                        IsUnsupportedManualMod = false,
                        IsEnabled = isEnabled,
                        InstalledVersion = GHPC_Mod_Manager.Resources.Strings.Manual,
                        LatestVersion = GHPC_Mod_Manager.Resources.Strings.Unknown
                    };

                    // Try to get latest version from GitHub
                    try
                    {
                        var releases = await _networkService.GetGitHubReleasesAsync(
                            GetRepoOwner(matchingConfig.ReleaseUrl),
                            GetRepoName(matchingConfig.ReleaseUrl)
                        );
                        supportedManualMod.LatestVersion = releases.FirstOrDefault()?.TagName ?? GHPC_Mod_Manager.Resources.Strings.Unknown;
                    }
                    catch
                    {
                        supportedManualMod.LatestVersion = GHPC_Mod_Manager.Resources.Strings.Unknown;
                    }
                    
                    modList.Add(supportedManualMod);
                }
                else
                {
                    // This is an unsupported manual mod - create basic entry
                    modList.Add(new ModViewModel
                    {
                        Id = $"manual_{Path.GetFileNameWithoutExtension(fileName)}",
                        DisplayName = Path.GetFileNameWithoutExtension(fileName),
                        IsInstalled = true,
                        IsManuallyInstalled = true,
                        IsSupportedManualMod = false,
                        IsUnsupportedManualMod = true,
                        IsEnabled = isEnabled,
                        InstalledVersion = GHPC_Mod_Manager.Resources.Strings.Manual,
                        LatestVersion = GHPC_Mod_Manager.Resources.Strings.Unknown
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.ManualModScanError);
        }
    }

    public async Task<bool> InstallModAsync(ModConfig modConfig, string version, IProgress<DownloadProgress>? progress = null)
    {
        try
        {
            _loggingService.LogInfo(Strings.Installing, modConfig.Id, version);

            // Check if we can reinstall from backup first (quick install)
            if (await _modBackupService.CheckModBackupExistsAsync(modConfig.Id, version))
            {
                _loggingService.LogInfo(Strings.AttemptingQuickReinstallFromBackup, modConfig.Id, version);
                
                var quickInstallSuccess = await _modBackupService.ReinstallModFromBackupAsync(modConfig.Id, version);
                if (quickInstallSuccess)
                {
                    // Update install manifest for quick reinstall
                    await LoadManifestAsync();
                    var quickGameRootPath = _settingsService.Settings.GameRootPath;
                    var quickModsPath = Path.Combine(quickGameRootPath, "Mods");
                    var quickNewFiles = Directory.GetFiles(quickModsPath, "*.dll", SearchOption.AllDirectories)
                        .Where(f => Path.GetFileName(f).Contains(modConfig.MainBinaryFileName.Replace(".dll", "")))
                        .Select(f => Path.GetRelativePath(quickGameRootPath, f))
                        .ToList();

                    var quickInstallInfo = new ModInstallInfo
                    {
                        ModId = modConfig.Id,
                        Version = version,
                        InstalledFiles = quickNewFiles,
                        InstallDate = DateTime.Now
                    };

                    _installManifest.InstalledMods[modConfig.Id] = quickInstallInfo;
                    await SaveManifestAsync();

                    _loggingService.LogInfo(Strings.ModReinstalledFromBackup, modConfig.Id, version);
                    return true;
                }
            }

            // Standard installation process continues if backup installation fails or doesn't exist
            // Check for missing dependencies first
            var (hasMissingRequirements, missingMods) = await CheckModDependenciesAsync(modConfig);
            if (hasMissingRequirements)
            {
                var missingModNames = missingMods.Select(GetModDisplayName).ToList();
                var currentModName = GetModDisplayName(modConfig.Id);
                var message = string.Format(Strings.ModDependencyMessage, currentModName, string.Join(", ", missingModNames));
                
                MessageBox.Show(message, Strings.ModDependencyMissing, MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            // Check for conflicts
            var (hasConflicts, conflictingMods) = await CheckModConflictsAsync(modConfig);
            if (hasConflicts)
            {
                var conflictingModNames = conflictingMods.Select(GetModDisplayName).ToList();
                var currentModName = GetModDisplayName(modConfig.Id);
                var message = string.Format(Strings.ModConflictMessage, currentModName, string.Join(", ", conflictingModNames));
                
                var dialogResult = MessageBox.Show(message, Strings.ModConflictDetected, MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (dialogResult == MessageBoxResult.No)
                {
                    return false;
                }
            }

            var releases = await _networkService.GetGitHubReleasesAsync(
                GetRepoOwner(modConfig.ReleaseUrl),
                GetRepoName(modConfig.ReleaseUrl)
            );

            var targetRelease = releases.FirstOrDefault(r => r.TagName == version);
            if (targetRelease == null)
            {
                _loggingService.LogError(Strings.ModVersionNotFound, modConfig.Id, version);
                return false;
            }

            var asset = targetRelease.Assets.FirstOrDefault(a => a.Name.Contains(modConfig.TargetFileNameKeyword));
            if (asset == null)
            {
                _loggingService.LogError(Strings.ModAssetNotFound, modConfig.Id, modConfig.TargetFileNameKeyword);
                return false;
            }

            var gameRootPath = _settingsService.Settings.GameRootPath;
            var modsPath = Path.Combine(gameRootPath, "Mods");
            Directory.CreateDirectory(modsPath);

            // 创建文件操作追踪器
            var tracker = new FileOperationTracker(_loggingService, _settingsService);
            var trackedOps = new TrackedFileOperations(tracker, _loggingService);

            // 开始追踪文件操作
            tracker.StartTracking($"mod_install_{modConfig.Id}_{version}", modsPath);

            var downloadData = await _networkService.DownloadFileAsync(asset.DownloadUrl, progress);

            if (modConfig.InstallMethod == InstallMethod.Scripted && !string.IsNullOrEmpty(modConfig.InstallScript_Base64))
            {
                var confirmed = await ShowScriptWarningAsync();
                if (!confirmed) return false;

                var tempDownloadPath = await SaveTempDownloadFileAsync(downloadData, asset.Name);
                
                await ExecuteInstallScriptWithTrackingAsync(modConfig.InstallScript_Base64, tempDownloadPath, gameRootPath, modConfig.MainBinaryFileName, tracker, progress);
            }
            else if (asset.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                // 单个DLL文件直接复制
                var tempDownloadPath = await SaveTempDownloadFileAsync(downloadData, asset.Name);
                var targetPath = Path.Combine(modsPath, asset.Name);
                await trackedOps.CopyFileAsync(tempDownloadPath, targetPath);
                
                // 清理临时文件
                File.Delete(tempDownloadPath);
            }
            else
            {
                await trackedOps.ExtractZipAsync(downloadData, modsPath);
            }

            // 停止追踪并获取结果
            tracker.StopTracking();
            var result = tracker.GetResult();
            var processedFiles = result.GetAllProcessedFiles();

            if (!processedFiles.Any())
            {
                _loggingService.LogError(Strings.NoFilesProcessedDuringInstallation, modConfig.Id);
                return false;
            }

            var installInfo = new ModInstallInfo
            {
                ModId = modConfig.Id,
                Version = version,
                InstalledFiles = processedFiles, // 使用追踪器记录的实际处理文件
                InstallDate = DateTime.Now
            };

            _installManifest.InstalledMods[modConfig.Id] = installInfo;
            await SaveManifestAsync();

            _loggingService.LogInfo(Strings.ModInstalled, modConfig.Id, version);
            return true;
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.ModInstallError, modConfig.Id, version);
            return false;
        }
    }

    public async Task<bool> UpdateModAsync(string modId, string newVersion, IProgress<DownloadProgress>? progress = null)
    {
        try
        {
            _loggingService.LogInfo(Strings.UpdatingMod, modId, "current", newVersion);

            // Get the mod configuration
            var modConfig = _availableMods.FirstOrDefault(m => m.Id == modId);
            if (modConfig == null)
            {
                _loggingService.LogError(Strings.ModConfigNotFoundForUpdate, modId);
                return false;
            }

            // Check if mod is currently installed
            if (!_installManifest.InstalledMods.TryGetValue(modId, out var currentInstallInfo))
            {
                _loggingService.LogError(Strings.ModNotInstalledCannotUpdate, modId);
                return false;
            }

            // Step 1: Download new version first
            var releases = await _networkService.GetGitHubReleasesAsync(
                GetRepoOwner(modConfig.ReleaseUrl),
                GetRepoName(modConfig.ReleaseUrl)
            );

            var targetRelease = releases.FirstOrDefault(r => r.TagName == newVersion);
            if (targetRelease == null)
            {
                _loggingService.LogError(Strings.ModVersionNotFound, modId, newVersion);
                return false;
            }

            var asset = targetRelease.Assets.FirstOrDefault(a => a.Name.Contains(modConfig.TargetFileNameKeyword));
            if (asset == null)
            {
                _loggingService.LogError(Strings.ModAssetNotFound, modId, modConfig.TargetFileNameKeyword);
                return false;
            }

            // Download the new version
            var downloadData = await _networkService.DownloadFileAsync(asset.DownloadUrl, progress);

            // Step 2: Uninstall current version (move to uninstalled backup)
            await _modBackupService.UninstallModWithBackupAsync(modId, currentInstallInfo.Version, currentInstallInfo.InstalledFiles);

            var gameRootPath = _settingsService.Settings.GameRootPath;

            // Delete current files
            foreach (var relativePath in currentInstallInfo.InstalledFiles)
            {
                var fullPath = Path.Combine(gameRootPath, relativePath);
                if (File.Exists(fullPath))
                {
                    File.Delete(fullPath);
                }
            }

            // Clean up empty directories
            var directories = currentInstallInfo.InstalledFiles
                .Select(f => Path.GetDirectoryName(Path.Combine(gameRootPath, f)))
                .Where(d => !string.IsNullOrEmpty(d))
                .Distinct()
                .OrderByDescending(d => d?.Length ?? 0);

            foreach (var directory in directories)
            {
                if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory);
                }
            }

            // Step 3: Install new version using tracking system
            var modsPath = Path.Combine(gameRootPath, "Mods");
            Directory.CreateDirectory(modsPath);

            // Create file operation tracker for update
            var tracker = new FileOperationTracker(_loggingService, _settingsService);
            var trackedOps = new TrackedFileOperations(tracker, _loggingService);

            // Start tracking file operations
            tracker.StartTracking($"mod_update_{modId}_{newVersion}", modsPath);

            if (modConfig.InstallMethod == InstallMethod.Scripted && !string.IsNullOrEmpty(modConfig.InstallScript_Base64))
            {
                var confirmed = await ShowScriptWarningAsync();
                if (!confirmed) 
                {
                    // Restore from backup if user cancels
                    await _modBackupService.ReinstallModFromBackupAsync(modId, currentInstallInfo.Version);
                    return false;
                }

                var tempDownloadPath = await SaveTempDownloadFileAsync(downloadData, asset.Name);
                await ExecuteInstallScriptWithTrackingAsync(modConfig.InstallScript_Base64, tempDownloadPath, gameRootPath, modConfig.MainBinaryFileName, tracker, progress);
            }
            else if (asset.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                // Single DLL file direct copy
                var tempDownloadPath = await SaveTempDownloadFileAsync(downloadData, asset.Name);
                var targetPath = Path.Combine(modsPath, asset.Name);
                await trackedOps.CopyFileAsync(tempDownloadPath, targetPath);
                
                // Clean up temp file
                File.Delete(tempDownloadPath);
            }
            else
            {
                await trackedOps.ExtractZipAsync(downloadData, modsPath);
            }

            // Stop tracking and get results
            tracker.StopTracking();
            var trackingResult = tracker.GetResult();
            var processedFiles = trackingResult.GetAllProcessedFiles();

            // Step 4: Update install manifest with new version
            var newInstallInfo = new ModInstallInfo
            {
                ModId = modId,
                Version = newVersion,
                InstalledFiles = processedFiles, // Use tracked processed files
                InstallDate = DateTime.Now
            };

            _installManifest.InstalledMods[modId] = newInstallInfo;
            await SaveManifestAsync();

            // Step 5: Delete old backup now that new installation is successful
            await _modBackupService.DeleteModBackupAsync(modId, currentInstallInfo.Version);

            _loggingService.LogInfo(Strings.ModUpdatedSuccessfully, modId);
            return true;
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.ModUpdateFailed, modId);
            
            // Try to restore from backup if update failed
            try
            {
                await LoadManifestAsync();
                if (_installManifest.InstalledMods.TryGetValue(modId, out var backupInfo))
                {
                    await _modBackupService.ReinstallModFromBackupAsync(modId, backupInfo.Version);
                    _loggingService.LogInfo(Strings.RestoredModFromBackupAfterFailedUpdate, modId);
                }
            }
            catch (Exception restoreEx)
            {
                _loggingService.LogError(restoreEx, "Failed to restore mod from backup after failed update: {0}", modId);
            }
            
            return false;
        }
    }

    public async Task<bool> UninstallModAsync(string modId)
    {
        try
        {
            if (!_installManifest.InstalledMods.TryGetValue(modId, out var installInfo))
            {
                _loggingService.LogError(Strings.ModNotInstalled, modId);
                return false;
            }

            _loggingService.LogInfo(Strings.UninstallingMod, modId);

            var gameRootPath = _settingsService.Settings.GameRootPath;
            var disabledBackupPath = Path.Combine(gameRootPath, "GHPCMM", "modbackup", "disabled", modId);
            var uninstalledBackupPath = Path.Combine(gameRootPath, "GHPCMM", "modbackup", "uninstalled", $"{modId}_v{installInfo.Version}");

            // Check if mod is currently disabled (has backup in disabled folder)
            if (Directory.Exists(disabledBackupPath))
            {
                _loggingService.LogInfo(Strings.ModDisabledMovingBackupToUninstalled, modId);
                
                // Create uninstalled backup directory
                Directory.CreateDirectory(Path.GetDirectoryName(uninstalledBackupPath)!);
                
                // Move disabled backup to uninstalled backup
                if (Directory.Exists(uninstalledBackupPath))
                {
                    Directory.Delete(uninstalledBackupPath, true);
                }
                Directory.Move(disabledBackupPath, uninstalledBackupPath);
                
                // Create proper uninstalled backup manifest
                var backupManifest = new
                {
                    ModId = modId,
                    Version = installInfo.Version,
                    BackupDate = DateTime.Now,
                    OriginalFiles = installInfo.InstalledFiles
                };
                
                var manifestPath = Path.Combine(uninstalledBackupPath, "backup_manifest.json");
                var manifestJson = System.Text.Json.JsonSerializer.Serialize(backupManifest, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(manifestPath, manifestJson);
                
                _loggingService.LogInfo(Strings.MovedDisabledBackupToUninstalled, modId);
            }
            else
            {
                // Mod is currently enabled, create backup before uninstalling
                await _modBackupService.UninstallModWithBackupAsync(modId, installInfo.Version, installInfo.InstalledFiles);

                // Delete active files after backup
                foreach (var relativePath in installInfo.InstalledFiles)
                {
                    var fullPath = Path.Combine(gameRootPath, relativePath);
                    if (File.Exists(fullPath))
                    {
                        File.Delete(fullPath);
                    }
                }

                // Clean up empty directories
                var directories = installInfo.InstalledFiles
                    .Select(f => Path.GetDirectoryName(Path.Combine(gameRootPath, f)))
                    .Where(d => !string.IsNullOrEmpty(d))
                    .Distinct()
                    .OrderByDescending(d => d?.Length ?? 0);

                foreach (var directory in directories)
                {
                    if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                    {
                        Directory.Delete(directory);
                    }
                }
            }

            _installManifest.InstalledMods.Remove(modId);
            await SaveManifestAsync();

            _loggingService.LogInfo(Strings.ModUninstalled, modId);
            return true;
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.ModUninstallError, modId);
            return false;
        }
    }

    public async Task<bool> UninstallManualModAsync(string modId)
    {
        try
        {
            _loggingService.LogInfo(Strings.UninstallingMod, modId);

            var gameRootPath = _settingsService.Settings.GameRootPath;
            var modsPath = Path.Combine(gameRootPath, "Mods");
            var disabledBackupPath = Path.Combine(gameRootPath, "GHPCMM", "modbackup", "disabled", modId);
            var uninstalledBackupPath = Path.Combine(gameRootPath, "GHPCMM", "modbackup", "uninstalled", $"{modId}_manual");
            
            // Check if mod is currently disabled (has backup in disabled folder)
            if (Directory.Exists(disabledBackupPath))
            {
                _loggingService.LogInfo(Strings.ManualModDisabledMovingBackup, modId);
                
                // Create uninstalled backup directory
                Directory.CreateDirectory(Path.GetDirectoryName(uninstalledBackupPath)!);
                
                // Move disabled backup to uninstalled backup
                if (Directory.Exists(uninstalledBackupPath))
                {
                    Directory.Delete(uninstalledBackupPath, true);
                }
                Directory.Move(disabledBackupPath, uninstalledBackupPath);
                
                // Create proper uninstalled backup manifest for manual mod
                var backupManifest = new
                {
                    ModId = modId,
                    Version = "manual",
                    BackupDate = DateTime.Now,
                    IsManualMod = true
                };
                
                var manifestPath = Path.Combine(uninstalledBackupPath, "backup_manifest.json");
                var manifestJson = System.Text.Json.JsonSerializer.Serialize(backupManifest, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(manifestPath, manifestJson);
                
                _loggingService.LogInfo(Strings.MovedDisabledManualModBackup, modId);
            }
            else
            {
                // Mod is currently enabled, need to find and delete active files
                List<string> filesToDelete = new();
                
                // Handle different types of manual mods
                if (modId.StartsWith("manual_"))
                {
                    // Unsupported manual mod
                    var fileName = modId.Substring("manual_".Length) + ".dll";
                    
                    // Check for files in subdirectories
                    var foundFiles = Directory.GetFiles(modsPath, fileName, SearchOption.AllDirectories);
                    
                    filesToDelete.AddRange(foundFiles);
                }
                else
                {
                    // Supported manual mod - use MainBinaryFileName from config
                    var matchingConfig = _availableMods.FirstOrDefault(m => m.Id == modId);
                    if (matchingConfig != null)
                    {
                        var fileName = matchingConfig.MainBinaryFileName;
                        var foundFiles = Directory.GetFiles(modsPath, fileName, SearchOption.AllDirectories);
                        
                        filesToDelete.AddRange(foundFiles);
                    }
                }

                if (!filesToDelete.Any())
                {
                    _loggingService.LogError(Strings.ModNotFound, modId);
                    return false;
                }

                // Create backup before deletion for manual mods
                if (filesToDelete.Any())
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(uninstalledBackupPath)!);
                    Directory.CreateDirectory(uninstalledBackupPath);
                    
                    // Create backup manifest
                    var backupManifest = new Dictionary<string, string>();
                    
                    // Copy files to backup before deletion
                    foreach (var filePath in filesToDelete)
                    {
                        if (File.Exists(filePath))
                        {
                            var relativePath = Path.GetRelativePath(gameRootPath, filePath);
                            var backupFileName = relativePath.Replace('\\', '_').Replace('/', '_');
                            var backupFilePath = Path.Combine(uninstalledBackupPath, backupFileName);
                            
                            File.Copy(filePath, backupFilePath, true);
                            backupManifest[backupFileName] = relativePath;
                        }
                    }
                    
                    // Save backup manifest
                    var manifestPath = Path.Combine(uninstalledBackupPath, "backup_paths.json");
                    var manifestJson = System.Text.Json.JsonSerializer.Serialize(backupManifest, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                    await File.WriteAllTextAsync(manifestPath, manifestJson);
                    
                    // Also save general backup manifest
                    var generalManifest = new
                    {
                        ModId = modId,
                        Version = "manual",
                        BackupDate = DateTime.Now,
                        IsManualMod = true
                    };
                    
                    var generalManifestPath = Path.Combine(uninstalledBackupPath, "backup_manifest.json");
                    var generalManifestJson = System.Text.Json.JsonSerializer.Serialize(generalManifest, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                    await File.WriteAllTextAsync(generalManifestPath, generalManifestJson);
                }

                // Delete all found files after backup
                var deletedDirectories = new HashSet<string>();
                foreach (var filePath in filesToDelete)
                {
                    if (File.Exists(filePath))
                    {
                        File.Delete(filePath);
                        _loggingService.LogInfo(GHPC_Mod_Manager.Resources.Strings.ManualModDeleted, filePath);
                        
                        // Track directory for cleanup
                        var directory = Path.GetDirectoryName(filePath);
                        if (!string.IsNullOrEmpty(directory) && directory != modsPath)
                        {
                            deletedDirectories.Add(directory);
                        }
                    }
                }

                // Clean up empty directories
                foreach (var directory in deletedDirectories.OrderByDescending(d => d.Length))
                {
                    try
                    {
                        if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                        {
                            Directory.Delete(directory);
                            _loggingService.LogInfo(Strings.RemovedEmptyDirectory, directory);
                        }
                    }
                    catch (Exception ex)
                    {
                        _loggingService.LogWarning(Strings.CouldNotRemoveDirectory, directory, ex.Message);
                    }
                }
            }

            _loggingService.LogInfo(Strings.ModUninstalled, modId);
            return true;
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.ModUninstallError, modId);
            return false;
        }
    }

    public async Task<bool> EnableModAsync(string modId)
    {
        return await ToggleModAsync(modId, true);
    }

    public async Task<bool> DisableModAsync(string modId)
    {
        return await ToggleModAsync(modId, false);
    }

    private async Task<bool> ToggleModAsync(string modId, bool enable)
    {
        try
        {
            await LoadManifestAsync();

            // Get mod files from install manifest
            List<string> modFiles = new();

            // Check if this is a manual mod (either unsupported "manual_" or supported but manually installed)
            bool isManualMod = modId.StartsWith("manual_") || 
                              (!_installManifest.InstalledMods.ContainsKey(modId) && 
                               _availableMods.Any(m => m.Id == modId));
            
            // For managed mods, get files from install manifest
            if (_installManifest.InstalledMods.TryGetValue(modId, out var installInfo))
            {
                modFiles = installInfo.InstalledFiles.ToList();
            }
            // For manual mods, try to find the actual files based on mod type
            else if (isManualMod)
            {
                _loggingService.LogInfo(Strings.ProcessingManualModToggle, modId);
                
                var gameRootPath = _settingsService.Settings.GameRootPath;
                var modsPath = Path.Combine(gameRootPath, "Mods");
                
                if (modId.StartsWith("manual_"))
                {
                    // Unsupported manual mod: manual_231 -> 231.dll
                    var fileName = modId.Substring("manual_".Length) + ".dll";
                    var foundFiles = Directory.GetFiles(modsPath, fileName, SearchOption.AllDirectories);
                    
                    foreach (var file in foundFiles)
                    {
                        var relativePath = Path.GetRelativePath(gameRootPath, file);
                        modFiles.Add(relativePath);
                    }
                }
                else
                {
                    // Supported manual mod - use MainBinaryFileName from config
                    var matchingConfig = _availableMods.FirstOrDefault(m => m.Id == modId);
                    if (matchingConfig != null)
                    {
                        var fileName = matchingConfig.MainBinaryFileName;
                        var foundFiles = Directory.GetFiles(modsPath, fileName, SearchOption.AllDirectories);
                        
                        foreach (var file in foundFiles)
                        {
                            var relativePath = Path.GetRelativePath(gameRootPath, file);
                            modFiles.Add(relativePath);
                        }
                    }
                }
            }

            if (!modFiles.Any())
            {
                // For manual mods, if we can't find files here, let backup service try
                if (!isManualMod)
                {
                    _loggingService.LogError(Strings.ModNotFound, modId);
                    return false;
                }
                // For manual mods, the backup service will handle finding the files or restoring from backup
            }

            // Use backup service for enable/disable operations
            if (enable)
            {
                return await _modBackupService.EnableModFromBackupAsync(modId);
            }
            else
            {
                return await _modBackupService.DisableModWithBackupAsync(modId, modFiles);
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.ModToggleError, modId, enable);
            return false;
        }
    }


    public async Task<Dictionary<string, object>> GetModConfigurationAsync(string modId)
    {
        var result = new Dictionary<string, object>();
        var comments = new Dictionary<string, string>();
        var standaloneComments = new List<string>();
        
        try
        {
            var modConfig = _availableMods.FirstOrDefault(m => m.Id == modId);
            if (modConfig == null || string.IsNullOrEmpty(modConfig.ConfigSectionName))
            {
                _loggingService.LogInfo(Strings.ModConfigReadFailedNoSection, modId);
                return result;
            }

            var gameRootPath = _settingsService.Settings.GameRootPath;
            var configPath = Path.Combine(gameRootPath, "UserData", "MelonPreferences.cfg");
            
            _loggingService.LogInfo(Strings.TryingToReadConfigFile, configPath);

            if (!File.Exists(configPath))
            {
                _loggingService.LogInfo(Strings.ConfigFileNotExists, configPath);
                return result;
            }

            var lines = await File.ReadAllLinesAsync(configPath);
            var inTargetSection = false;
            var targetSectionName = $"[{modConfig.ConfigSectionName}]";
            
            _loggingService.LogInfo(Strings.SearchingForConfigSection, targetSectionName);

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                
                if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                {
                    inTargetSection = trimmed == targetSectionName;
                    if (inTargetSection)
                    {
                        _loggingService.LogInfo(Strings.FoundConfigSection, targetSectionName);
                    }
                    continue;
                }

                if (inTargetSection)
                {
                    // 处理单独的注释行
                    if (trimmed.StartsWith("#") && !trimmed.Contains("="))
                    {
                        var standaloneComment = trimmed.Substring(1).Trim(); // 移除#号
                        if (!string.IsNullOrEmpty(standaloneComment))
                        {
                            standaloneComments.Add(standaloneComment);
                            _loggingService.LogInfo(Strings.FoundStandaloneComment, standaloneComment);
                        }
                        continue;
                    }
                    
                    // 处理配置项
                    if (trimmed.Contains("="))
                    {
                        var parts = trimmed.Split('=', 2);
                        if (parts.Length == 2)
                        {
                            var key = parts[0].Trim().Trim('"');
                            var valueAndComment = parts[1];
                            
                            // Extract comment (everything after #)
                            var comment = "";
                            var commentIndex = valueAndComment.IndexOf('#');
                            if (commentIndex >= 0)
                            {
                                comment = valueAndComment.Substring(commentIndex + 1).Trim();
                                valueAndComment = valueAndComment.Substring(0, commentIndex);
                            }
                            
                            var value = valueAndComment.Trim();
                            
                            // Check if this is a TOML array - starts with [ and ends with ]
                            if (value.StartsWith("[") && value.EndsWith("]"))
                            {
                                // Keep arrays as-is, don't remove quotes
                                // Arrays should not have outer quotes in TOML
                            }
                            else
                            {
                                // Remove quotes from regular string values
                                value = value.Trim('"');
                            }

                            if (bool.TryParse(value, out var boolValue))
                            {
                                result[key] = boolValue;
                            }
                            else
                            {
                                result[key] = value;
                            }
                            
                            if (!string.IsNullOrEmpty(comment))
                            {
                                comments[key] = comment;
                            }
                            
                            var commentSuffix = string.IsNullOrEmpty(comment) ? "" : $" # {comment}";
                            _loggingService.LogInfo(Strings.ReadConfigItem, key, value, commentSuffix);
                        }
                    }
                }
            }

            // Store comments and standalone comments for later use
            _configComments[modId] = comments;
            _standaloneComments[modId] = standaloneComments;

            _loggingService.LogInfo(Strings.ConfigReadComplete, modId, result.Count);
            return result;
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.ModConfigReadError, modId);
            return result;
        }
    }

    public async Task<Dictionary<string, string>> GetModConfigurationCommentsAsync(string modId)
    {
        return _configComments.GetValueOrDefault(modId) ?? new Dictionary<string, string>();
    }

    public async Task<List<string>> GetModConfigurationStandaloneCommentsAsync(string modId)
    {
        return _standaloneComments.GetValueOrDefault(modId) ?? new List<string>();
    }

    public async Task<List<ConfigurationItemViewModel>> GetModConfigurationOrderedAsync(string modId)
    {
        var result = new List<ConfigurationItemViewModel>();
        
        try
        {
            var modConfig = _availableMods.FirstOrDefault(m => m.Id == modId);
            if (modConfig == null || string.IsNullOrEmpty(modConfig.ConfigSectionName))
            {
                _loggingService.LogInfo(Strings.ModConfigReadFailedNoSection, modId);
                return result;
            }

            var gameRootPath = _settingsService.Settings.GameRootPath;
            var configPath = Path.Combine(gameRootPath, "UserData", "MelonPreferences.cfg");
            
            if (!File.Exists(configPath))
            {
                _loggingService.LogInfo(Strings.ConfigFileNotExists, configPath);
                return result;
            }

            var lines = await File.ReadAllLinesAsync(configPath);
            var inTargetSection = false;
            var targetSectionName = $"[{modConfig.ConfigSectionName}]";
            
            _loggingService.LogInfo(Strings.SearchingForConfigSection, targetSectionName);

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                
                if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                {
                    inTargetSection = trimmed == targetSectionName;
                    if (inTargetSection)
                    {
                        _loggingService.LogInfo(Strings.FoundConfigSection, targetSectionName);
                    }
                    continue;
                }

                if (inTargetSection)
                {
                    // 处理单独的注释行
                    if (trimmed.StartsWith("#") && !trimmed.Contains("="))
                    {
                        var standaloneComment = trimmed.Substring(1).Trim(); // 移除#号
                        if (!string.IsNullOrEmpty(standaloneComment))
                        {
                            var localizedComment = GetLocalizedConfigComment(modId, standaloneComment);
                            var commentItem = new ConfigurationItemViewModel(localizedComment, true);
                            result.Add(commentItem);
                            _loggingService.LogInfo(Strings.FoundStandaloneComment, standaloneComment);
                        }
                        continue;
                    }
                    
                    // 处理配置项
                    if (trimmed.Contains("="))
                    {
                        var parts = trimmed.Split('=', 2);
                        if (parts.Length == 2)
                        {
                            var key = parts[0].Trim().Trim('"');
                            var valueAndComment = parts[1];
                            
                            // Extract comment (everything after #)
                            var comment = "";
                            var commentIndex = valueAndComment.IndexOf('#');
                            if (commentIndex >= 0)
                            {
                                comment = valueAndComment.Substring(commentIndex + 1).Trim();
                                valueAndComment = valueAndComment.Substring(0, commentIndex);
                            }
                            
                            var value = valueAndComment.Trim();
                            
                            // Check if this is a TOML array - starts with [ and ends with ]
                            if (value.StartsWith("[") && value.EndsWith("]"))
                            {
                                // Keep arrays as-is, don't remove quotes
                                // Arrays should not have outer quotes in TOML
                            }
                            else
                            {
                                // Remove quotes from regular string values
                                value = value.Trim('"');
                            }

                            object typedValue;
                            if (bool.TryParse(value, out var boolValue))
                            {
                                typedValue = boolValue;
                            }
                            else
                            {
                                typedValue = value;
                            }
                            
                            var localizedLabel = GetLocalizedConfigLabel(modId, key);
                            var localizedComment = string.IsNullOrEmpty(comment) ? "" : 
                                GetLocalizedConfigComment(modId, comment);
                            
                            var configItem = new ConfigurationItemViewModel(key, localizedLabel, typedValue, "", localizedComment);
                            result.Add(configItem);
                            
                            var commentSuffix = string.IsNullOrEmpty(comment) ? "" : $" # {comment}";
                            _loggingService.LogInfo(Strings.ReadConfigItem, key, value, commentSuffix);
                        }
                    }
                }
            }

            _loggingService.LogInfo(Strings.ConfigReadOrderedComplete, modId, result.Count);
            return result;
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.ModConfigReadOrderedError, modId);
            return result;
        }
    }

    public async Task<bool> SaveModConfigurationAsync(string modId, Dictionary<string, object> configuration)
    {
        try
        {
            var modConfig = _availableMods.FirstOrDefault(m => m.Id == modId);
            if (modConfig == null || string.IsNullOrEmpty(modConfig.ConfigSectionName))
                return false;

            var gameRootPath = _settingsService.Settings.GameRootPath;
            var configPath = Path.Combine(gameRootPath, "UserData", "MelonPreferences.cfg");

            if (!File.Exists(configPath))
                return false;

            var lines = await File.ReadAllLinesAsync(configPath);
            var newLines = new List<string>();
            var inTargetSection = false;

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                
                if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                {
                    inTargetSection = trimmed == $"[{modConfig.ConfigSectionName}]";
                    newLines.Add(line);
                    continue;
                }

                if (inTargetSection && trimmed.Contains("="))
                {
                    var parts = trimmed.Split('=', 2);
                    if (parts.Length == 2)
                    {
                        var key = parts[0].Trim().Trim('"');
                        
                        if (configuration.TryGetValue(key, out var newValue))
                        {
                            var comment = "";
                            var valuePart = parts[1];
                            var commentIndex = valuePart.IndexOf('#');
                            if (commentIndex >= 0)
                            {
                                comment = valuePart.Substring(commentIndex);
                            }

                            string formattedValue;
                            if (newValue is bool boolVal)
                            {
                                formattedValue = boolVal.ToString().ToLower();
                            }
                            else if (newValue is int || newValue is long || newValue is float || newValue is double)
                            {
                                // Keep numeric values as-is without quotes
                                formattedValue = newValue.ToString();
                            }
                            else
                            {
                                var valueStr = newValue.ToString();
                                
                                // Try to determine if the original value was numeric
                                var originalValue = parts[1].Substring(0, commentIndex >= 0 ? commentIndex : parts[1].Length).Trim();
                                var wasNumeric = IsNumericValue(originalValue.Trim('"') ?? "");
                                
                                if (wasNumeric && IsNumericValue(valueStr ?? ""))
                                {
                                    // Keep as numeric without quotes
                                    formattedValue = valueStr;
                                }
                                else if (valueStr?.Trim().StartsWith("[") == true && valueStr?.Trim().EndsWith("]") == true)
                                {
                                    // Keep arrays as-is, no quotes
                                    formattedValue = valueStr.Trim();
                                }
                                else
                                {
                                    // Wrap strings in quotes
                                    formattedValue = $"\"{valueStr}\"";
                                }
                            }
                            newLines.Add($"\"{key}\" = {formattedValue} {comment}".Trim());
                            continue;
                        }
                    }
                }

                newLines.Add(line);
            }

            await File.WriteAllLinesAsync(configPath, newLines);
            _loggingService.LogInfo(Strings.ModConfigSaved, modId);
            return true;
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.ModConfigSaveError, modId);
            return false;
        }
    }

    public async Task RefreshModListAsync()
    {
        await LoadAvailableModsAsync();
    }

    private async Task LoadManifestAsync()
    {
        try
        {
            var manifestPath = Path.Combine(_settingsService.AppDataPath, "mod_install_manifest.json");
            if (File.Exists(manifestPath))
            {
                var json = await File.ReadAllTextAsync(manifestPath);
                var manifest = JsonConvert.DeserializeObject<ModInstallManifest>(json);
                _installManifest = manifest ?? new ModInstallManifest();
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.ManifestLoadError);
        }
    }

    private async Task SaveManifestAsync()
    {
        try
        {
            var manifestPath = Path.Combine(_settingsService.AppDataPath, "mod_install_manifest.json");
            var json = JsonConvert.SerializeObject(_installManifest, Formatting.Indented);
            await File.WriteAllTextAsync(manifestPath, json);
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.ManifestSaveError);
        }
    }

    private async Task LoadAvailableModsAsync()
    {
        try
        {
            _availableMods = await _networkService.GetModConfigAsync(_settingsService.Settings.ModConfigUrl);
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.AvailableModsLoadError);
        }
    }

    private string GetLocalizedName(ModConfig modConfig)
    {
        var language = _settingsService.Settings.Language;
        return modConfig.Name.GetValueOrDefault(language) ?? 
               modConfig.Name.GetValueOrDefault("en-US") ?? 
               modConfig.Id;
    }

    private bool IsModEnabled(string binaryFileName, string modsPath)
    {
        var modFile = Path.Combine(modsPath, binaryFileName);
        
        // If the original file exists, mod is enabled
        return File.Exists(modFile);
    }

    private bool IsModInstalled(string binaryFileName, string modsPath)
    {
        var modFile = Path.Combine(modsPath, binaryFileName);
        
        // Check if mod file exists in Mods folder
        if (File.Exists(modFile))
            return true;
            
        // Check if mod exists in disabled backup folder
        var gameRootPath = _settingsService.Settings.GameRootPath;
        var disabledPath = Path.Combine(gameRootPath, "GHPCMM", "modbackup", "disabled");
        
        if (Directory.Exists(disabledPath))
        {
            var disabledModDirs = Directory.GetDirectories(disabledPath);
            foreach (var disabledModDir in disabledModDirs)
            {
                // Check if any backup contains this binary file
                var backupFiles = Directory.GetFiles(disabledModDir, "*", SearchOption.TopDirectoryOnly)
                    .Where(f => Path.GetFileName(f) != "backup_paths.json");
                    
                if (backupFiles.Any(f => f.Contains(binaryFileName.Replace(".dll", "")) || Path.GetFileName(f) == binaryFileName.Replace('\\', '_').Replace('/', '_')))
                {
                    return true;
                }
            }
        }
        
        return false;
    }

    private string GetRepoOwner(string repoUrl)
    {
        var uri = new Uri(repoUrl);
        var segments = uri.AbsolutePath.Trim('/').Split('/');
        // For URLs like https://api.github.com/repos/owner/repo/releases/latest
        // segments = ["repos", "owner", "repo", "releases", "latest"]
        // We want segments[1] for the owner
        return segments.Length >= 2 ? segments[1] : "";
    }

    private string GetRepoName(string repoUrl)
    {
        var uri = new Uri(repoUrl);
        var segments = uri.AbsolutePath.Trim('/').Split('/');
        // For URLs like https://api.github.com/repos/owner/repo/releases/latest
        // segments = ["repos", "owner", "repo", "releases", "latest"]
        // We want segments[2] for the repo name
        return segments.Length >= 3 ? segments[2] : "";
    }

    private async Task<bool> ShowScriptWarningAsync()
    {
        return await App.Current.Dispatcher.InvokeAsync(() =>
        {
            var result = System.Windows.MessageBox.Show(
                GHPC_Mod_Manager.Resources.Strings.ScriptModSecurityWarning,
                GHPC_Mod_Manager.Resources.Strings.SecurityWarningTitle,
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning
            );
            return result == System.Windows.MessageBoxResult.Yes;
        });
    }

    private async Task ExecuteInstallScriptWithTrackingAsync(string base64Script, string downloadedFilePath, string gameRootPath, string targetFileName, IFileOperationTracker tracker, IProgress<DownloadProgress>? progress)
    {
        // 记录脚本执行前的文件状态
        var filesBeforeScript = new Dictionary<string, DateTime>();
        await RecordDirectoryStateAsync(gameRootPath, filesBeforeScript);

        var scriptContent = Encoding.UTF8.GetString(Convert.FromBase64String(base64Script));
        var tempBatFile = Path.Combine(_settingsService.TempPath, $"install_{Guid.NewGuid()}.bat");

        await File.WriteAllTextAsync(tempBatFile, scriptContent);
        
        var processInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = tempBatFile,
            WorkingDirectory = gameRootPath,
            Arguments = $"\"{downloadedFilePath}\" \"{gameRootPath}\" \"{targetFileName}\"",
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var scriptStartTime = DateTime.Now;

        using var process = System.Diagnostics.Process.Start(processInfo);
        if (process != null)
        {
            await process.WaitForExitAsync();
        }
        else
        {
            _loggingService.LogError(Strings.FailedToStartScriptProcess);
        }
        
        // 检测脚本执行后文件的变化
        var filesAfterScript = new Dictionary<string, DateTime>();
        await RecordDirectoryStateAsync(gameRootPath, filesAfterScript);

        // 分析脚本创建/修改的文件
        foreach (var (filePath, lastWriteTime) in filesAfterScript)
        {
            var relativePath = Path.GetRelativePath(gameRootPath, filePath);

            if (!filesBeforeScript.ContainsKey(filePath))
            {
                // 新创建的文件
                tracker.RecordFileOperation(new FileOperation
                {
                    Type = FileOperationType.Create,
                    SourcePath = "script_created",
                    TargetPath = filePath,
                    FileSize = File.Exists(filePath) ? new System.IO.FileInfo(filePath).Length : 0
                });
            }
            else if (lastWriteTime > scriptStartTime)
            {
                // 文件在脚本执行期间被修改
                tracker.RecordFileOperation(new FileOperation
                {
                    Type = FileOperationType.Overwrite,
                    SourcePath = "script_modified",
                    TargetPath = filePath,
                    FileSize = File.Exists(filePath) ? new System.IO.FileInfo(filePath).Length : 0
                });
            }
        }

        // Clean up temp files
        File.Delete(tempBatFile);
        if (File.Exists(downloadedFilePath))
        {
            File.Delete(downloadedFilePath);
        }
    }

    private async Task RecordDirectoryStateAsync(string directory, Dictionary<string, DateTime> fileStates)
    {
        try
        {
            var files = Directory.GetFiles(directory, "*", SearchOption.AllDirectories);
            foreach (var file in files)
            {
                fileStates[file] = File.GetLastWriteTime(file);
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError(Strings.ErrorRecordingDirectoryState, directory, ex.Message);
        }
    }

    private async Task<string> SaveTempDownloadFileAsync(byte[] downloadData, string originalFileName)
    {
        var tempFilePath = Path.Combine(_settingsService.TempPath, $"download_{Guid.NewGuid()}_{originalFileName}");
        await File.WriteAllBytesAsync(tempFilePath, downloadData);
        return tempFilePath;
    }

    public string GetLocalizedConfigLabel(string modId, string configKey)
    {
        return _modI18nService.GetLocalizedLabel(modId, configKey, configKey);
    }

    public string GetLocalizedConfigComment(string modId, string commentKey)
    {
        return _modI18nService.GetLocalizedComment(modId, commentKey, commentKey);
    }

    /// <summary>
    /// Check for mod conflicts before installation
    /// </summary>
    public async Task<(bool HasConflicts, List<string> ConflictingMods)> CheckModConflictsAsync(ModConfig modConfig)
    {
        var conflictingMods = new List<string>();
        
        _loggingService.LogInfo(Strings.CheckingModConflicts, modConfig.Id);
        
        if (modConfig.Conflicts?.Any() == true)
        {
            foreach (var conflictId in modConfig.Conflicts)
            {
                if (_installManifest.InstalledMods.ContainsKey(conflictId))
                {
                    conflictingMods.Add(conflictId);
                    _loggingService.LogWarning(Strings.ConflictFound, conflictId);
                }
            }
        }
        
        _loggingService.LogInfo(Strings.ModConflictCheckComplete, modConfig.Id);
        return (conflictingMods.Any(), conflictingMods);
    }

    /// <summary>
    /// Check for missing mod dependencies before installation
    /// </summary>
    public async Task<(bool HasMissingRequirements, List<string> MissingMods)> CheckModDependenciesAsync(ModConfig modConfig)
    {
        var missingMods = new List<string>();
        
        _loggingService.LogInfo(Strings.CheckingModDependencies, modConfig.Id);
        
        if (modConfig.Requirements?.Any() == true)
        {
            foreach (var requiredId in modConfig.Requirements)
            {
                if (!_installManifest.InstalledMods.ContainsKey(requiredId))
                {
                    missingMods.Add(requiredId);
                    _loggingService.LogWarning(Strings.MissingRequirement, requiredId);
                }
            }
        }
        
        _loggingService.LogInfo(Strings.ModDependencyCheckComplete, modConfig.Id);
        return (missingMods.Any(), missingMods);
    }

    /// <summary>
    /// Get display name for a mod by ID
    /// </summary>
    private string GetModDisplayName(string modId)
    {
        // Try to get the display name from available mod configs
        var availableMods = Task.Run(async () => await _networkService.GetModConfigAsync(_settingsService.Settings.ModConfigUrl)).Result;
        var modConfig = availableMods.FirstOrDefault(m => m.Id == modId);
        
        if (modConfig?.Name != null)
        {
            var currentLanguage = _settingsService.Settings.Language;
            return modConfig.Name.TryGetValue(currentLanguage, out var name) ? name : 
                   modConfig.Name.TryGetValue("en-US", out var enName) ? enName : modId;
        }
        
        return modId;
    }

    private bool IsNumericValue(string value)
    {
        return int.TryParse(value, out _) || 
               long.TryParse(value, out _) || 
               float.TryParse(value, out _) || 
               double.TryParse(value, out _);
    }
}