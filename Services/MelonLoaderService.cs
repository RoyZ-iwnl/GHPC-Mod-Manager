using GHPC_Mod_Manager.Models;
using System.IO;
using System.IO.Compression;
using GHPC_Mod_Manager.Resources;

namespace GHPC_Mod_Manager.Services;

public interface IMelonLoaderService
{
    Task<bool> IsMelonLoaderInstalledAsync(string gameRootPath);
    Task<bool> AreMelonLoaderDirectoriesCreatedAsync(string gameRootPath);
    Task<List<GitHubRelease>> GetMelonLoaderReleasesAsync();
    Task<bool> InstallMelonLoaderAsync(string gameRootPath, string version, IProgress<DownloadProgress>? progress = null);
    /// <summary>从 MelonLoader/Latest.log 解析当前安装版本号</summary>
    Task<string?> GetInstalledVersionAsync(string gameRootPath);
    /// <summary>禁用 MelonLoader（重命名 version.dll）</summary>
    Task<bool> DisableMelonLoaderAsync(string gameRootPath);
    /// <summary>启用 MelonLoader（还原 version.dll）</summary>
    Task<bool> EnableMelonLoaderAsync(string gameRootPath);
    /// <summary>检查 MelonLoader 是否被禁用</summary>
    bool IsMelonLoaderDisabled(string gameRootPath);
    /// <summary>通过下载当前版本ZIP建立索引，删除旧版本文件</summary>
    Task<bool> UninstallCurrentVersionAsync(string gameRootPath, string currentVersion, IProgress<DownloadProgress>? progress = null);
    /// <summary>从 Latest.log 读取游戏版本号，取最后一个+后的部分，如 20260210.1</summary>
    Task<string?> GetCurrentGameVersionAsync(string gameRootPath);
}

public class MelonLoaderService : IMelonLoaderService
{
    private readonly INetworkService _networkService;
    private readonly ILoggingService _loggingService;
    private readonly ISettingsService _settingsService;

    public MelonLoaderService(INetworkService networkService, ILoggingService loggingService, ISettingsService settingsService)
    {
        _networkService = networkService;
        _loggingService = loggingService;
        _settingsService = settingsService;
    }

    public async Task<bool> IsMelonLoaderInstalledAsync(string gameRootPath)
    {
        return await Task.Run(() =>
        {
            try
            {
                if (string.IsNullOrEmpty(gameRootPath) || !Directory.Exists(gameRootPath))
                    return false;

                var melonLoaderDir = Path.Combine(gameRootPath, "MelonLoader");
                var versionDll = Path.Combine(gameRootPath, "version.dll");
                var dobbyDll = Path.Combine(gameRootPath, "dobby.dll");

                var hasMelonLoaderDir = Directory.Exists(melonLoaderDir);
                var hasVersionDll = File.Exists(versionDll) || File.Exists(dobbyDll);

                return hasMelonLoaderDir && hasVersionDll;
            }
            catch (Exception ex)
            {
                _loggingService.LogError(ex, Strings.MelonLoaderCheckError);
                return false;
            }
        });
    }

    public async Task<bool> AreMelonLoaderDirectoriesCreatedAsync(string gameRootPath)
    {
        return await Task.Run(() =>
        {
            try
            {
                var userDataDir = Path.Combine(gameRootPath, "UserData");
                var modsDir = Path.Combine(gameRootPath, "Mods");

                return Directory.Exists(userDataDir) && Directory.Exists(modsDir);
            }
            catch (Exception ex)
            {
                _loggingService.LogError(ex, Strings.MelonLoaderDirCheckError);
                return false;
            }
        });
    }

