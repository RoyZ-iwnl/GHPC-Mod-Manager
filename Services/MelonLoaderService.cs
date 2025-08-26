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
            var fileInfo = new FileInfo(tempFile);
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
                        }
                        catch (Exception fileEx)
                        {
                            _loggingService.LogError(fileEx, Strings.FileExtractionFailed, entry.FullName);
                            throw;
                        }
                    }
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
}