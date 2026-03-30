using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GHPC_Mod_Manager.Models;
using GHPC_Mod_Manager.Resources;
using GHPC_Mod_Manager.Services;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Windows;
using GHPC_Mod_Manager.Helpers;

namespace GHPC_Mod_Manager.ViewModels;

public partial class ModInfoDumperViewModel : ObservableObject
{
    private readonly ISettingsService _settingsService;
    private readonly IModManagerService _modManagerService;
    private readonly IMelonLoaderService _melonLoaderService;
    private readonly ILoggingService _loggingService;

    [ObservableProperty]
    private bool _isOperating = false;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    public ModInfoDumperViewModel(
        ISettingsService settingsService,
        IModManagerService modManagerService,
        IMelonLoaderService melonLoaderService,
        ILoggingService loggingService)
    {
        _settingsService = settingsService;
        _modManagerService = modManagerService;
        _melonLoaderService = melonLoaderService;
        _loggingService = loggingService;
    }

    /// <summary>
    /// 生成已安装Mod的信息文本
    /// </summary>
    [RelayCommand]
    private async Task CopyModInfoAsync()
    {
        if (IsOperating) return;

        IsOperating = true;
        StatusMessage = Strings.GeneratingModInfo;

        try
        {
            var modInfoText = await GenerateModInfoTextAsync();

            if (string.IsNullOrEmpty(modInfoText))
            {
                await MessageDialogHelper.ShowInformationAsync(Strings.NoModsInstalled, Strings.Information);
                return;
            }

            Clipboard.SetText(modInfoText);
            _loggingService.LogInfo(Strings.ModInfoCopied);
            await MessageDialogHelper.ShowSuccessAsync(Strings.ModInfoCopied, Strings.Success);
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, "Failed to generate mod info");
            await MessageDialogHelper.ShowErrorAsync(string.Format(Strings.PackCreatedFailed, ex.Message), Strings.Error);
        }
        finally
        {
            IsOperating = false;
            StatusMessage = string.Empty;
        }
    }

    /// <summary>
    /// 打包所有诊断信息到ZIP文件
    /// </summary>
    [RelayCommand]
    private async Task PackAllInfoAsync()
    {
        if (IsOperating) return;

        // 确认对话框
        if (!await MessageDialogHelper.ConfirmAsync(
            Strings.PackAllInfoConfirmMessage,
            Strings.PackAllInfoConfirmTitle))
            return;

        IsOperating = true;
        StatusMessage = Strings.CreatingPackage;

        try
        {
            var gameRootPath = _settingsService.Settings.GameRootPath;
            if (string.IsNullOrEmpty(gameRootPath))
            {
                await MessageDialogHelper.ShowErrorAsync(Strings.GamePathNotSet, Strings.Error);
                return;
            }

            // 生成Mod信息
            var modInfoText = await GenerateModInfoTextAsync();

            // 创建临时目录
            var tempDir = Path.Combine(_settingsService.TempPath, $"diagnostic_{DateTime.Now:yyyyMMdd_HHmmss}");
            Directory.CreateDirectory(tempDir);

            var collectedFiles = new List<(string SourcePath, string ZipEntryName)>();
            var skippedFiles = new List<string>();

            // 保存Mod信息
            var modInfoPath = Path.Combine(tempDir, "mod_info.txt");
            await File.WriteAllTextAsync(modInfoPath, modInfoText ?? "No mods installed");
            collectedFiles.Add((modInfoPath, "mod_info.txt"));

            // 尝试收集 MelonLoader/Latest.log
            var latestLogPath = Path.Combine(gameRootPath, "MelonLoader", "Latest.log");
            if (File.Exists(latestLogPath))
            {
                var destPath = Path.Combine(tempDir, "Latest.log");
                File.Copy(latestLogPath, destPath, true);
                collectedFiles.Add((destPath, "Latest.log"));
                _loggingService.LogInfo(Strings.ReadingFileInfo, "Latest.log");
            }
            else
            {
                skippedFiles.Add($"MelonLoader/Latest.log - {Strings.FileNotFoundSkip}");
                _loggingService.LogInfo(Strings.FileNotFoundSkip, "MelonLoader/Latest.log");
            }

            // 尝试收集 UserData/MelonPreferences.cfg
            var melonPrefsPath = Path.Combine(gameRootPath, "UserData", "MelonPreferences.cfg");
            if (File.Exists(melonPrefsPath))
            {
                var destPath = Path.Combine(tempDir, "MelonPreferences.cfg");
                File.Copy(melonPrefsPath, destPath, true);
                collectedFiles.Add((destPath, "MelonPreferences.cfg"));
                _loggingService.LogInfo(Strings.ReadingFileInfo, "MelonPreferences.cfg");
            }
            else
            {
                skippedFiles.Add($"UserData/MelonPreferences.cfg - {Strings.FileNotFoundSkip}");
                _loggingService.LogInfo(Strings.FileNotFoundSkip, "UserData/MelonPreferences.cfg");
            }

            // 生成收集日志
            if (skippedFiles.Count > 0)
            {
                var collectionLogPath = Path.Combine(tempDir, "collection_log.txt");
                var collectionLog = new System.Text.StringBuilder();
                collectionLog.AppendLine("=== Collection Log ===");
                collectionLog.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                collectionLog.AppendLine();
                collectionLog.AppendLine("Skipped Files:");
                foreach (var skipped in skippedFiles)
                {
                    collectionLog.AppendLine($"  - {skipped}");
                }
                await File.WriteAllTextAsync(collectionLogPath, collectionLog.ToString());
                collectedFiles.Add((collectionLogPath, "collection_log.txt"));
            }

            // 创建ZIP文件
            var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            var zipFileName = $"GHPC_Mod_Diagnostic_{DateTime.Now:yyyyMMdd_HHmmss}.zip";
            var zipFilePath = Path.Combine(desktopPath, zipFileName);

            _loggingService.LogInfo(Strings.PackagingFiles);

            using (var zipStream = new FileStream(zipFilePath, FileMode.Create))
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
            {
                foreach (var (sourcePath, entryName) in collectedFiles)
                {
                    var entry = archive.CreateEntry(entryName);
                    using var entryStream = entry.Open();
                    using var fileStream = File.OpenRead(sourcePath);
                    await fileStream.CopyToAsync(entryStream);
                }
            }

            // 清理临时目录
            try
            {
                Directory.Delete(tempDir, true);
            }
            catch
            {
                // 忽略清理失败
            }

            _loggingService.LogInfo(Strings.PackCreatedSuccess, zipFilePath);
            await MessageDialogHelper.ShowSuccessAsync(string.Format(Strings.PackCreatedSuccess, zipFilePath), Strings.Success);
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, "Failed to create diagnostic package");
            await MessageDialogHelper.ShowErrorAsync(string.Format(Strings.PackCreatedFailed, ex.Message), Strings.Error);
        }
        finally
        {
            IsOperating = false;
            StatusMessage = string.Empty;
        }
    }

    /// <summary>
    /// 生成Mod信息的文本内容
    /// </summary>
    private async Task<string?> GenerateModInfoTextAsync()
    {
        var gameRootPath = _settingsService.Settings.GameRootPath;
        if (string.IsNullOrEmpty(gameRootPath)) return null;

        var sb = new System.Text.StringBuilder();

        // 标题
        sb.AppendLine("=== GHPC Mod Manager - Installed Mods Info ===");
        sb.AppendLine($"{Strings.ModInfoGenerated} {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        // 游戏版本
        var gameVersion = await _melonLoaderService.GetCurrentGameVersionAsync(gameRootPath);
        sb.AppendLine($"{Strings.ModInfoGameVersion} {gameVersion ?? Strings.Unknown}");

        // MelonLoader版本
        var mlVersion = await _melonLoaderService.GetInstalledVersionAsync(gameRootPath);
        sb.AppendLine($"{Strings.ModInfoMelonLoaderVersion} {mlVersion ?? Strings.Unknown}");

        sb.AppendLine();

        // 获取已安装的Mod列表
        var mods = await _modManagerService.GetModListAsync();
        var installedMods = mods.Where(m => m.IsInstalled).ToList();

        sb.AppendLine($"{Strings.ModInfoTotalMods} {installedMods.Count}");
        sb.AppendLine();
        sb.AppendLine($"--- {Strings.ModInfoModList} ---");
        sb.AppendLine();

        var modIndex = 1;
        foreach (var mod in installedMods)
        {
            sb.AppendLine($"[{modIndex}] {mod.DisplayName}");

            // 状态
            var status = mod.IsEnabled ? Strings.ModInfoEnabled : Strings.ModInfoDisabled;
            if (mod.IsManuallyInstalled)
            {
                status += $" ({Strings.ModInfoManual})";
            }
            sb.AppendLine($"    {Strings.ModInfoStatus} {status}");

            // 版本
            sb.AppendLine($"    {Strings.ModInfoVersion} {mod.InstalledVersion ?? Strings.Unknown}");

            // 文件信息（即时读取）
            sb.AppendLine($"    {Strings.ModInfoFiles}");

            var filesInfo = await GetModFilesInfoAsync(mod, gameRootPath);
            foreach (var fileInfo in filesInfo)
            {
                sb.AppendLine($"      - {fileInfo}");
            }

            sb.AppendLine();
            modIndex++;
        }

        return sb.ToString();
    }

    /// <summary>
    /// 获取Mod的文件信息（即时读取文件大小和SHA256）
    /// </summary>
    private async Task<List<string>> GetModFilesInfoAsync(ModViewModel mod, string gameRootPath)
    {
        var result = new List<string>();

        try
        {
            // 从manifest获取已安装文件列表
            var manifestPath = Path.Combine(_settingsService.AppDataPath, "mod_install_manifest.json");
            if (File.Exists(manifestPath))
            {
                var json = await File.ReadAllTextAsync(manifestPath);
                var manifest = Newtonsoft.Json.JsonConvert.DeserializeObject<ModInstallManifest>(json);

                if (manifest?.InstalledMods.TryGetValue(mod.Id, out var installInfo) == true && installInfo.InstalledFiles.Count > 0)
                {
                    foreach (var fileInfo in installInfo.InstalledFiles)
                    {
                        var fullPath = Path.Combine(gameRootPath, fileInfo.RelativePath);

                        if (File.Exists(fullPath))
                        {
                            // 即时读取文件大小
                            var fileSize = new FileInfo(fullPath).Length;
                            var sizeFormatted = FormatFileSize(fileSize);

                            // 即时计算SHA256（如果manifest中没有记录）
                            string sha256;
                            if (!string.IsNullOrEmpty(fileInfo.Sha256))
                            {
                                sha256 = fileInfo.Sha256;
                            }
                            else
                            {
                                _loggingService.LogInfo(Strings.CalculatingFileHash, fileInfo.RelativePath);
                                sha256 = await CalculateFileSha256Async(fullPath);
                            }

                            result.Add($"{fileInfo.RelativePath} (SHA256: {sha256}, Size: {sizeFormatted})");
                        }
                        else
                        {
                            result.Add($"{fileInfo.RelativePath} (NOT FOUND)");
                        }
                    }
                    return result;
                }
            }

            // 如果没有manifest记录（手动安装的Mod或记录丢失），只显示主DLL
            if (!string.IsNullOrEmpty(mod.Config.MainBinaryFileName))
            {
                var modsPath = Path.Combine(gameRootPath, "Mods");
                var dllPath = Path.Combine(modsPath, mod.Config.MainBinaryFileName);

                if (File.Exists(dllPath))
                {
                    var fileSize = new FileInfo(dllPath).Length;
                    var sizeFormatted = FormatFileSize(fileSize);

                    _loggingService.LogInfo(Strings.CalculatingFileHash, mod.Config.MainBinaryFileName);
                    var sha256 = await CalculateFileSha256Async(dllPath);

                    result.Add($"Mods/{mod.Config.MainBinaryFileName} (SHA256: {sha256}, Size: {sizeFormatted})");
                }
                else
                {
                    // 检查是否有.disabled后缀
                    var disabledPath = dllPath + ".disabled";
                    if (File.Exists(disabledPath))
                    {
                        var fileSize = new FileInfo(disabledPath).Length;
                        var sizeFormatted = FormatFileSize(fileSize);

                        _loggingService.LogInfo(Strings.CalculatingFileHash, mod.Config.MainBinaryFileName + ".disabled");
                        var sha256 = await CalculateFileSha256Async(disabledPath);

                        result.Add($"Mods/{mod.Config.MainBinaryFileName}.disabled (SHA256: {sha256}, Size: {sizeFormatted})");
                    }
                    else
                    {
                        result.Add($"Mods/{mod.Config.MainBinaryFileName} (NOT FOUND)");
                    }
                }
            }
            else if (mod.Id.StartsWith("manual_"))
            {
                // 不支持的手动Mod
                var fileName = mod.Id.Substring("manual_".Length) + ".dll";
                var modsPath = Path.Combine(gameRootPath, "Mods");

                // 搜索文件
                var foundFiles = Directory.GetFiles(modsPath, fileName, SearchOption.AllDirectories);
                if (foundFiles.Length > 0)
                {
                    foreach (var foundFile in foundFiles)
                    {
                        var relativePath = Path.GetRelativePath(gameRootPath, foundFile);
                        var fileSize = new FileInfo(foundFile).Length;
                        var sizeFormatted = FormatFileSize(fileSize);

                        _loggingService.LogInfo(Strings.CalculatingFileHash, relativePath);
                        var sha256 = await CalculateFileSha256Async(foundFile);

                        result.Add($"{relativePath} (SHA256: {sha256}, Size: {sizeFormatted})");
                    }
                }
                else
                {
                    result.Add($"Mods/{fileName} (NOT FOUND)");
                }
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, "Failed to get mod files info for {0}", mod.Id);
            result.Add($"(Error reading files: {ex.Message})");
        }

        return result;
    }

    /// <summary>
    /// 计算文件的SHA256哈希值
    /// </summary>
    private static async Task<string> CalculateFileSha256Async(string filePath)
    {
        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var hashBytes = await sha256.ComputeHashAsync(stream);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    /// <summary>
    /// 格式化文件大小
    /// </summary>
    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes}B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1}KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1}MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):F1}GB";
    }
}