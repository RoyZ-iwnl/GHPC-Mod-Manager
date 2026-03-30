using GHPC_Mod_Manager.Models;
using GHPC_Mod_Manager.ViewModels;
using Newtonsoft.Json;
using System.IO;
using System.Windows;
using GHPC_Mod_Manager.Resources;
using GHPC_Mod_Manager.Helpers;

namespace GHPC_Mod_Manager.Services;

public interface IModManagerService
{
    Task<List<ModViewModel>> GetModListAsync(bool forceRefresh = false);
    Task<bool> InstallModAsync(ModConfig modConfig, string version, IProgress<DownloadProgress>? progress = null, bool skipDependencyCheck = false, bool skipConflictCheck = false, bool preferBackup = true);
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
    Task<(bool HasConflicts, List<(string ModId, string ConflictsWith)> Conflicts)> CheckEnabledModsConflictsAsync();
    Task<(bool HasMissingDependencies, List<(string ModId, string RequiredMod)> MissingDependencies)> CheckEnabledModsDependenciesAsync();

    // 新增：获取指定MOD的所有GitHub Releases（用于版本选择）
    Task<List<GitHubRelease>> GetModReleasesAsync(string modId);

    // 新增：检查单个MOD的依赖状态（供详情页和列表图标使用）
    Task<(bool AllSatisfied, List<string> MissingModIds)> CheckSingleModDependenciesAsync(string modId);

    // 重置MOD配置段
    Task<bool> ResetModConfigurationAsync(string modId);

    // 检查配置段是否存在
    Task<bool> ConfigSectionExistsAsync(string modId);
    Task<List<ModIntegrityIssue>> CheckManagedModsIntegrityAsync();
}

public partial class ModManagerService : IModManagerService
{
    private readonly ISettingsService _settingsService;
    private readonly INetworkService _networkService;
    private readonly ILoggingService _loggingService;
    private readonly IModI18nService _modI18nService;
    private readonly IModBackupService _modBackupService;
    private readonly IProcessService _processService;
    private readonly IMainConfigService _mainConfigService;
    private List<ModConfig> _availableMods = new();
    private ModInstallManifest _installManifest = new();
    private Dictionary<string, Dictionary<string, string>> _configComments = new(); // Store comments for each mod
    private Dictionary<string, List<string>> _standaloneComments = new(); // Store standalone comments for each mod

    public ModManagerService(ISettingsService settingsService, INetworkService networkService, ILoggingService loggingService, IModI18nService modI18nService, IModBackupService modBackupService, IProcessService processService, IMainConfigService mainConfigService)
    {
        _settingsService = settingsService;
        _networkService = networkService;
        _loggingService = loggingService;
        _modI18nService = modI18nService;
        _modBackupService = modBackupService;
        _processService = processService;
        _mainConfigService = mainConfigService;
    }

    public async Task<List<ModViewModel>> GetModListAsync(bool forceRefresh = false)
    {
        await LoadManifestAsync();
        await LoadAvailableModsAsync(forceRefresh);

        var result = new List<ModViewModel>();
        var gameRootPath = _settingsService.Settings.GameRootPath;
        var modsPath = Path.Combine(gameRootPath, "Mods");

        // 先创建所有ModViewModel（不包含GitHub版本信息）
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
                // MainBinaryFileName 为空时依赖清单判断，否则用传统方式
                bool useManifestState = IsReplaceMode(modConfig) || string.IsNullOrEmpty(modConfig.MainBinaryFileName);
                bool modFilesExist = useManifestState
                    ? IsModActuallyInstalledFromManifest(installInfo)
                    : IsModActuallyInstalled(modConfig.MainBinaryFileName, modsPath, installInfo);

                if (modFilesExist)
                {
                    viewModel.IsInstalled = true;
                    viewModel.InstalledVersion = installInfo.Version;
                    viewModel.IsEnabled = useManifestState
                        ? IsModEnabledFromManifest(installInfo)
                        : IsModEnabled(modConfig.MainBinaryFileName, modsPath);
                }
                else
                {
                    _loggingService.LogWarning(Strings.ModInManifestButFilesNotFound, modConfig.Id);
                    _installManifest.InstalledMods.Remove(modConfig.Id);
                    await SaveManifestAsync();
                }
            }
            else
            {
                // 无清单记录时，只有 MainBinaryFileName 非空才能检测手动安装
                if (!string.IsNullOrEmpty(modConfig.MainBinaryFileName) && IsModInstalled(modConfig.MainBinaryFileName, modsPath))
                {
                    viewModel.IsInstalled = true;
                    viewModel.IsManuallyInstalled = true;
                    viewModel.IsSupportedManualMod = true;
                    viewModel.InstalledVersion = GHPC_Mod_Manager.Resources.Strings.Manual;
                    viewModel.IsEnabled = IsModEnabled(modConfig.MainBinaryFileName, modsPath);
                }
            }

