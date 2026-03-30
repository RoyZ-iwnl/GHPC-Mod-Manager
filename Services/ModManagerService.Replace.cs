using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Windows;
using GHPC_Mod_Manager.Models;
using GHPC_Mod_Manager.Resources;
using GHPC_Mod_Manager.Helpers;
using Newtonsoft.Json;

namespace GHPC_Mod_Manager.Services;

public partial class ModManagerService
{
    private const string BackupManifestFileName = "backup_manifest.json";
    private const string BackupPathsFileName = "backup_paths.json";

    private sealed class BackupRestoreEntry
    {
        public string BackupFilePath { get; set; } = string.Empty;
        public string RelativePath { get; set; } = string.Empty;
        public string Sha256 { get; set; } = string.Empty;
    }

    private sealed class BackupRestorePlan
    {
        public string ModId { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public List<BackupRestoreEntry> Files { get; set; } = new();
    }

    private readonly record struct QuickReinstallResult(ModInstallInfo? InstallInfo, bool ShouldAbortInstall);

    private sealed class ModBackupManifestData
    {
        public string ModId { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public DateTime BackupDate { get; set; }
        public List<string> OriginalFiles { get; set; } = new();
        public Dictionary<string, string> FilePathMapping { get; set; } = new();
        public Dictionary<string, string> FileSha256Mapping { get; set; } = new();
    }

    private bool IsReplaceMode(ModConfig modConfig) => modConfig.InstallMethod == InstallMethod.Replace;

    private List<string> GetInstalledRelativePaths(ModInstallInfo installInfo)
    {
        return installInfo.InstalledFiles
            .Select(file => NormalizeRelativePath(file.RelativePath))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private string NormalizeRelativePath(string relativePath)
    {
        return relativePath
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar);
    }

    private string GetBackupSuffix(string modId) => $".GHPCMM{modId}";

    private string GetUninstalledBackupPath(string modId, string version)
    {
        return Path.Combine(_settingsService.Settings.GameRootPath, "GHPCMM", "modbackup", "uninstalled", $"{modId}_v{version}");
    }

    private string GetDisabledBackupPath(string modId)
    {
        return Path.Combine(_settingsService.Settings.GameRootPath, "GHPCMM", "modbackup", "disabled", modId);
    }

    private bool HasDisabledBackup(ModInstallInfo installInfo)
    {
        var disabledPath = GetDisabledBackupPath(installInfo.ModId);
        return Directory.Exists(disabledPath) &&
               Directory.GetFiles(disabledPath, "*", SearchOption.TopDirectoryOnly)
                   .Any(file => Path.GetFileName(file) != BackupPathsFileName);
    }

    private bool IsManagedConfigSupported(ModConfig modConfig)
    {
        return !modConfig.HasLegacyScriptConfig;
    }

    private string GetLocalizedResourceText(string resourceKey, string fallback)
    {
        return Strings.ResourceManager.GetString(resourceKey, Strings.Culture) ?? fallback;
    }

    private bool EnsureManagedConfigSupported(ModConfig modConfig)
    {
        if (IsManagedConfigSupported(modConfig))
            return true;

        var message = string.Format(
            GetLocalizedResourceText(
                "DeprecatedScriptedConfigMessage",
                _settingsService.Settings.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
                    ? "Mod {0} 仍在使用已废弃的 Scripted 配置，当前版本不再支持安装或更新。"
                    : "Mod {0} still uses the deprecated Scripted configuration and can no longer be installed or updated."),
            GetLocalizedName(modConfig));

        MessageDialogHelper.ShowWarningAsync(message, Strings.Warning).Wait();
        _loggingService.LogWarning("Blocked deprecated scripted mod config: {0}", modConfig.Id);
        return false;
    }