    public async Task<List<GitHubRelease>> GetMelonLoaderReleasesAsync()
    {
        try
        {
            return await _networkService.GetGitHubReleasesAsync("LavaGang", "MelonLoader");
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.MelonLoaderReleasesFetchError);
            return new List<GitHubRelease>();
        }
    }

    public async Task<bool> InstallMelonLoaderAsync(string gameRootPath, string version, IProgress<DownloadProgress>? progress = null)
    {
        try
        {
            _loggingService.LogInfo(Strings.InstallingMelonLoader, version);
            _loggingService.LogInfo(Strings.GameRootDirectoryPath, gameRootPath);

            var releases = await GetMelonLoaderReleasesAsync();
            var targetRelease = releases.FirstOrDefault(r => r.TagName == version);
            
            if (targetRelease == null)
            {
                _loggingService.LogError(Strings.MelonLoaderVersionNotFound, version);
                return false;
            }

            var asset = targetRelease.Assets.FirstOrDefault(a => a.Name.Contains("MelonLoader.x64.zip"));
            if (asset == null)
            {
                _loggingService.LogError(Strings.MelonLoaderAssetNotFound, version);
                return false;
            }

            var tempFile = Path.Combine(_settingsService.TempPath, $"MelonLoader_{version}.zip");
            _loggingService.LogInfo(Strings.TempFilePath, tempFile);
            
            // 确保临时目录存在
            Directory.CreateDirectory(_settingsService.TempPath);
            
            var downloadData = await _networkService.DownloadFileAsync(asset.DownloadUrl, progress);
            _loggingService.LogInfo(Strings.FileDownloadCompleted, downloadData.Length);
            
            // 验证下载的数据不为空
            if (downloadData == null || downloadData.Length == 0)
            {
                _loggingService.LogError(Strings.DownloadedFileDataEmpty);
                return false;
            }
            
            // 检查是否是有效的 ZIP 文件头
            if (downloadData.Length < 4 || downloadData[0] != 0x50 || downloadData[1] != 0x4B)
            {
                _loggingService.LogError(Strings.InvalidZipFileHeader, BitConverter.ToString(downloadData.Take(8).ToArray()));
                return false;
            }
            
            await File.WriteAllBytesAsync(tempFile, downloadData);
            
            if (!File.Exists(tempFile))
            {
                _loggingService.LogError(Strings.TempFileNotExists, tempFile);
                return false;
            }
            
            // 验证写入的文件大小
            var fileInfo = new System.IO.FileInfo(tempFile);
            if (fileInfo.Length != downloadData.Length)
            {
                _loggingService.LogError(Strings.FileSizeMismatch, downloadData.Length, fileInfo.Length);
                return false;
            }
            
            _loggingService.LogInfo(Strings.FileVerificationSuccess, fileInfo.Length);

            try
            {
                using (var archive = ZipFile.OpenRead(tempFile))
                {
                    _loggingService.LogInfo(Strings.ZipFileOpenSuccess, archive.Entries.Count);

                    if (archive.Entries.Count == 0)
                    {
                        _loggingService.LogError(Strings.ZipFileEmpty);
                        return false;
                    }

                    // 收集文件列表，安装后保存为 manifest
                    var installedFiles = new List<string>();

                    foreach (var entry in archive.Entries)
                    {
                        if (string.IsNullOrEmpty(entry.Name))
                        {
                            _loggingService.LogInfo(Strings.SkippingDirectory, entry.FullName);
                            continue;
                        }

                        var destinationPath = Path.Combine(gameRootPath, entry.FullName);
                        _loggingService.LogInfo(Strings.ExtractingFile, entry.FullName, destinationPath);

                        var destinationDir = Path.GetDirectoryName(destinationPath);
                        if (!string.IsNullOrEmpty(destinationDir))
                        {
                            Directory.CreateDirectory(destinationDir);
                        }

                        try
                        {
                            entry.ExtractToFile(destinationPath, true);
                            _loggingService.LogInfo(Strings.FileExtractionSuccess, Path.GetFileName(destinationPath));
                            installedFiles.Add(entry.FullName);
                        }
                        catch (Exception fileEx)
                        {
                            _loggingService.LogError(fileEx, Strings.FileExtractionFailed, entry.FullName);
                            throw;
                        }
                    }

                    // 安装完成后保存文件索引
                    await SaveManifestAsync(version, installedFiles);
                }
            }
            catch (Exception ex)
            {
                _loggingService.LogError(ex, Strings.ZipProcessingError, ex.Message);
                return false;
            }

            // 验证安装结果
            var melonLoaderDir = Path.Combine(gameRootPath, "MelonLoader");
            var versionDll = Path.Combine(gameRootPath, "version.dll");
            var dobbyDll = Path.Combine(gameRootPath, "dobby.dll");
            
            _loggingService.LogInfo(Strings.VerifyingInstallationResult);
            _loggingService.LogInfo(Strings.MelonLoaderDirectoryExists, Directory.Exists(melonLoaderDir));
            _loggingService.LogInfo(Strings.VersionDllExists, File.Exists(versionDll));
            _loggingService.LogInfo(Strings.DobbyDllExists, File.Exists(dobbyDll));

            // 清理临时文件
            try
            {
                File.Delete(tempFile);
                _loggingService.LogInfo(Strings.TempFileDeleted);
            }
            catch (Exception cleanupEx)
            {
                _loggingService.LogWarning(Strings.CleanupTempFileFailed, cleanupEx.Message);
            }

            // 验证安装是否成功
            var installSuccess = Directory.Exists(melonLoaderDir) && (File.Exists(versionDll) || File.Exists(dobbyDll));
            
            if (installSuccess)
            {
                _loggingService.LogInfo(Strings.MelonLoaderInstalled, version);
                return true;
            }
            else
            {
                _loggingService.LogError(Strings.MelonLoaderInstallVerificationFailed);
                return false;
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.MelonLoaderInstallError, version);
            return false;
        }
    }

    public async Task<string?> GetInstalledVersionAsync(string gameRootPath)
    {
        return await Task.Run(() =>
        {
            try
            {
                // 优先从 MelonLoader.dll 的 FileVersion 读取，net6 和 net35 都有，取第一个存在的
                var dllCandidates = new[]
                {
                    Path.Combine(gameRootPath, "MelonLoader", "net6", "MelonLoader.dll"),
                    Path.Combine(gameRootPath, "MelonLoader", "net35", "MelonLoader.dll"),
                };

                foreach (var dllPath in dllCandidates)
                {
                    if (!File.Exists(dllPath)) continue;

                    var fvi = System.Diagnostics.FileVersionInfo.GetVersionInfo(dllPath);
                    if (string.IsNullOrEmpty(fvi.FileVersion)) continue;

                    // FileVersion 格式: "0.7.1.0" → 去掉末尾 ".0" → "v0.7.1"
                    var ver = fvi.FileVersion.Trim();
                    if (ver.EndsWith(".0")) ver = ver[..^2];
                    var result = "v" + ver;
                    _loggingService.LogInfo(Strings.MelonLoaderVersionDetected, Path.GetFileName(Path.GetDirectoryName(dllPath)), result);
                    return result;
                }

                // 兜底：从 Latest.log 解析
                var logPath = Path.Combine(gameRootPath, "MelonLoader", "Latest.log");
                if (!File.Exists(logPath))
                {
                    _loggingService.LogInfo(Strings.MelonLoaderVersionNotDetectable);
                    return null;
                }

                foreach (var line in File.ReadLines(logPath))
                {
                    var idx = line.IndexOf("MelonLoader v", StringComparison.Ordinal);
                    if (idx >= 0)
                    {
                        var start = idx + "MelonLoader v".Length;
                        var end = line.IndexOf(' ', start);
                        var result = "v" + (end > start ? line[start..end] : line[start..]);
                        _loggingService.LogInfo(Strings.MelonLoaderVersionFromLog, result);
                        return result;
                    }
                }
                return null;
            }
            catch (Exception ex)
            {
                _loggingService.LogError(ex, Strings.MelonLoaderCheckError);
                return null;
            }
        });
    }

    // 不删除这些目录下的文件，避免误删用户数据
    private static readonly HashSet<string> _protectedPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Mods/", "Mods\\",
        "UserData/", "UserData\\",
        "Plugins/", "Plugins\\",
        "Translation/", "Translation\\",
    };

    public async Task<bool> UninstallCurrentVersionAsync(string gameRootPath, string currentVersion, IProgress<DownloadProgress>? progress = null)
    {
        try
        {
            _loggingService.LogInfo(Strings.MelonLoaderUninstallingCurrentVersion, currentVersion);

            List<string> filesToDelete;

            // 优先读本地 manifest
            var manifest = await LoadManifestAsync();
            if (manifest != null)
            {
                _loggingService.LogInfo(Strings.MelonLoaderManifestLoaded, manifest.Files.Count);
                filesToDelete = manifest.Files
                    .Where(f =>
                    {
                        var p = f.Replace('\\', '/');
                        return !_protectedPrefixes.Any(prefix => p.StartsWith(prefix.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
                    })
                    .Select(f => Path.Combine(gameRootPath, f))
                    .ToList();
            }
            else
            {
                // 没有本地 manifest，下载当前版本 ZIP 建立临时索引
                _loggingService.LogInfo(Strings.MelonLoaderManifestNotFound);

                var releases = await GetMelonLoaderReleasesAsync();
                var targetRelease = releases.FirstOrDefault(r => r.TagName == currentVersion);
                if (targetRelease == null)
                {
                    _loggingService.LogWarning(Strings.MelonLoaderCurrentVersionAssetNotFound, currentVersion);
                    return true;
                }

                var asset = targetRelease.Assets.FirstOrDefault(a => a.Name.Contains("MelonLoader.x64.zip"));
                if (asset == null)
                {
                    _loggingService.LogWarning(Strings.MelonLoaderCurrentVersionAssetNotFound, currentVersion);
                    return true;
                }

                _loggingService.LogInfo(Strings.MelonLoaderDownloadingIndexZip);
                Directory.CreateDirectory(_settingsService.TempPath);
                var indexZipPath = Path.Combine(_settingsService.TempPath, $"MelonLoader_{currentVersion}_index.zip");

                var zipData = await _networkService.DownloadFileAsync(asset.DownloadUrl, progress);
                if (zipData == null || zipData.Length < 4 || zipData[0] != 0x50 || zipData[1] != 0x4B)
                {
                    _loggingService.LogError(Strings.MelonLoaderUninstallFailed);
                    return false;
                }
                await File.WriteAllBytesAsync(indexZipPath, zipData);

                filesToDelete = new List<string>();
                using (var archive = ZipFile.OpenRead(indexZipPath))
                {
                    _loggingService.LogInfo(Strings.MelonLoaderIndexFileCount, archive.Entries.Count);
                    foreach (var entry in archive.Entries)
                    {
                        if (string.IsNullOrEmpty(entry.Name)) continue;
                        var entryPath = entry.FullName.Replace('\\', '/');
                        if (_protectedPrefixes.Any(p => entryPath.StartsWith(p.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)))
                            continue;
                        filesToDelete.Add(Path.Combine(gameRootPath, entry.FullName));
                    }
                }

                try { File.Delete(indexZipPath); } catch { }
            }

            // 按索引删除文件
            int deleted = 0;
            foreach (var filePath in filesToDelete)
            {
                try
                {
                    if (File.Exists(filePath))
                    {
                        _loggingService.LogInfo(Strings.MelonLoaderDeletingFile, Path.GetRelativePath(gameRootPath, filePath));
                        File.Delete(filePath);
                        deleted++;
                    }
                }
                catch (Exception ex)
                {
                    _loggingService.LogWarning(Strings.FileExtractionFailed, filePath, ex.Message);
                }
            }

            _loggingService.LogInfo(Strings.MelonLoaderUninstallComplete, deleted);
            return true;
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.MelonLoaderUninstallFailed);
            return false;
        }
    }

    private string ManifestPath => Path.Combine(_settingsService.AppDataPath, "melonloader_manifest.json");

    private async Task SaveManifestAsync(string version, List<string> files)
    {
        try
        {
            var manifest = new MelonLoaderManifest { Version = version, Files = files };
            var json = Newtonsoft.Json.JsonConvert.SerializeObject(manifest, Newtonsoft.Json.Formatting.Indented);
            await File.WriteAllTextAsync(ManifestPath, json);
            _loggingService.LogInfo(Strings.MelonLoaderManifestSaved, files.Count);
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.MelonLoaderManifestSaveError);
        }
    }

    private async Task<MelonLoaderManifest?> LoadManifestAsync()
    {
        try
        {
            if (!File.Exists(ManifestPath)) return null;
            var json = await File.ReadAllTextAsync(ManifestPath);
            return Newtonsoft.Json.JsonConvert.DeserializeObject<MelonLoaderManifest>(json);
        }
        catch
        {
            return null;
        }
    }

    private const string DisabledDllName = "version.dllGHPCMM";

    public bool IsMelonLoaderDisabled(string gameRootPath)
    {
        return File.Exists(Path.Combine(gameRootPath, DisabledDllName));
    }

    public async Task<string?> GetCurrentGameVersionAsync(string gameRootPath)
    {
        return await Task.Run(() =>
        {
            try
            {
                var logPath = Path.Combine(gameRootPath, "MelonLoader", "Latest.log");
                if (!File.Exists(logPath))
                    return null;

                foreach (var line in File.ReadLines(logPath))
                {
                    // 匹配 "Game Version: 0.1.0-alpha+20260210.1"
                    var idx = line.IndexOf("Game Version: ", StringComparison.Ordinal);
                    if (idx < 0) continue;

                    var versionStr = line[(idx + "Game Version: ".Length)..].Trim();
                    // 取最后一个 + 后的部分
                    var plusIdx = versionStr.LastIndexOf('+');
                    return plusIdx >= 0 ? versionStr[(plusIdx + 1)..] : null;
                }
                return null;
            }
            catch
            {
                return null;
            }
        });
    }

    public async Task<bool> DisableMelonLoaderAsync(string gameRootPath)
    {
        return await Task.Run(() =>
        {
            try
            {
                var src = Path.Combine(gameRootPath, "version.dll");
                var dst = Path.Combine(gameRootPath, DisabledDllName);
                if (!File.Exists(src)) return false;
                File.Move(src, dst, overwrite: true);
                return true;
            }
            catch (Exception ex)
            {
                _loggingService.LogError(ex, Strings.MelonLoaderCheckError);
                return false;
            }
        });
    }

    public async Task<bool> EnableMelonLoaderAsync(string gameRootPath)
    {
        return await Task.Run(() =>
        {
            try
            {
                var src = Path.Combine(gameRootPath, DisabledDllName);
                var dst = Path.Combine(gameRootPath, "version.dll");
                if (!File.Exists(src)) return false;
                File.Move(src, dst, overwrite: true);
                return true;
            }
            catch (Exception ex)
            {
                _loggingService.LogError(ex, Strings.MelonLoaderCheckError);
                return false;
            }
        });
    }
}