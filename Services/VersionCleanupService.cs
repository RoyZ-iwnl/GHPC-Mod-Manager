using GHPC_Mod_Manager.Models;
using GHPC_Mod_Manager.Resources;
using System.IO;
using System.IO.Compression;
using System.Reflection;

namespace GHPC_Mod_Manager.Services;

public interface IVersionCleanupService
{
    /// <summary>
    /// 检查是否需要执行旧版本清理，需要则执行
    /// </summary>
    Task RunIfNeededAsync();
}

public class VersionCleanupService : IVersionCleanupService
{
    private readonly ISettingsService _settingsService;
    private readonly INetworkService _networkService;
    private readonly ILoggingService _loggingService;
    private readonly IUpdateService _updateService;

    public VersionCleanupService(
        ISettingsService settingsService,
        INetworkService networkService,
        ILoggingService loggingService,
        IUpdateService updateService)
    {
        _settingsService = settingsService;
        _networkService = networkService;
        _loggingService = loggingService;
        _updateService = updateService;
    }

    public async Task RunIfNeededAsync()
    {
        var currentVersion = _updateService.GetCurrentVersion().TrimStart('v');
        var doneVersion = _settingsService.Settings.CleanupDoneForVersion?.TrimStart('v') ?? string.Empty;

        // 已经对当前版本做过清理，跳过
        if (string.Equals(doneVersion, currentVersion, StringComparison.OrdinalIgnoreCase))
            return;

        _loggingService.LogInfo(Strings.VersionCleanupStarting, currentVersion);

        try
        {
            await DoCleanupAsync(currentVersion);

            // 标记当前版本已完成清理
            _settingsService.Settings.CleanupDoneForVersion = currentVersion;
            await _settingsService.SaveSettingsAsync();
            _loggingService.LogInfo(Strings.VersionCleanupComplete, currentVersion);
        }
        catch (Exception ex)
        {
            // 清理失败不阻断启动，下次启动会重试
            _loggingService.LogError(ex, Strings.VersionCleanupFailed);
        }
    }

    private async Task DoCleanupAsync(string currentVersion)
    {
        var currentExe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
        var installDir = Path.GetDirectoryName(currentExe) ?? string.Empty;

        if (string.IsNullOrEmpty(installDir))
        {
            _loggingService.LogInfo(Strings.VersionCleanupSkippedNoInstallDir);
            return;
        }

        var tempDir = Path.Combine(_settingsService.TempPath, "version_cleanup");
        Directory.CreateDirectory(tempDir);

        try
        {
            // 获取上一个版本的 release 列表
            var releases = await _networkService.GetGitHubReleasesAsync("RoyZ-iwnl", "GHPC-Mod-Manager", forceRefresh: true);
            if (releases == null || releases.Count == 0)
            {
                _loggingService.LogInfo(Strings.VersionCleanupNoReleasesFound);
                return;
            }

            // 找到上一个版本（排除当前版本，取第一个）
            var prevRelease = releases.FirstOrDefault(r =>
                !string.Equals(r.TagName.TrimStart('v'), currentVersion, StringComparison.OrdinalIgnoreCase));

            if (prevRelease == null)
            {
                _loggingService.LogInfo(Strings.VersionCleanupNoPreviousRelease);
                return;
            }

            var prevVersion = prevRelease.TagName.TrimStart('v');
            _loggingService.LogInfo(Strings.VersionCleanupComparingVersions, prevVersion, currentVersion);

            // 下载上一个版本的 ZIP，建立旧文件索引
            var prevAsset = prevRelease.Assets.FirstOrDefault(a =>
                a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
            if (prevAsset == null)
            {
                _loggingService.LogInfo(Strings.VersionCleanupPrevAssetNotFound, prevVersion);
                return;
            }

            // 下载当前版本的 ZIP，建立新文件索引
            var currentRelease = releases.FirstOrDefault(r =>
                string.Equals(r.TagName.TrimStart('v'), currentVersion, StringComparison.OrdinalIgnoreCase));
            var currentAsset = currentRelease?.Assets.FirstOrDefault(a =>
                a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
            if (currentAsset == null)
            {
                _loggingService.LogInfo(Strings.VersionCleanupCurrentAssetNotFound, currentVersion);
                return;
            }

            _loggingService.LogInfo(Strings.VersionCleanupDownloadingPrev, prevVersion);
            var prevZipPath = Path.Combine(tempDir, $"prev_{prevVersion}.zip");
            var prevData = await _networkService.DownloadFileAsync(prevAsset.DownloadUrl);
            await File.WriteAllBytesAsync(prevZipPath, prevData);

            _loggingService.LogInfo(Strings.VersionCleanupDownloadingCurrent, currentVersion);
            var currZipPath = Path.Combine(tempDir, $"curr_{currentVersion}.zip");
            var currData = await _networkService.DownloadFileAsync(currentAsset.DownloadUrl);
            await File.WriteAllBytesAsync(currZipPath, currData);

            // 建立两个版本的文件名集合（只取文件名，不含路径）
            var prevFiles = GetZipEntryNames(prevZipPath);
            var currFiles = GetZipEntryNames(currZipPath);

            // 旧版本有、新版本没有的文件 = 遗留文件
            var obsoleteFiles = prevFiles.Except(currFiles, StringComparer.OrdinalIgnoreCase).ToList();
            _loggingService.LogInfo(Strings.VersionCleanupObsoleteCount, obsoleteFiles.Count);

            foreach (var relPath in obsoleteFiles)
            {
                var fullPath = Path.Combine(installDir, relPath);
                if (!File.Exists(fullPath))
                    continue;

                try
                {
                    File.Delete(fullPath);
                    _loggingService.LogInfo(Strings.VersionCleanupDeletedFile, relPath);
                }
                catch (Exception ex)
                {
                    _loggingService.LogError(ex, Strings.VersionCleanupDeleteFailed, relPath);
                }
            }
        }
        finally
        {
            // 清理临时文件
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    /// <summary>
    /// 读取 ZIP 内所有条目的相对路径（保留目录结构，统一用正斜杠）
    /// </summary>
    private static HashSet<string> GetZipEntryNames(string zipPath)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries)
        {
            // 跳过纯目录条目
            if (string.IsNullOrEmpty(entry.Name))
                continue;
            result.Add(entry.FullName.Replace('\\', '/'));
        }
        return result;
    }
}