    private async Task<(byte[] Data, string FileName)?> DownloadModPackageAsync(ModConfig modConfig, string version, IProgress<DownloadProgress>? progress)
    {
        if (IsGitHubApiUrl(modConfig.ReleaseUrl))
        {
            var releases = await _networkService.GetGitHubReleasesAsync(
                GetRepoOwner(modConfig.ReleaseUrl),
                GetRepoName(modConfig.ReleaseUrl)
            );

            var targetRelease = releases.FirstOrDefault(r => r.TagName == version);
            if (targetRelease == null)
            {
                _loggingService.LogError(Strings.ModVersionNotFound, modConfig.Id, version);
                return null;
            }

            var asset = targetRelease.Assets.FirstOrDefault(a => a.Name.Contains(modConfig.TargetFileNameKeyword, StringComparison.OrdinalIgnoreCase));
            if (asset == null)
            {
                _loggingService.LogError(Strings.ModAssetNotFound, modConfig.Id, modConfig.TargetFileNameKeyword);
                return null;
            }

            var data = await _networkService.DownloadFileAsync(asset.DownloadUrl, progress, expectedSize: asset.Size, expectedDigest: asset.Digest, assetName: asset.Name);
            return (data, asset.Name);
        }

        var directData = await _networkService.DownloadFileAsync(modConfig.ReleaseUrl, progress);
        return (directData, GetFileNameFromUrl(modConfig.ReleaseUrl));
    }