            result.Add(viewModel);
        }

        // 并行检查备份状态 + 获取GitHub版本信息
        var uninstalledBackupRoot = Path.Combine(gameRootPath, "GHPCMM", "modbackup", "uninstalled");
        var versionTasks = result.Select(async vm =>
        {
            // 检查是否有卸载备份（用于快速重装提示）
            var hasAnyBackup = false;
            if (Directory.Exists(uninstalledBackupRoot))
            {
                hasAnyBackup = Directory.GetDirectories(uninstalledBackupRoot, $"{vm.Id}_v*").Any();
            }
            try
            {
                // 区分 GitHub 源和直接下载源
                if (IsGitHubApiUrl(vm.Config.ReleaseUrl))
                {
                    // GitHub 模式：调用 API 获取版本
                    var releases = await _networkService.GetGitHubReleasesAsync(
                        GetRepoOwner(vm.Config.ReleaseUrl),
                        GetRepoName(vm.Config.ReleaseUrl),
                        forceRefresh
                    );
                    var latestRelease = releases.FirstOrDefault();
                    vm.LatestVersion = latestRelease?.TagName ?? GHPC_Mod_Manager.Resources.Strings.Unknown;
                    vm.UpdateDate = latestRelease?.PublishedAt;
                }
                else
                {
                    // 直接下载模式：从 URL 解析版本
                    vm.LatestVersion = ParseVersionFromUrl(vm.Config.ReleaseUrl);
                    vm.UpdateDate = null;  // 非 GitHub 源无发布日期
                }

                vm.HasBackup = !string.IsNullOrWhiteSpace(vm.LatestVersion) &&
                               vm.LatestVersion != GHPC_Mod_Manager.Resources.Strings.Unknown &&
                               await _modBackupService.CheckModBackupExistsAsync(vm.Id, vm.LatestVersion);
            }
            catch
            {
                vm.LatestVersion = GHPC_Mod_Manager.Resources.Strings.Unknown;
                vm.UpdateDate = null;
                vm.HasBackup = hasAnyBackup;
            }
        }).ToList();

        await Task.WhenAll(versionTasks);

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
                // 统一用正斜杠比较，避免Windows路径分隔符不一致导致匹配失败
                var relativeModPath = Path.GetRelativePath(_settingsService.Settings.GameRootPath, modFile).Replace('\\', '/');
                if (_installManifest.InstalledMods.Values.Any(m =>
                    m.InstalledFiles.Any(f => f.RelativePath.Replace('\\', '/') == relativeModPath)))
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

                    // Try to get latest version (支持 GitHub 和直接下载源)
                    try
                    {
                        if (IsGitHubApiUrl(matchingConfig.ReleaseUrl))
                        {
                            var releases = await _networkService.GetGitHubReleasesAsync(
                                GetRepoOwner(matchingConfig.ReleaseUrl),
                                GetRepoName(matchingConfig.ReleaseUrl)
                            );
                            supportedManualMod.LatestVersion = releases.FirstOrDefault()?.TagName ?? GHPC_Mod_Manager.Resources.Strings.Unknown;
                        }
                        else
                        {
                            supportedManualMod.LatestVersion = ParseVersionFromUrl(matchingConfig.ReleaseUrl);
                        }
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

    public async Task<bool> InstallModAsync(ModConfig modConfig, string version, IProgress<DownloadProgress>? progress = null, bool skipDependencyCheck = false, bool skipConflictCheck = false, bool preferBackup = true)
    {
        try
        {
            _loggingService.LogInfo(Strings.Installing, modConfig.Id, version);

            if (!EnsureManagedConfigSupported(modConfig))
                return false;

            if (!ShowReplaceInstallWarningIfNeeded(modConfig))
                return false;

            // Check if we can reinstall from backup first (quick install)
            var hasBackup = await _modBackupService.CheckModBackupExistsAsync(modConfig.Id, version);
            if (hasBackup && preferBackup)
            {
                _loggingService.LogInfo(Strings.AttemptingQuickReinstallFromBackup, modConfig.Id, version);

                var quickReinstallResult = await TryQuickReinstallAsync(modConfig, version);
                var quickInstallInfo = quickReinstallResult.InstallInfo;
                if (quickInstallInfo != null)
                {
                    await LoadManifestAsync();
                    _installManifest.InstalledMods[modConfig.Id] = quickInstallInfo;
                    await SaveManifestAsync();

                    _loggingService.LogInfo(Strings.ModReinstalledFromBackup, modConfig.Id, version);
                    return true;
                }

                if (quickReinstallResult.ShouldAbortInstall)
                    return false;
            }
            else if (hasBackup && !preferBackup)
            {
                await _modBackupService.DeleteModBackupAsync(modConfig.Id, version);
            }

            // Standard installation process continues if backup installation fails or doesn't exist
            // 依赖检查：skipDependencyCheck=true时跳过（ViewModel已在调用前处理依赖对话框）
            if (!skipDependencyCheck)
            {
                var (hasMissingRequirements, missingMods) = await CheckModDependenciesAsync(modConfig);
                if (hasMissingRequirements)
                {
                    var missingModNames = missingMods.Select(GetModDisplayName).ToList();
                    var currentModName = GetModDisplayName(modConfig.Id);
                    var message = string.Format(Strings.ModDependencyMessage, currentModName, string.Join(", ", missingModNames));

                    await MessageDialogHelper.ShowWarningAsync(message, Strings.ModDependencyMissing);
                    return false;
                }
            }

            // 冲突检查：skipConflictCheck=true时跳过（ViewModel已在调用前处理冲突对话框）
            if (!skipConflictCheck)
            {
                var (hasConflicts, conflictingMods) = await CheckModConflictsAsync(modConfig);
                if (hasConflicts)
                {
                    var conflictingModNames = conflictingMods.Select(GetModDisplayName).ToList();
                    var currentModName = GetModDisplayName(modConfig.Id);
                    var message = string.Format(Strings.ModConflictMessage, currentModName, string.Join(", ", conflictingModNames));

                    if (!await MessageDialogHelper.ConfirmAsync(message, Strings.ModConflictDetected))
                        return false;
                }
            }

            // 下载完成后检查游戏是否已启动，避免写入冲突
            if (_processService.IsGameRunning)
            {
                _loggingService.LogError(Strings.GameRunningCannotOperate);
                return false;
            }

            var downloadResult = await DownloadModPackageAsync(modConfig, version, progress);
            if (downloadResult == null)
                return false;

            ModInstallInfo? installInfo = IsReplaceMode(modConfig)
                ? await InstallReplaceModeAsync(modConfig, version, downloadResult.Value.Data, downloadResult.Value.FileName)
                : await InstallDirectReleaseModeAsync(modConfig, version, downloadResult.Value.Data, downloadResult.Value.FileName);

            if (installInfo == null || !installInfo.InstalledFiles.Any())
            {
                _loggingService.LogError(Strings.NoFilesProcessedDuringInstallation, modConfig.Id);
                return false;
            }

            if (!await RefreshUninstalledBackupAsync(installInfo))
            {
                _loggingService.LogWarning("Failed to refresh uninstalled backup after install: {0} {1}", modConfig.Id, version);
            }

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
        string previousVersion = string.Empty;
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

            if (!EnsureManagedConfigSupported(modConfig))
                return false;

            // Check if mod is currently installed
            if (!_installManifest.InstalledMods.TryGetValue(modId, out var currentInstallInfo))
            {
                _loggingService.LogError(Strings.ModNotInstalledCannotUpdate, modId);
                return false;
            }
            previousVersion = currentInstallInfo.Version;

            var downloadResult = await DownloadModPackageAsync(modConfig, newVersion, progress);
            if (downloadResult == null)
                return false;

            // 下载完成后检查游戏是否已启动，避免写入冲突
            if (_processService.IsGameRunning)
            {
                _loggingService.LogError(Strings.GameRunningCannotOperate);
                return false;
            }

            var currentPaths = GetInstalledRelativePaths(currentInstallInfo);
            var backupCreated = await _modBackupService.UninstallModWithBackupAsync(modId, currentInstallInfo.Version, currentPaths);
            if (!backupCreated)
            {
                ShowManagedBackupFailureMessage(modId);
                return false;
            }

            await DeleteManagedFilesAsync(currentPaths);

            if (IsReplaceMode(modConfig))
            {
                await RestoreImmediateBackupFilesAsync(modId, currentInstallInfo.BackupFiles);
                await ShowReplaceEmptyDirectoryWarningIfNeededAsync(modConfig);
            }

            var newInstallInfo = IsReplaceMode(modConfig)
                ? await InstallReplaceModeAsync(modConfig, newVersion, downloadResult.Value.Data, downloadResult.Value.FileName)
                : await InstallDirectReleaseModeAsync(modConfig, newVersion, downloadResult.Value.Data, downloadResult.Value.FileName);

            if (newInstallInfo == null)
                throw new InvalidOperationException($"No files were installed for {modId}.");

            if (!await RefreshUninstalledBackupAsync(newInstallInfo))
            {
                _loggingService.LogWarning("Failed to refresh uninstalled backup after update: {0} {1}", modId, newVersion);
            }

            _installManifest.InstalledMods[modId] = newInstallInfo;
            await SaveManifestAsync();

            await _modBackupService.DeleteModBackupAsync(modId, previousVersion);

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
                var modConfig = _availableMods.FirstOrDefault(m => m.Id == modId);
                if (modConfig != null)
                {
                    var restoredInstallInfo = (await TryQuickReinstallAsync(modConfig, previousVersion)).InstallInfo;
                    if (restoredInstallInfo != null)
                    {
                        _installManifest.InstalledMods[modId] = restoredInstallInfo;
                        await SaveManifestAsync();
                        _loggingService.LogInfo(Strings.RestoredModFromBackupAfterFailedUpdate, modId);
                    }
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
            var modConfig = _availableMods.FirstOrDefault(m => m.Id == modId);

            if (HasDisabledBackup(installInfo))
            {
                _loggingService.LogInfo(Strings.ModDisabledMovingBackupToUninstalled, modId);
                if (!await MoveDisabledBackupToUninstalledAsync(installInfo))
                {
                    ShowManagedBackupFailureMessage(modId);
                    return false;
                }

                _loggingService.LogInfo(Strings.MovedDisabledBackupToUninstalled, modId);
            }
            else
            {
                var currentPaths = GetInstalledRelativePaths(installInfo);
                var backupCreated = await _modBackupService.UninstallModWithBackupAsync(modId, installInfo.Version, currentPaths);
                if (!backupCreated)
                {
                    ShowManagedBackupFailureMessage(modId);
                    return false;
                }

                await DeleteManagedFilesAsync(currentPaths);

                if (modConfig != null && IsReplaceMode(modConfig))
                {
                    await RestoreImmediateBackupFilesAsync(modId, installInfo.BackupFiles);
                    await ShowReplaceEmptyDirectoryWarningIfNeededAsync(modConfig);
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
                        // MainBinaryFileName 为空时无法处理手动安装（无标识文件）
                        if (string.IsNullOrEmpty(matchingConfig.MainBinaryFileName))
                        {
                            _loggingService.LogError(Strings.CannotUninstallManualModWithoutBinaryName, modId);
                            return false;
                        }

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
                modFiles = GetInstalledRelativePaths(installInfo);
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
                var modConfig = _availableMods.FirstOrDefault(m => m.Id == modId);
                if (modConfig != null && installInfo != null && IsReplaceMode(modConfig))
                {
                    var enabledInstallInfo = await EnableReplaceModAsync(modConfig, installInfo);
                    if (enabledInstallInfo == null)
                        return false;

                    _installManifest.InstalledMods[modId] = enabledInstallInfo;
                    await SaveManifestAsync();
                    return true;
                }

                return await _modBackupService.EnableModFromBackupAsync(modId);
            }
            else
            {
                if (installInfo != null)
                {
                    var modConfig = _availableMods.FirstOrDefault(m => m.Id == modId);
                    if (modConfig != null && IsReplaceMode(modConfig))
                    {
                        var replaceDisabled = await DisableReplaceModAsync(installInfo);
                        if (!replaceDisabled)
                            return false;

                        installInfo.BackupFiles.Clear();
                        _installManifest.InstalledMods[modId] = installInfo;
                        await SaveManifestAsync();
                        return true;
                    }
                }

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
                            var commentItem = new ConfigurationItemViewModel(localizedComment);
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

                            // 检查是否为多选类型
                            var multipleChoiceOptions = _modI18nService.GetMultipleChoiceOptions(modId, key);
                            if (multipleChoiceOptions != null && multipleChoiceOptions.Count > 0)
                            {
                                configItem.IsMultipleChoiceType = true;
                                configItem.IsStringType = false;

                                // 解析当前值（数组格式）
                                var selectedValues = new List<string>();
                                if (value.StartsWith("[") && value.EndsWith("]"))
                                {
                                    var arrayContent = value.Substring(1, value.Length - 2);
                                    selectedValues = arrayContent.Split(',')
                                        .Select(v => v.Trim().Trim('"').Trim())
                                        .Where(v => !string.IsNullOrEmpty(v))
                                        .ToList();
                                }

                                // 创建多选选项
                                foreach (var option in multipleChoiceOptions)
                                {
                                    var isSelected = selectedValues.Contains(option);
                                    configItem.MultipleChoiceOptions.Add(new MultipleChoiceOption(option, isSelected));
                                }
                            }
                            // 检查是否为单选类型
                            else
                            {
                                var singleChoiceOptions = _modI18nService.GetSingleChoiceOptions(modId, key);
                                if (singleChoiceOptions != null && singleChoiceOptions.Count > 0)
                                {
                                    configItem.IsChoiceType = true;
                                    configItem.IsStringType = false;
                                    configItem.ChoiceOptions = singleChoiceOptions;
                                }
                            }

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
                            else if (newValue is List<string> listVal)
                            {
                                // 多选类型：格式化为 TOML 数组
                                var items = listVal.Select(item => $"\"{item}\"");
                                formattedValue = $"[ {string.Join(", ", items)} ]";
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
            _installManifest = new ModInstallManifest();
            if (File.Exists(manifestPath))
            {
                var json = await File.ReadAllTextAsync(manifestPath);
                var manifest = JsonConvert.DeserializeObject<ModInstallManifest>(json);
                _installManifest = manifest ?? new ModInstallManifest();
            }

            await MigrateManifestIfNeededAsync();
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

    private async Task LoadAvailableModsAsync(bool forceRefresh = false)
    {
        try
        {
            _availableMods = await GetModConfigsWithFallbackAsync(forceRefresh);
            NormalizeModConfigs(_availableMods);
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.AvailableModsLoadError);
        }
    }

    private async Task<List<ModConfig>> GetModConfigsWithFallbackAsync(bool forceRefresh = false)
    {
        var urls = _mainConfigService.GetModConfigUrlCandidates();
        var lastResult = new List<ModConfig>();

        for (var i = 0; i < urls.Count; i++)
        {
            var url = urls[i];
            var configs = await _networkService.GetModConfigAsync(url, forceRefresh);
            if (configs.Count > 0)
            {
                NormalizeModConfigs(configs);
                return configs;
            }

            lastResult = configs;
            var hasNext = i < urls.Count - 1;
            if (hasNext)
                _loggingService.LogWarning("Mod配置为空或不可访问，触发fallback，尝试下一个渠道。当前渠道: {0}", url);
            else
                _loggingService.LogWarning("Mod配置为空或不可访问，且已无可用fallback。最后渠道: {0}", url);
        }

        return lastResult;
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

                foreach (var backupFile in backupFiles)
                {
                    // 从备份文件名提取原始文件名 (Mods_SubFolder_File.dll -> File.dll)
                    var backupFileName = Path.GetFileName(backupFile);
                    var lastUnderscoreIndex = backupFileName.LastIndexOf('_');
                    var originalFileName = lastUnderscoreIndex >= 0
                        ? backupFileName.Substring(lastUnderscoreIndex + 1)
                        : backupFileName;

                    if (originalFileName.Equals(binaryFileName, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>
    /// 验证manifest中记录的MOD是否实际存在（文件或备份）
    /// </summary>
    private bool IsModActuallyInstalled(string binaryFileName, string modsPath, ModInstallInfo installInfo)
    {
        var gameRootPath = _settingsService.Settings.GameRootPath;

        // 检查manifest中记录的任意一个文件是否存在
        foreach (var relativePath in GetInstalledRelativePaths(installInfo))
        {
            var fullPath = Path.Combine(gameRootPath, relativePath);
            if (File.Exists(fullPath))
            {
                return true; // 至少有一个文件存在
            }
        }

        // 检查是否在disabled备份中
        var disabledPath = Path.Combine(gameRootPath, "GHPCMM", "modbackup", "disabled", installInfo.ModId);
        if (Directory.Exists(disabledPath) &&
            Directory.GetFiles(disabledPath, "*", SearchOption.TopDirectoryOnly)
                .Any(f => Path.GetFileName(f) != "backup_paths.json"))
            return true;

        // uninstalled备份不代表已安装，不在此检查
        return false;
    }

    /// <summary>
    /// 仅依赖清单判断MOD是否实际存在（用于 Replace 等清单托管安装）
    /// </summary>
    private bool IsModActuallyInstalledFromManifest(ModInstallInfo installInfo)
    {
        var gameRootPath = _settingsService.Settings.GameRootPath;

        // 检查清单中记录的任意一个文件是否存在
        foreach (var relativePath in GetInstalledRelativePaths(installInfo))
        {
            var fullPath = Path.Combine(gameRootPath, relativePath);
            if (File.Exists(fullPath))
            {
                return true;
            }
        }

        // 检查是否在 disabled 备份中
        var disabledPath = Path.Combine(gameRootPath, "GHPCMM", "modbackup", "disabled", installInfo.ModId);
        if (Directory.Exists(disabledPath) &&
            Directory.GetFiles(disabledPath, "*", SearchOption.TopDirectoryOnly)
                .Any(f => Path.GetFileName(f) != "backup_paths.json"))
            return true;

        return false;
    }

    /// <summary>
    /// 仅依赖清单判断MOD是否启用（用于 Replace 等清单托管安装）
    /// 启用状态：清单文件中至少有一个存在且不在 disabled 备份中
    /// </summary>
    private bool IsModEnabledFromManifest(ModInstallInfo installInfo)
    {
        var gameRootPath = _settingsService.Settings.GameRootPath;

        // 如果在 disabled 备份中，则禁用
        var disabledPath = Path.Combine(gameRootPath, "GHPCMM", "modbackup", "disabled", installInfo.ModId);
        if (Directory.Exists(disabledPath) &&
            Directory.GetFiles(disabledPath, "*", SearchOption.TopDirectoryOnly)
                .Any(f => Path.GetFileName(f) != "backup_paths.json"))
            return false;

        // 检查清单文件中是否有存在的文件
        foreach (var relativePath in GetInstalledRelativePaths(installInfo))
        {
            var fullPath = Path.Combine(gameRootPath, relativePath);
            if (File.Exists(fullPath))
            {
                return true;
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

    /// <summary>
    /// 检测 ReleaseUrl 是否为 GitHub API URL
    /// GitHub API URL 格式: https://api.github.com/repos/owner/repo/...
    /// </summary>
    private bool IsGitHubApiUrl(string url)
    {
        return url.Contains("api.github.com/repos");
    }

    /// <summary>
    /// 从 URL 提取文件名
    /// </summary>
    private string GetFileNameFromUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            return Path.GetFileName(uri.AbsolutePath);
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// 从 URL 文件名中解析版本号
    ///
    /// 支持的格式示例:
    ///   - mod_v1.2.3.zip → v1.2.3
    ///   - mod-1.2.3.zip → 1.2.3
    ///   - v2.0/file.zip → v2.0
    ///   - MyMod_2.5.1_beta.zip → 2.5.1
    ///
    /// 以后可扩展:
    ///   - 自定义版本解析规则（通过 ModConfig 配置 VersionPattern 字段）
    ///   - 支持更多版本格式（日期版本如 20240101、自定义命名）
    ///   - 从外部版本检查 API/文件获取（通过 ModConfig 配置 VersionCheckUrl 字段）
    ///   - 从 HTTP 响应头获取版本信息
    /// </summary>
    private string ParseVersionFromUrl(string url)
    {
        var fileName = GetFileNameFromUrl(url);
        if (string.IsNullOrEmpty(fileName))
            return Strings.Unknown;

        // 正则匹配常见版本格式
        // 匹配: v1.2.3, v1.2, 1.2.3, 1.2（支持可选的 v/V 前缀）
        var versionPattern = @"[vV]?(\d+\.\d+(\.\d+)?)";
        var match = System.Text.RegularExpressions.Regex.Match(fileName, versionPattern);

        if (match.Success)
        {
            // 保留原始格式（包括 v 前缀）
            return match.Value;
        }

        return Strings.Unknown;
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
    /// Check for mod conflicts before installation (双向检测)
    /// </summary>
    public async Task<(bool HasConflicts, List<string> ConflictingMods)> CheckModConflictsAsync(ModConfig modConfig)
    {
        var conflictingMods = new List<string>();

        _loggingService.LogInfo(Strings.CheckingModConflicts, modConfig.Id);

        // 检查当前MOD的Conflicts列表
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

        // 双向检测:检查已安装的MOD是否将当前MOD列为冲突
        foreach (var installedMod in _installManifest.InstalledMods.Keys)
        {
            var installedModConfig = _availableMods.FirstOrDefault(m => m.Id == installedMod);
            if (installedModConfig?.Conflicts?.Contains(modConfig.Id) == true)
            {
                if (!conflictingMods.Contains(installedMod))
                {
                    conflictingMods.Add(installedMod);
                    _loggingService.LogWarning(Strings.ConflictFound, installedMod);
                }
            }
        }

        _loggingService.LogInfo(Strings.ModConflictCheckComplete, modConfig.Id);
        return (conflictingMods.Any(), conflictingMods);
    }

    /// <summary>
    /// 检查已启用MOD之间的冲突(用于启动游戏时)
    /// </summary>
    public async Task<(bool HasConflicts, List<(string ModId, string ConflictsWith)> Conflicts)> CheckEnabledModsConflictsAsync()
    {
        var conflicts = new List<(string ModId, string ConflictsWith)>();

        try
        {
            var gameRootPath = _settingsService.Settings.GameRootPath;
            if (string.IsNullOrEmpty(gameRootPath))
            {
                return (false, conflicts);
            }

            var modsPath = Path.Combine(gameRootPath, "Mods");

            // 获取所有已安装且已启用的MOD
            var enabledMods = new List<string>();
            foreach (var kvp in _installManifest.InstalledMods)
            {
                var installInfo = kvp.Value;
                var modConfig = _availableMods.FirstOrDefault(m => m.Id == kvp.Key);
                if (modConfig != null && IsModEnabledFromManifest(installInfo))
                {
                    enabledMods.Add(kvp.Key);
                }
            }

            // 检查每个已启用MOD的冲突
            foreach (var modId in enabledMods)
            {
                var modConfig = _availableMods.FirstOrDefault(m => m.Id == modId);
                if (modConfig?.Conflicts?.Any() == true)
                {
                    foreach (var conflictId in modConfig.Conflicts)
                    {
                        // 如果冲突的MOD也已启用,记录冲突
                        if (enabledMods.Contains(conflictId))
                        {
                            // 避免重复记录(A冲突B,B冲突A只记录一次)
                            if (!conflicts.Any(c => c.ModId == conflictId && c.ConflictsWith == modId))
                            {
                                conflicts.Add((modId, conflictId));
                            }
                        }
                    }
                }
            }

            return (conflicts.Any(), conflicts);
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, "CheckEnabledModsConflictsAsync failed");
            return (false, conflicts);
        }
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
    /// 检查已启用MOD的依赖是否已安装且已启用(用于启动游戏时)
    /// </summary>
    public async Task<(bool HasMissingDependencies, List<(string ModId, string RequiredMod)> MissingDependencies)> CheckEnabledModsDependenciesAsync()
    {
        var missingDependencies = new List<(string ModId, string RequiredMod)>();

        try
        {
            var gameRootPath = _settingsService.Settings.GameRootPath;
            if (string.IsNullOrEmpty(gameRootPath))
            {
                return (false, missingDependencies);
            }

            var modsPath = Path.Combine(gameRootPath, "Mods");

            // 获取所有已安装且已启用的MOD
            var enabledMods = new List<string>();
            foreach (var kvp in _installManifest.InstalledMods)
            {
                var installInfo = kvp.Value;
                var modConfig = _availableMods.FirstOrDefault(m => m.Id == kvp.Key);
                if (modConfig != null && IsModEnabledFromManifest(installInfo))
                {
                    enabledMods.Add(kvp.Key);
                }
            }

            // 检查每个已启用MOD的依赖
            foreach (var modId in enabledMods)
            {
                var modConfig = _availableMods.FirstOrDefault(m => m.Id == modId);
                if (modConfig?.Requirements?.Any() == true)
                {
                    foreach (var requiredId in modConfig.Requirements)
                    {
                        // 检查依赖的MOD是否已安装且已启用
                        if (!enabledMods.Contains(requiredId))
                        {
                            missingDependencies.Add((modId, requiredId));
                            _loggingService.LogWarning(Strings.MissingRequirement, requiredId);
                        }
                    }
                }
            }

            return (missingDependencies.Any(), missingDependencies);
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, "CheckEnabledModsDependenciesAsync failed");
            return (false, missingDependencies);
        }
    }

    /// <summary>
    /// Get display name for a mod by ID
    /// </summary>
    private string GetModDisplayName(string modId)
    {
        // Try to get the display name from available mod configs
        var availableMods = Task.Run(async () => await GetModConfigsWithFallbackAsync()).Result;
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

    /// <summary>
    /// 获取指定MOD的所有GitHub Releases（用于版本选择下拉）
    /// 非 GitHub 源返回解析的版本作为虚拟 Release
    /// </summary>
    public async Task<List<GitHubRelease>> GetModReleasesAsync(string modId)
    {
        try
        {
            // 从已缓存的mod列表中找到对应配置
            var modConfigs = await GetModConfigsWithFallbackAsync();
            var modConfig = modConfigs.FirstOrDefault(m => m.Id == modId);
            if (modConfig == null || string.IsNullOrEmpty(modConfig.ReleaseUrl))
                return new List<GitHubRelease>();

            if (IsGitHubApiUrl(modConfig.ReleaseUrl))
            {
                // GitHub 模式：获取所有 releases
                var owner = GetRepoOwner(modConfig.ReleaseUrl);
                var repo = GetRepoName(modConfig.ReleaseUrl);
                if (string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(repo))
                    return new List<GitHubRelease>();

                return await _networkService.GetGitHubReleasesAsync(owner, repo, forceRefresh: false);
            }
            else
            {
                // 直接下载模式：返回解析的版本作为虚拟 Release
                var parsedVersion = ParseVersionFromUrl(modConfig.ReleaseUrl);
                if (parsedVersion == Strings.Unknown)
                    return new List<GitHubRelease>();

                return new List<GitHubRelease>
                {
                    new GitHubRelease
                    {
                        TagName = parsedVersion,
                        Name = parsedVersion,
                        PublishedAt = DateTime.Now
                    }
                };
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, "GetModReleasesAsync failed for mod: {0}", modId);
            return new List<GitHubRelease>();
        }
    }

    /// <summary>
    /// 检查单个MOD的依赖状态（供详情页和列表图标使用）
    /// </summary>
    public async Task<(bool AllSatisfied, List<string> MissingModIds)> CheckSingleModDependenciesAsync(string modId)
    {
        try
        {
            var modConfigs = await GetModConfigsWithFallbackAsync();
            var modConfig = modConfigs.FirstOrDefault(m => m.Id == modId);
            if (modConfig == null || modConfig.Requirements?.Any() != true)
                return (true, new List<string>());

            var (hasMissing, missingMods) = await CheckModDependenciesAsync(modConfig);
            return (!hasMissing, missingMods);
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, "CheckSingleModDependenciesAsync failed for mod: {0}", modId);
            return (true, new List<string>());
        }
    }

    public async Task<bool> ConfigSectionExistsAsync(string modId)
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
            var targetSection = $"[{modConfig.ConfigSectionName}]";

            return lines.Any(line => line.Trim().Equals(targetSection, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, "ConfigSectionExistsAsync failed for mod: {0}", modId);
            return false;
        }
    }

    public async Task<bool> ResetModConfigurationAsync(string modId)
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
            var targetSection = $"[{modConfig.ConfigSectionName}]";
            var inTargetSection = false;

            foreach (var line in lines)
            {
                var trimmed = line.Trim();

                // 检测到目标配置段开始
                if (trimmed.Equals(targetSection, StringComparison.OrdinalIgnoreCase))
                {
                    inTargetSection = true;
                    continue; // 跳过配置段标题行
                }

                // 检测到新的配置段开始
                if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                {
                    inTargetSection = false;
                }

                // 不在目标配置段内，保留该行
                if (!inTargetSection)
                {
                    newLines.Add(line);
                }
            }

            await File.WriteAllLinesAsync(configPath, newLines);
            _loggingService.LogInfo(Strings.ConfigurationReset, modConfig.ConfigSectionName);
            return true;
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.ConfigurationResetFailed);
            return false;
        }
    }
}