    private async Task<ModInstallInfo?> BuildInstallInfoAsync(string modId, string version, IEnumerable<string> relativePaths, IEnumerable<string>? backupFiles = null)
    {
        var installedFiles = await BuildInstalledFileInfosAsync(relativePaths);
        if (!installedFiles.Any())
            return null;

        return new ModInstallInfo
        {
            ModId = modId,
            Version = version,
            InstalledFiles = installedFiles,
            BackupFiles = backupFiles?
                .Select(NormalizeRelativePath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<string>(),
            InstallDate = DateTime.Now
        };
    }

    private async Task<ModInstallInfo?> InstallDirectReleaseModeAsync(ModConfig modConfig, string version, byte[] downloadData, string downloadedFileName)
    {
        var gameRootPath = _settingsService.Settings.GameRootPath;
        var modsPath = Path.Combine(gameRootPath, "Mods");
        Directory.CreateDirectory(modsPath);

        var tracker = new FileOperationTracker(_loggingService, _settingsService);
        var trackedOps = new TrackedFileOperations(tracker, _loggingService);
        tracker.StartTracking($"mod_install_{modConfig.Id}_{version}", modsPath);

        try
        {
            if (downloadedFileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                var tempDownloadPath = await SaveTempDownloadFileAsync(downloadData, downloadedFileName);
                var targetPath = Path.Combine(modsPath, downloadedFileName);
                await trackedOps.CopyFileAsync(tempDownloadPath, targetPath);
                File.Delete(tempDownloadPath);
            }
            else
            {
                await trackedOps.ExtractZipAsync(downloadData, modsPath);
            }
        }
        finally
        {
            tracker.StopTracking();
        }

        return await BuildInstallInfoAsync(modConfig.Id, version, tracker.GetResult().GetAllProcessedFiles());
    }

    private async Task<QuickReinstallResult> TryQuickReinstallAsync(ModConfig modConfig, string version)
    {
        var plan = await ReadUninstalledBackupPlanAsync(modConfig.Id, version);
        if (plan == null)
            return new QuickReinstallResult(null, false);

        if (IsReplaceMode(modConfig))
        {
            if (!await EnsureReplaceBackupStateIsCleanAsync(modConfig))
                return new QuickReinstallResult(null, true);

            return new QuickReinstallResult(
                await RestoreReplaceFilesFromPlanAsync(modConfig.Id, version, plan),
                false);
        }

        var conflictingFiles = await GetDirectRestoreConflictsAsync(plan);
        if (conflictingFiles.Any())
        {
            ShowDirectRestoreConflictMessage(modConfig, conflictingFiles);
            return new QuickReinstallResult(null, true);
        }

        return new QuickReinstallResult(
            await RestoreDirectFilesFromPlanAsync(modConfig.Id, version, plan),
            false);
    }

    private async Task<bool> RefreshUninstalledBackupAsync(ModInstallInfo installInfo)
    {
        var installedPaths = GetInstalledRelativePaths(installInfo);
        if (!installedPaths.Any())
            return false;

        await _modBackupService.DeleteModBackupAsync(installInfo.ModId, installInfo.Version);
        return await _modBackupService.UninstallModWithBackupAsync(installInfo.ModId, installInfo.Version, installedPaths);
    }

    private async Task<List<InstalledFileInfo>> BuildInstalledFileInfosAsync(IEnumerable<string> relativePaths)
    {
        var gameRootPath = _settingsService.Settings.GameRootPath;
        var installedFiles = new List<InstalledFileInfo>();

        foreach (var relativePath in relativePaths
            .Select(NormalizeRelativePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var fullPath = Path.Combine(gameRootPath, relativePath);
            if (!File.Exists(fullPath))
                continue;

            var fileInfo = new FileInfo(fullPath);
            installedFiles.Add(new InstalledFileInfo
            {
                RelativePath = relativePath,
                Sha256 = await CalculateFileSha256Async(fullPath),
                FileSize = fileInfo.Length
            });
        }

        return installedFiles;
    }

    private async Task<string> CalculateFileSha256Async(string filePath)
    {
        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var hashBytes = await sha256.ComputeHashAsync(stream);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    private async Task<bool> MigrateManifestIfNeededAsync()
    {
        bool changed = _installManifest.SchemaVersion < ModInstallManifest.CurrentSchemaVersion;

        foreach (var installInfo in _installManifest.InstalledMods.Values)
        {
            installInfo.BackupFiles ??= new List<string>();

            foreach (var backupFile in installInfo.BackupFiles.ToList())
            {
                var normalizedBackup = NormalizeRelativePath(backupFile);
                if (!string.Equals(backupFile, normalizedBackup, StringComparison.Ordinal))
                {
                    changed = true;
                    installInfo.BackupFiles.Remove(backupFile);
                    installInfo.BackupFiles.Add(normalizedBackup);
                }
            }

            if (!installInfo.InstalledFiles.Any())
                continue;

            var disabledPlan = HasDisabledBackup(installInfo)
                ? await ReadDisabledBackupPlanAsync(installInfo.ModId)
                : null;

            foreach (var file in installInfo.InstalledFiles)
            {
                var normalizedPath = NormalizeRelativePath(file.RelativePath);
                if (!string.Equals(file.RelativePath, normalizedPath, StringComparison.Ordinal))
                {
                    file.RelativePath = normalizedPath;
                    changed = true;
                }

                if (!string.IsNullOrWhiteSpace(file.Sha256) && file.FileSize > 0)
                    continue;

                var gameRootPath = _settingsService.Settings.GameRootPath;
                var fullPath = Path.Combine(gameRootPath, file.RelativePath);
                if (File.Exists(fullPath))
                {
                    file.Sha256 = await CalculateFileSha256Async(fullPath);
                    file.FileSize = new FileInfo(fullPath).Length;
                    changed = true;
                    continue;
                }

                var disabledEntry = disabledPlan?.Files.FirstOrDefault(entry =>
                    string.Equals(entry.RelativePath, file.RelativePath, StringComparison.OrdinalIgnoreCase));
                if (disabledEntry != null && File.Exists(disabledEntry.BackupFilePath))
                {
                    file.Sha256 = await CalculateFileSha256Async(disabledEntry.BackupFilePath);
                    file.FileSize = new FileInfo(disabledEntry.BackupFilePath).Length;
                    changed = true;
                }
            }
        }

        if (changed)
        {
            _installManifest.SchemaVersion = ModInstallManifest.CurrentSchemaVersion;
            await SaveManifestAsync();
        }

        return changed;
    }

    private void NormalizeModConfigs(List<ModConfig> configs)
    {
        foreach (var config in configs)
        {
            if (string.IsNullOrWhiteSpace(config.ReplaceTargetPath))
                config.ReplaceTargetPath = null;

            if (string.IsNullOrWhiteSpace(config.ReplaceFileName))
                config.ReplaceFileName = null;
        }
    }

    private async Task<BackupRestorePlan?> ReadUninstalledBackupPlanAsync(string modId, string version)
    {
        return await ReadBackupPlanAsync(GetUninstalledBackupPath(modId, version), modId, version, includeVersionManifest: true);
    }

    private async Task<BackupRestorePlan?> ReadDisabledBackupPlanAsync(string modId)
    {
        return await ReadBackupPlanAsync(GetDisabledBackupPath(modId), modId, string.Empty, includeVersionManifest: false);
    }

    private async Task<BackupRestorePlan?> ReadBackupPlanAsync(string backupDirectory, string modId, string version, bool includeVersionManifest)
    {
        if (!Directory.Exists(backupDirectory))
            return null;

        Dictionary<string, string> relativePathMap = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> sha256Map = new(StringComparer.OrdinalIgnoreCase);

        if (includeVersionManifest)
        {
            var manifestPath = Path.Combine(backupDirectory, BackupManifestFileName);
            if (File.Exists(manifestPath))
            {
                var manifestJson = await File.ReadAllTextAsync(manifestPath);
                var manifest = JsonConvert.DeserializeObject<ModBackupManifestData>(manifestJson);
                if (manifest?.FilePathMapping != null)
                {
                    foreach (var item in manifest.FilePathMapping)
                        relativePathMap[item.Key] = NormalizeRelativePath(item.Value);
                }

                if (manifest?.FileSha256Mapping != null)
                {
                    foreach (var item in manifest.FileSha256Mapping)
                        sha256Map[item.Key] = item.Value;
                }
            }
        }

        var pathsFile = Path.Combine(backupDirectory, BackupPathsFileName);
        if (File.Exists(pathsFile))
        {
            var pathsJson = await File.ReadAllTextAsync(pathsFile);
            var pathMapping = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(pathsJson) ?? new Dictionary<string, string>();
            foreach (var item in pathMapping)
                relativePathMap[item.Key] = NormalizeRelativePath(item.Value);
        }

        var plan = new BackupRestorePlan
        {
            ModId = modId,
            Version = version
        };

        foreach (var file in Directory.GetFiles(backupDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileName(file);
            if (fileName == BackupManifestFileName || fileName == BackupPathsFileName)
                continue;

            if (!relativePathMap.TryGetValue(fileName, out var relativePath))
                relativePath = NormalizeRelativePath(fileName.Replace('_', Path.DirectorySeparatorChar));

            plan.Files.Add(new BackupRestoreEntry
            {
                BackupFilePath = file,
                RelativePath = relativePath,
                Sha256 = sha256Map.GetValueOrDefault(fileName, string.Empty)
            });
        }

        return plan.Files.Count > 0 ? plan : null;
    }

    private async Task<ModInstallInfo?> RestoreDirectFilesFromPlanAsync(string modId, string version, BackupRestorePlan plan)
    {
        var restoredPaths = new List<string>();
        var gameRootPath = _settingsService.Settings.GameRootPath;

        foreach (var entry in plan.Files)
        {
            var targetPath = Path.Combine(gameRootPath, entry.RelativePath);
            var targetDir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(targetDir))
                Directory.CreateDirectory(targetDir);

            File.Copy(entry.BackupFilePath, targetPath, true);
            restoredPaths.Add(entry.RelativePath);
        }

        return await BuildInstallInfoAsync(modId, version, restoredPaths);
    }

    private async Task<ModInstallInfo?> RestoreReplaceFilesFromPlanAsync(string modId, string version, BackupRestorePlan plan)
    {
        var backupFiles = new List<string>();
        var restoredPaths = new List<string>();
        var gameRootPath = _settingsService.Settings.GameRootPath;

        foreach (var entry in plan.Files)
        {
            var targetPath = Path.Combine(gameRootPath, entry.RelativePath);
            var backupRelative = await BackupExistingFileAsync(modId, entry.RelativePath);
            if (!string.IsNullOrWhiteSpace(backupRelative))
                backupFiles.Add(backupRelative);

            var targetDir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(targetDir))
                Directory.CreateDirectory(targetDir);

            File.Copy(entry.BackupFilePath, targetPath, true);
            restoredPaths.Add(entry.RelativePath);
        }

        return await BuildInstallInfoAsync(modId, version, restoredPaths, backupFiles);
    }

    private async Task<List<string>> GetDirectRestoreConflictsAsync(BackupRestorePlan plan)
    {
        var conflicts = new List<string>();
        var gameRootPath = _settingsService.Settings.GameRootPath;

        foreach (var entry in plan.Files)
        {
            var targetPath = Path.Combine(gameRootPath, entry.RelativePath);
            if (!File.Exists(targetPath))
                continue;

            var expectedSha256 = entry.Sha256;
            if (string.IsNullOrWhiteSpace(expectedSha256) && File.Exists(entry.BackupFilePath))
            {
                expectedSha256 = await CalculateFileSha256Async(entry.BackupFilePath);
                entry.Sha256 = expectedSha256;
            }

            if (string.IsNullOrWhiteSpace(expectedSha256))
                continue;

            var currentSha256 = await CalculateFileSha256Async(targetPath);
            if (!string.Equals(currentSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
                conflicts.Add(NormalizeRelativePath(entry.RelativePath));
        }

        return conflicts;
    }

    private void ShowDirectRestoreConflictMessage(ModConfig modConfig, List<string> conflictingFiles)
    {
        var displayFiles = conflictingFiles
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToList();
        var fileList = string.Join(Environment.NewLine, displayFiles.Select(path => $"- {path}"));
        var hasMore = conflictingFiles.Count > displayFiles.Count;
        if (hasMore)
        {
            fileList += Environment.NewLine + string.Format(
                _settingsService.Settings.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
                    ? "……以及另外 {0} 个文件"
                    : "...and {0} more files",
                conflictingFiles.Count - displayFiles.Count);
        }

        var message = string.Format(
            GetLocalizedResourceText(
                "DirectRestoreConflictDetectedMessage",
                _settingsService.Settings.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
                    ? "检测到 {0} 的快速重装目标位置已存在不同文件，已取消本次快速重装。\n\n冲突文件：\n{1}\n\n建议改为重新下载安装，或先手动处理这些文件。"
                    : "Quick reinstall for {0} was canceled because different files already exist at the target paths.\n\nConflicting files:\n{1}\n\nPlease redownload and reinstall the mod, or handle these files manually first."),
            GetLocalizedName(modConfig),
            fileList);

        MessageDialogHelper.ShowWarningAsync(message, Strings.Warning).Wait();
    }

    private Task<string?> BackupExistingFileAsync(string modId, string relativePath)
    {
        var gameRootPath = _settingsService.Settings.GameRootPath;
        var normalizedRelative = NormalizeRelativePath(relativePath);
        var targetPath = Path.Combine(gameRootPath, normalizedRelative);
        if (!File.Exists(targetPath))
            return Task.FromResult<string?>(null);

        var backupPath = targetPath + GetBackupSuffix(modId);
        if (File.Exists(backupPath))
            throw new IOException($"Backup file already exists: {backupPath}");

        File.Move(targetPath, backupPath);
        return Task.FromResult<string?>(NormalizeRelativePath(Path.GetRelativePath(gameRootPath, backupPath)));
    }

    private Task RestoreImmediateBackupFilesAsync(string modId, IEnumerable<string> backupFiles)
    {
        var suffix = GetBackupSuffix(modId);
        var gameRootPath = _settingsService.Settings.GameRootPath;

        foreach (var backupRelativePath in backupFiles
            .Select(NormalizeRelativePath)
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var backupFullPath = Path.Combine(gameRootPath, backupRelativePath);
            if (!File.Exists(backupFullPath))
                continue;

            var originalRelativePath = backupRelativePath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                ? backupRelativePath.Substring(0, backupRelativePath.Length - suffix.Length)
                : backupRelativePath;
            var originalFullPath = Path.Combine(gameRootPath, originalRelativePath);

            var targetDir = Path.GetDirectoryName(originalFullPath);
            if (!string.IsNullOrEmpty(targetDir))
                Directory.CreateDirectory(targetDir);

            if (File.Exists(originalFullPath))
                File.Delete(originalFullPath);

            File.Move(backupFullPath, originalFullPath);
        }

        return Task.CompletedTask;
    }

    private Task DeleteManagedFilesAsync(IEnumerable<string> relativePaths)
    {
        var gameRootPath = _settingsService.Settings.GameRootPath;
        foreach (var relativePath in relativePaths)
        {
            var fullPath = Path.Combine(gameRootPath, NormalizeRelativePath(relativePath));
            if (File.Exists(fullPath))
                File.Delete(fullPath);
        }

        CleanupEmptyDirectories(relativePaths);
        return Task.CompletedTask;
    }

    private void CleanupEmptyDirectories(IEnumerable<string> relativePaths)
    {
        var gameRootPath = _settingsService.Settings.GameRootPath;
        var directories = relativePaths
            .Select(path => Path.GetDirectoryName(Path.Combine(gameRootPath, NormalizeRelativePath(path))))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(path => path!.Length)
            .ToList();

        foreach (var directory in directories)
        {
            if (string.IsNullOrWhiteSpace(directory))
                continue;

            if (!directory.StartsWith(gameRootPath, StringComparison.OrdinalIgnoreCase))
                continue;

            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
        }
    }

    private bool TryGetReplaceBaseRelativePath(ModConfig modConfig, out string relativeBasePath)
    {
        relativeBasePath = string.Empty;
        if (string.IsNullOrWhiteSpace(modConfig.ReplaceTargetPath))
            return false;

        var normalized = NormalizeRelativePath(modConfig.ReplaceTargetPath);
        if (Path.IsPathRooted(normalized) || normalized.Contains(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return false;

        relativeBasePath = normalized;
        return true;
    }

    private string CombineReplaceRelativePath(string baseRelativePath, string childRelativePath)
    {
        var normalizedChild = NormalizeRelativePath(childRelativePath);
        return NormalizeRelativePath(Path.Combine(baseRelativePath, normalizedChild));
    }

    private Task<bool> EnsureReplaceBackupStateIsCleanAsync(ModConfig modConfig)
    {
        if (!IsReplaceMode(modConfig))
            return Task.FromResult(true);

        if (!TryGetReplaceBaseRelativePath(modConfig, out var baseRelativePath))
            return Task.FromResult(false);

        var gameRootPath = _settingsService.Settings.GameRootPath;
        var targetDirectory = Path.Combine(gameRootPath, baseRelativePath);
        if (!Directory.Exists(targetDirectory))
            return Task.FromResult(true);

        var staleBackups = Directory.GetFiles(targetDirectory, $"*{GetBackupSuffix(modConfig.Id)}", SearchOption.AllDirectories).ToList();
        if (!staleBackups.Any())
            return Task.FromResult(true);

        var message = string.Format(
            GetLocalizedResourceText(
                "ReplaceLeftoverBackupDetectedMessage",
                _settingsService.Settings.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
                    ? "检测到 {0} 残留的 .GHPCMM 备份文件。\n\n是：打开目录手动处理\n否：删除这些残留备份后继续\n取消：终止当前操作\n\n删除残留备份后，建议通过 Steam 检查游戏完整性。"
                    : "Detected leftover .GHPCMM backup files for {0}.\n\nYes: open the directory for manual handling\nNo: delete the leftover backups and continue\nCancel: abort the current operation\n\nAfter deleting leftover backups, running Steam file verification is recommended."),
            GetLocalizedName(modConfig));

        var result = MessageDialogHelper.ShowAsync(message, Strings.Warning, MessageDialogButton.YesNoCancel, MessageDialogImage.Warning).Result;
        if (result == MessageDialogResult.Yes)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = targetDirectory,
                UseShellExecute = true
            });
            return Task.FromResult(false);
        }

        if (result == MessageDialogResult.Cancel)
            return Task.FromResult(false);

        foreach (var staleBackup in staleBackups)
            File.Delete(staleBackup);

        return Task.FromResult(true);
    }

    private bool ShowReplaceInstallWarningIfNeeded(ModConfig modConfig)
    {
        if (!IsReplaceMode(modConfig))
            return true;

        var message = string.Format(
            GetLocalizedResourceText(
                "ReplaceInstallWarningMessage",
                _settingsService.Settings.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
                    ? "{0} 会直接替换游戏文件。\n\n游戏更新或通过 Steam 检查游戏完整性后，此 Mod 可能会失效或被覆盖。\n\n是否继续安装？"
                    : "{0} directly replaces game files.\n\nThis mod may stop working or be overwritten after a game update or Steam file verification.\n\nDo you want to continue installing it?"),
            GetLocalizedName(modConfig));

        return MessageDialogHelper.ConfirmAsync(message, Strings.Warning).Result;
    }

    private void ShowManagedBackupFailureMessage(string modId)
    {
        var modConfig = _availableMods.FirstOrDefault(m => m.Id == modId);
        var modDisplayName = modConfig != null ? GetLocalizedName(modConfig) : modId;
        var message = string.Format(
            GetLocalizedResourceText(
                "ManagedBackupRequiredMessage",
                _settingsService.Settings.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
                    ? "无法为 {0} 创建所需备份，当前操作已中止以避免数据丢失。"
                    : "Failed to create the required backup for {0}. The current operation was canceled to avoid data loss."),
            modDisplayName);

        MessageDialogHelper.ShowErrorAsync(message, Strings.Error).Wait();
    }

    private async Task<ModInstallInfo?> InstallReplaceModeAsync(ModConfig modConfig, string version, byte[] downloadData, string downloadedFileName)
    {
        if (!TryGetReplaceBaseRelativePath(modConfig, out var baseRelativePath))
        {
            var message = string.Format(
                GetLocalizedResourceText(
                    "InvalidReplaceTargetPathMessage",
                    _settingsService.Settings.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
                        ? "Mod {0} 的 ReplaceTargetPath 无效。"
                        : "Mod {0} has an invalid ReplaceTargetPath."),
                GetLocalizedName(modConfig));
            await MessageDialogHelper.ShowErrorAsync(message, Strings.Error);
            return null;
        }

        if (!await EnsureReplaceBackupStateIsCleanAsync(modConfig))
            return null;

        var gameRootPath = _settingsService.Settings.GameRootPath;
        var installedPaths = new List<string>();
        var backupFiles = new List<string>();

        if (downloadedFileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var zipStream = new MemoryStream(downloadData);
            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);

            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name))
                    continue;

                var targetRelativePath = CombineReplaceRelativePath(baseRelativePath, entry.FullName);
                var backupRelativePath = await BackupExistingFileAsync(modConfig.Id, targetRelativePath);
                if (!string.IsNullOrWhiteSpace(backupRelativePath))
                    backupFiles.Add(backupRelativePath);

                var targetPath = Path.Combine(gameRootPath, targetRelativePath);
                var targetDir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(targetDir))
                    Directory.CreateDirectory(targetDir);

                using var sourceStream = entry.Open();
                using var targetStream = File.Create(targetPath);
                await sourceStream.CopyToAsync(targetStream);
                installedPaths.Add(targetRelativePath);
            }
        }
        else
        {
            var targetFileName = string.IsNullOrWhiteSpace(modConfig.ReplaceFileName)
                ? downloadedFileName
                : modConfig.ReplaceFileName;
            var targetRelativePath = CombineReplaceRelativePath(baseRelativePath, targetFileName);
            var backupRelativePath = await BackupExistingFileAsync(modConfig.Id, targetRelativePath);
            if (!string.IsNullOrWhiteSpace(backupRelativePath))
                backupFiles.Add(backupRelativePath);

            var targetPath = Path.Combine(gameRootPath, targetRelativePath);
            var targetDir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(targetDir))
                Directory.CreateDirectory(targetDir);

            await File.WriteAllBytesAsync(targetPath, downloadData);
            installedPaths.Add(targetRelativePath);
        }

        return await BuildInstallInfoAsync(modConfig.Id, version, installedPaths, backupFiles);
    }

    private async Task<bool> MoveDisabledBackupToUninstalledAsync(ModInstallInfo installInfo)
    {
        var disabledBackupPath = GetDisabledBackupPath(installInfo.ModId);
        var uninstalledBackupPath = GetUninstalledBackupPath(installInfo.ModId, installInfo.Version);
        if (!Directory.Exists(disabledBackupPath))
            return false;

        Directory.CreateDirectory(Path.GetDirectoryName(uninstalledBackupPath)!);
        if (Directory.Exists(uninstalledBackupPath))
            Directory.Delete(uninstalledBackupPath, true);

        Directory.Move(disabledBackupPath, uninstalledBackupPath);

        var filePathMapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pathsFile = Path.Combine(uninstalledBackupPath, BackupPathsFileName);
        if (File.Exists(pathsFile))
        {
            var pathsJson = await File.ReadAllTextAsync(pathsFile);
            filePathMapping = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(pathsJson) ??
                              new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        if (!filePathMapping.Any())
        {
            filePathMapping = Directory.GetFiles(uninstalledBackupPath, "*", SearchOption.TopDirectoryOnly)
                .Where(file => Path.GetFileName(file) != BackupPathsFileName)
                .ToDictionary(
                    file => Path.GetFileName(file),
                    file => NormalizeRelativePath(Path.GetFileName(file).Replace('_', Path.DirectorySeparatorChar)),
                    StringComparer.OrdinalIgnoreCase);
        }

        var backupManifest = new ModBackupManifestData
        {
            ModId = installInfo.ModId,
            Version = installInfo.Version,
            BackupDate = DateTime.Now,
            OriginalFiles = GetInstalledRelativePaths(installInfo),
            FilePathMapping = filePathMapping,
            FileSha256Mapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };

        foreach (var file in Directory.GetFiles(uninstalledBackupPath, "*", SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileName(file);
            if (fileName == BackupPathsFileName || fileName == BackupManifestFileName)
                continue;

            backupManifest.FileSha256Mapping[fileName] = await CalculateFileSha256Async(file);
        }

        var manifestPath = Path.Combine(uninstalledBackupPath, BackupManifestFileName);
        var manifestJson = System.Text.Json.JsonSerializer.Serialize(backupManifest, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(manifestPath, manifestJson);
        return true;
    }

    private Task ShowReplaceEmptyDirectoryWarningIfNeededAsync(ModConfig modConfig)
    {
        if (!IsReplaceMode(modConfig))
            return Task.CompletedTask;

        if (!TryGetReplaceBaseRelativePath(modConfig, out var baseRelativePath))
            return Task.CompletedTask;

        var fullPath = Path.Combine(_settingsService.Settings.GameRootPath, baseRelativePath);
        if (!Directory.Exists(fullPath))
            return Task.CompletedTask;

        if (Directory.EnumerateFileSystemEntries(fullPath).Any())
            return Task.CompletedTask;

        var message = string.Format(
            GetLocalizedResourceText(
                "ReplaceTargetDirectoryEmptyMessage",
                _settingsService.Settings.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
                    ? "{0} 卸载后目标目录已为空：\n{1}\n\n如该目录原本属于游戏原版内容，建议通过 Steam 检查游戏完整性。"
                    : "{0} left its target directory empty after uninstall:\n{1}\n\nIf the directory originally belonged to the base game, Steam file verification is recommended."),
            GetLocalizedName(modConfig),
            fullPath);
        MessageDialogHelper.ShowWarningAsync(message, Strings.Warning).Wait();
        return Task.CompletedTask;
    }

    private async Task<bool> DisableReplaceModAsync(ModInstallInfo installInfo)
    {
        var modFiles = GetInstalledRelativePaths(installInfo);
        var result = await _modBackupService.DisableModWithBackupAsync(installInfo.ModId, modFiles);
        if (!result)
            return false;

        await RestoreImmediateBackupFilesAsync(installInfo.ModId, installInfo.BackupFiles);
        CleanupEmptyDirectories(modFiles);
        return true;
    }

    private async Task<ModInstallInfo?> EnableReplaceModAsync(ModConfig modConfig, ModInstallInfo installInfo)
    {
        if (!await EnsureReplaceBackupStateIsCleanAsync(modConfig))
            return null;

        var plan = await ReadDisabledBackupPlanAsync(installInfo.ModId);
        if (plan == null)
            return null;

        var enabledInstallInfo = await RestoreReplaceFilesFromPlanAsync(installInfo.ModId, installInfo.Version, plan);
        if (enabledInstallInfo == null)
            return null;

        var disabledBackupPath = GetDisabledBackupPath(installInfo.ModId);
        if (Directory.Exists(disabledBackupPath))
            Directory.Delete(disabledBackupPath, true);

        return enabledInstallInfo;
    }

    public async Task<List<ModIntegrityIssue>> CheckManagedModsIntegrityAsync()
    {
        await LoadManifestAsync();
        if (_availableMods.Count == 0)
            await LoadAvailableModsAsync();

        var issues = new List<ModIntegrityIssue>();
        var gameRootPath = _settingsService.Settings.GameRootPath;

        foreach (var kvp in _installManifest.InstalledMods)
        {
            var installInfo = kvp.Value;
            if (HasDisabledBackup(installInfo))
                continue;

            var displayName = _availableMods.FirstOrDefault(mod => mod.Id == kvp.Key) is { } modConfig
                ? GetLocalizedName(modConfig)
                : kvp.Key;

            foreach (var file in installInfo.InstalledFiles)
            {
                var fullPath = Path.Combine(gameRootPath, NormalizeRelativePath(file.RelativePath));
                if (!File.Exists(fullPath))
                {
                    issues.Add(new ModIntegrityIssue
                    {
                        ModId = kvp.Key,
                        ModDisplayName = displayName,
                        RelativePath = NormalizeRelativePath(file.RelativePath),
                        IssueType = ModIntegrityIssueType.Missing,
                        ExpectedSha256 = file.Sha256
                    });
                        continue;
                }

                if (string.IsNullOrWhiteSpace(file.Sha256))
                    continue;

                var actualSha256 = await CalculateFileSha256Async(fullPath);
                if (!string.Equals(actualSha256, file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add(new ModIntegrityIssue
                    {
                        ModId = kvp.Key,
                        ModDisplayName = displayName,
                        RelativePath = NormalizeRelativePath(file.RelativePath),
                        IssueType = ModIntegrityIssueType.Modified,
                        ExpectedSha256 = file.Sha256,
                        ActualSha256 = actualSha256
                    });
                }
            }
        }

        return issues;
    }
}
