using GHPC_Mod_Manager.Models;
using Newtonsoft.Json;
using System.IO;
using System.IO.Compression;
using GHPC_Mod_Manager.Resources;

namespace GHPC_Mod_Manager.Services;

public interface ITranslationManagerService
{
    Task<bool> IsTranslationInstalledAsync();
    Task<List<GitHubRelease>> GetXUnityReleasesAsync(bool forceRefresh = false);
    Task<bool> InstallTranslationAsync(string xUnityVersion, IProgress<DownloadProgress>? progress = null);
    Task<bool> UpdateTranslationFilesAsync(IProgress<DownloadProgress>? progress = null);
    Task<bool> UninstallTranslationAsync();
    Task<List<string>> GetAvailableLanguagesAsync();
    Task<string> GetCurrentLanguageAsync();
    Task<bool> SetLanguageAsync(string language);
    Task<bool> IsTranslationPluginEnabledAsync();
    Task<bool> SetTranslationPluginEnabledAsync(bool enabled);
    Task<List<GitHubRelease>> GetTranslationReleasesAsync(bool forceRefresh = false);
}

public class TranslationManagerService : ITranslationManagerService
{
    private readonly INetworkService _networkService;
    private readonly ILoggingService _loggingService;
    private readonly ISettingsService _settingsService;
    private TranslationInstallManifest _installManifest = new();

    public TranslationManagerService(INetworkService networkService, ILoggingService loggingService, ISettingsService settingsService)
    {
        _networkService = networkService;
        _loggingService = loggingService;
        _settingsService = settingsService;
    }

    public async Task<bool> IsTranslationInstalledAsync()
    {
        await LoadManifestAsync();
        
        var gameRootPath = _settingsService.Settings.GameRootPath;
        if (string.IsNullOrEmpty(gameRootPath))
            return false;

        var autoTranslatorPath = Path.Combine(gameRootPath, "Mods", "XUnity.AutoTranslator.Plugin.MelonMod.dll");
        var autoTranslatorBackupPath = Path.Combine(gameRootPath, "Mods", "XUnity.AutoTranslator.Plugin.MelonMod.dllbak");
        var configPath = Path.Combine(gameRootPath, "AutoTranslator", "Config.ini");

        // 翻译系统已安装的条件：配置文件存在 且 (主插件文件存在 或 备份插件文件存在)
        return File.Exists(configPath) && (File.Exists(autoTranslatorPath) || File.Exists(autoTranslatorBackupPath));
    }

    public async Task<List<GitHubRelease>> GetXUnityReleasesAsync(bool forceRefresh = false)
    {
        try
        {
            var releases = await _networkService.GetGitHubReleasesAsync("bbepis", "XUnity.AutoTranslator", forceRefresh);
            
            return releases.Where(r => r.Assets.Any(a => 
                a.Name.Contains("MelonMod") && 
                !a.Name.Contains("IL2CPP") && 
                a.Name.EndsWith(".zip"))).ToList();
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.XUnityReleasesFetchError);
            return new List<GitHubRelease>();
        }
    }

    public async Task<bool> InstallTranslationAsync(string xUnityVersion, IProgress<DownloadProgress>? progress = null)
    {
        try
        {
            _loggingService.LogInfo("debugtemplog: Starting translation installation: XUnity version {0}", xUnityVersion);
            _loggingService.LogInfo(Strings.InstallingTranslation, xUnityVersion);

            var gameRootPath = _settingsService.Settings.GameRootPath;
            
            // 创建文件操作追踪器
            var tracker = new FileOperationTracker(_loggingService, _settingsService);
            var trackedOps = new TrackedFileOperations(tracker, _loggingService);

            // 开始追踪文件操作
            tracker.StartTracking($"translation_install_{xUnityVersion}", gameRootPath);

            var progressSplit = new ProgressSplitter(progress, 2);

            // 1. Install XUnity AutoTranslator
            _loggingService.LogInfo("debugtemplog: Starting XUnity AutoTranslator installation");
            var success = await InstallXUnityAutoTranslatorWithTrackingAsync(xUnityVersion, trackedOps, progressSplit.GetProgress(0));
            if (!success) return false;

            // 2. Clone translation files
            _loggingService.LogInfo("debugtemplog: Starting translation files cloning");
            success = await CloneTranslationFilesWithTrackingAsync(trackedOps, progressSplit.GetProgress(1));
            if (!success) return false;

            // 停止追踪并获取结果
            tracker.StopTracking();
            var result = tracker.GetResult();
            var processedFiles = result.GetAllProcessedFiles();

            _loggingService.LogInfo("debugtemplog: Translation installation completed. Total processed files: {0}", processedFiles.Count);
            foreach (var file in processedFiles)
            {
                _loggingService.LogInfo("debugtemplog: Processed file: {0}", file);
            }

            if (!processedFiles.Any())
            {
                _loggingService.LogError("debugtemplog: No files were processed during translation installation");
                return false;
            }

            // Split files into XUnity and translation repo files based on their paths
            var modsPath = Path.Combine(gameRootPath, "Mods");
            var autoTranslatorPath = Path.Combine(gameRootPath, "AutoTranslator");
            var userLibsPath = Path.Combine(gameRootPath, "UserLibs");

            _installManifest.XUnityAutoTranslatorFiles = processedFiles
                .Where(f => 
                {
                    var fullPath = Path.Combine(gameRootPath, f);
                    return fullPath.StartsWith(modsPath, StringComparison.OrdinalIgnoreCase) || 
                           fullPath.StartsWith(userLibsPath, StringComparison.OrdinalIgnoreCase) ||
                           f.Contains("XUnity") || 
                           Path.GetFileName(f).Equals("Font", StringComparison.OrdinalIgnoreCase);
                })
                .ToList();

            _installManifest.TranslationRepoFiles = processedFiles
                .Where(f => 
                {
                    var fullPath = Path.Combine(gameRootPath, f);
                    return fullPath.StartsWith(autoTranslatorPath, StringComparison.OrdinalIgnoreCase) && 
                           !_installManifest.XUnityAutoTranslatorFiles.Contains(f);
                })
                .ToList();

            _loggingService.LogInfo("debugtemplog: XUnity files: {0}, Translation files: {1}", 
                _installManifest.XUnityAutoTranslatorFiles.Count, _installManifest.TranslationRepoFiles.Count);

            _installManifest.XUnityVersion = xUnityVersion;
            _installManifest.InstallDate = DateTime.Now;
            await SaveManifestAsync();

            _loggingService.LogInfo(Strings.TranslationInstalled);
            return true;
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.TranslationInstallError);
            return false;
        }
    }

    public async Task<List<GitHubRelease>> GetTranslationReleasesAsync(bool forceRefresh = false)
    {
        try
        {
            var translationConfig = await _networkService.GetTranslationConfigAsync(_settingsService.Settings.TranslationConfigUrl);
            
            string owner, repoName;
            
            // 优先使用新的Owner/RepoName字段，如果为空则从RepoUrl解析
            if (!string.IsNullOrEmpty(translationConfig.Owner) && !string.IsNullOrEmpty(translationConfig.RepoName))
            {
                owner = translationConfig.Owner;
                repoName = translationConfig.RepoName;
            }
            else if (!string.IsNullOrEmpty(translationConfig.RepoUrl))
            {
                // 从RepoUrl解析Owner和RepoName
                var parseResult = ParseGitHubRepoUrl(translationConfig.RepoUrl);
                if (parseResult == null)
                {
                    _loggingService.LogError("debugtemplog: Failed to parse GitHub repo URL: {0}", translationConfig.RepoUrl);
                    return new List<GitHubRelease>();
                }
                owner = parseResult.Value.owner;
                repoName = parseResult.Value.repoName;
            }
            else
            {
                _loggingService.LogError("debugtemplog: Translation config Owner/RepoName and RepoUrl are all empty");
                return new List<GitHubRelease>();
            }

            var releases = await _networkService.GetGitHubReleasesAsync(owner, repoName, forceRefresh);
            
            // 筛选符合命名规范的release (release-YYYYMMDD-HHMMSS)
            return releases.Where(r => r.TagName.StartsWith("release-") && 
                                     r.TagName.Length == 23 && // "release-" + "20250922-143025"
                                     r.Assets.Any(a => a.Name.Contains("ghpc-translation-") && 
                                                      (a.Name.EndsWith(".zip") || a.Name.EndsWith(".tar.gz"))))
                          .ToList();
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.TranslationReleasesFetchError);
            return new List<GitHubRelease>();
        }
    }

    /// <summary>
    /// 解析GitHub仓库URL，提取Owner和RepoName
    /// </summary>
    private (string owner, string repoName)? ParseGitHubRepoUrl(string repoUrl)
    {
        try
        {
            // Handle both HTTPS and Git URLs
            string httpsUrl = repoUrl;
            
            // Convert git:// URLs to https://
            if (repoUrl.StartsWith("git://"))
            {
                httpsUrl = repoUrl.Replace("git://", "https://");
            }
            
            // Convert SSH URLs (git@github.com:owner/repo.git) to HTTPS
            if (repoUrl.StartsWith("git@"))
            {
                var match = System.Text.RegularExpressions.Regex.Match(repoUrl, @"git@([^:]+):(.+)\.git$");
                if (match.Success)
                {
                    var host = match.Groups[1].Value;
                    var path = match.Groups[2].Value;
                    httpsUrl = $"https://{host}/{path}";
                }
            }

            var uri = new Uri(httpsUrl);
            if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            {
                _loggingService.LogWarning("debugtemplog: URL is not a GitHub repository: {0}", repoUrl);
                return null;
            }

            // Extract owner and repo from path (e.g., "/RoyZ-iwnl/ghpc-translation")
            var pathParts = uri.AbsolutePath.Trim('/').Split('/');
            if (pathParts.Length < 2)
            {
                _loggingService.LogWarning("debugtemplog: Invalid GitHub repository path: {0}", uri.AbsolutePath);
                return null;
            }

            var owner = pathParts[0];
            var repo = pathParts[1];
            
            // Remove .git suffix if present
            if (repo.EndsWith(".git"))
            {
                repo = repo.Substring(0, repo.Length - 4);
            }

            return (owner, repo);
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, "debugtemplog: Failed to parse GitHub repo URL: {0}", ex.Message);
            return null;
        }
    }

    public async Task<bool> IsTranslationPluginEnabledAsync()
    {
        try
        {
            var gameRootPath = _settingsService.Settings.GameRootPath;
            if (string.IsNullOrEmpty(gameRootPath))
                return false;

            var mainPluginPath = Path.Combine(gameRootPath, "Mods", "XUnity.AutoTranslator.Plugin.MelonMod.dll");
            var backupPluginPath = Path.Combine(gameRootPath, "Mods", "XUnity.AutoTranslator.Plugin.MelonMod.dllbak");

            // 如果主文件存在，插件是启用的
            if (File.Exists(mainPluginPath))
            {
                await LoadManifestAsync();
                _installManifest.IsEnabled = true;
                await SaveManifestAsync();
                return true;
            }

            // 如果备份文件存在，插件是禁用的
            if (File.Exists(backupPluginPath))
            {
                await LoadManifestAsync();
                _installManifest.IsEnabled = false;
                await SaveManifestAsync();
                return false;
            }

            // 都不存在，说明没有安装
            return false;
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, "debugtemplog: Error checking translation plugin status: {0}", ex.Message);
            return false;
        }
    }

    public async Task<bool> SetTranslationPluginEnabledAsync(bool enabled)
    {
        try
        {
            var gameRootPath = _settingsService.Settings.GameRootPath;
            if (string.IsNullOrEmpty(gameRootPath))
                return false;

            var mainPluginPath = Path.Combine(gameRootPath, "Mods", "XUnity.AutoTranslator.Plugin.MelonMod.dll");
            var backupPluginPath = Path.Combine(gameRootPath, "Mods", "XUnity.AutoTranslator.Plugin.MelonMod.dllbak");

            if (enabled)
            {
                // 启用插件：从.dllbak重命名为.dll
                if (File.Exists(backupPluginPath) && !File.Exists(mainPluginPath))
                {
                    File.Move(backupPluginPath, mainPluginPath);
                    _loggingService.LogInfo(Strings.TranslationPluginEnabled);
                }
            }
            else
            {
                // 禁用插件：从.dll重命名为.dllbak
                if (File.Exists(mainPluginPath) && !File.Exists(backupPluginPath))
                {
                    File.Move(mainPluginPath, backupPluginPath);
                    _loggingService.LogInfo(Strings.TranslationPluginDisabled);
                }
            }

            // 更新manifest状态
            await LoadManifestAsync();
            _installManifest.IsEnabled = enabled;
            await SaveManifestAsync();

            return true;
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.TranslationPluginToggleError, ex.Message);
            return false;
        }
    }

    private async Task<bool> InstallXUnityAutoTranslatorWithTrackingAsync(string version, TrackedFileOperations trackedOps, IProgress<DownloadProgress>? progress)
    {
        try
        {
            _loggingService.LogInfo("debugtemplog: Getting XUnity releases for version {0}", version);
            var releases = await GetXUnityReleasesAsync();
            var targetRelease = releases.FirstOrDefault(r => r.TagName == version);
            
            if (targetRelease == null)
            {
                _loggingService.LogError("debugtemplog: XUnity version not found: {0}", version);
                _loggingService.LogError(Strings.XUnityVersionNotFound, version);
                return false;
            }

            var asset = targetRelease.Assets.FirstOrDefault(a => 
                a.Name.Contains("MelonMod") && 
                !a.Name.Contains("IL2CPP") && 
                a.Name.EndsWith(".zip"));

            if (asset == null)
            {
                _loggingService.LogError("debugtemplog: XUnity asset not found for version {0}", version);
                _loggingService.LogError(Strings.XUnityAssetNotFound);
                return false;
            }

            _loggingService.LogInfo("debugtemplog: Downloading XUnity asset: {0}", asset.Name);
            var downloadData = await _networkService.DownloadFileAsync(asset.DownloadUrl, progress);
            _loggingService.LogInfo("debugtemplog: Downloaded XUnity asset: {0} ({1} bytes)", asset.Name, downloadData.Length);
            
            var gameRootPath = _settingsService.Settings.GameRootPath;

            // 使用追踪的ZIP解压操作
            await trackedOps.ExtractZipAsync(downloadData, gameRootPath);
            
            _loggingService.LogInfo("debugtemplog: XUnity AutoTranslator extraction completed using tracked operations");

            return true;
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, "debugtemplog: XUnity install error: {0}", ex.Message);
            _loggingService.LogError(ex, Strings.XUnityInstallError);
            return false;
        }
    }

    private async Task<bool> CloneTranslationFilesWithTrackingAsync(TrackedFileOperations trackedOps, IProgress<DownloadProgress>? progress)
    {
        try
        {
            _loggingService.LogInfo("debugtemplog: Getting translation configuration from {0}", _settingsService.Settings.TranslationConfigUrl);
            var translationConfig = await _networkService.GetTranslationConfigAsync(_settingsService.Settings.TranslationConfigUrl);
            
            string owner, repoName;
            
            // 优先使用新的Owner/RepoName字段，如果为空则从RepoUrl解析
            if (!string.IsNullOrEmpty(translationConfig.Owner) && !string.IsNullOrEmpty(translationConfig.RepoName))
            {
                owner = translationConfig.Owner;
                repoName = translationConfig.RepoName;
            }
            else if (!string.IsNullOrEmpty(translationConfig.RepoUrl))
            {
                // 从RepoUrl解析Owner和RepoName
                var parseResult = ParseGitHubRepoUrl(translationConfig.RepoUrl);
                if (parseResult == null)
                {
                    _loggingService.LogError("debugtemplog: Failed to parse GitHub repo URL: {0}", translationConfig.RepoUrl);
                    _loggingService.LogError(Strings.TranslationRepoUrlEmpty);
                    return false;
                }
                owner = parseResult.Value.owner;
                repoName = parseResult.Value.repoName;
            }
            else
            {
                _loggingService.LogError("debugtemplog: Translation config Owner/RepoName and RepoUrl are all empty");
                _loggingService.LogError(Strings.TranslationRepoUrlEmpty);
                return false;
            }

            // 获取翻译仓库的最新Release
            var releases = await _networkService.GetGitHubReleasesAsync(owner, repoName);
            if (!releases.Any())
            {
                _loggingService.LogError("debugtemplog: No translation releases found");
                _loggingService.LogError(Strings.TranslationReleasesFetchError);
                return false;
            }

            // 筛选符合命名规范的release
            var validReleases = releases.Where(r => r.TagName.StartsWith("release-") && 
                                                   r.TagName.Length == 23 && 
                                                   r.Assets.Any(a => a.Name.Contains("ghpc-translation-") && 
                                                                    (a.Name.EndsWith(".zip") || a.Name.EndsWith(".tar.gz"))))
                                       .ToList();

            if (!validReleases.Any())
            {
                _loggingService.LogError("debugtemplog: No valid translation releases found");
                _loggingService.LogError(Strings.TranslationReleasesFetchError);
                return false;
            }

            var latestRelease = validReleases.First();
            var targetAssetName = !string.IsNullOrEmpty(translationConfig.TargetAssetName) ? translationConfig.TargetAssetName : ".zip";
            var targetAsset = latestRelease.Assets.FirstOrDefault(a => 
                a.Name.Contains("ghpc-translation-") && a.Name.EndsWith(targetAssetName));

            if (targetAsset == null)
            {
                _loggingService.LogError("debugtemplog: Translation asset not found in release {0}", latestRelease.TagName);
                _loggingService.LogError(Strings.TranslationAssetNotFound);
                return false;
            }

            _loggingService.LogInfo("debugtemplog: Downloading translation asset: {0} from release {1}", targetAsset.Name, latestRelease.TagName);
            _loggingService.LogInfo(Strings.DownloadingTranslationRelease, latestRelease.TagName);

            // 下载翻译ZIP文件
            var zipData = await _networkService.DownloadFileAsync(targetAsset.DownloadUrl, progress);
            if (zipData == null || zipData.Length == 0)
            {
                _loggingService.LogError("debugtemplog: Failed to download translation ZIP file");
                return false;
            }

            _loggingService.LogInfo("debugtemplog: Downloaded translation ZIP: {0} bytes", zipData.Length);

            var gameRootPath = _settingsService.Settings.GameRootPath;

            // 使用追踪的ZIP解压操作，排除不需要的文件
            await trackedOps.ExtractZipAsync(zipData, gameRootPath, new[] { ".git", ".gitignore", "README.md", "LICENSE" });
            
            _loggingService.LogInfo("debugtemplog: Translation files extraction completed using tracked operations");

            // 更新翻译配置
            translationConfig.LastUpdated = DateTime.Now;
            var configPath = Path.Combine(_settingsService.AppDataPath, "translationconfig.json");
            var configJson = JsonConvert.SerializeObject(translationConfig, Formatting.Indented);
            await File.WriteAllTextAsync(configPath, configJson);
            _loggingService.LogInfo("debugtemplog: Updated translation config timestamp");

            return true;
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, "debugtemplog: Translation files download error: {0}", ex.Message);
            _loggingService.LogError(ex, Strings.TranslationFilesCloneError);
            return false;
        }
    }

    public async Task<bool> UpdateTranslationFilesAsync(IProgress<DownloadProgress>? progress = null)
    {
        try
        {
            _loggingService.LogInfo("debugtemplog: Starting translation files update");
            _loggingService.LogInfo(Strings.UpdatingTranslationFiles);

            await LoadManifestAsync();

            // Remove existing translation files
            if (_installManifest.TranslationRepoFiles.Any())
            {
                _loggingService.LogInfo("debugtemplog: Removing {0} existing translation files", _installManifest.TranslationRepoFiles.Count);
                var gameRootPath = _settingsService.Settings.GameRootPath;
                foreach (var relativePath in _installManifest.TranslationRepoFiles)
                {
                    var fullPath = Path.Combine(gameRootPath, relativePath);
                    if (File.Exists(fullPath))
                    {
                        File.Delete(fullPath);
                        _loggingService.LogInfo("debugtemplog: Removed existing translation file: {0}", relativePath);
                    }
                }
            }

            // 创建文件操作追踪器用于更新操作
            var gameRootPath2 = _settingsService.Settings.GameRootPath;
            var tracker = new FileOperationTracker(_loggingService, _settingsService);
            var trackedOps = new TrackedFileOperations(tracker, _loggingService);

            // 开始追踪文件操作
            tracker.StartTracking($"translation_update_{DateTime.Now:yyyyMMdd_HHmmss}", gameRootPath2);

            // Re-clone translation files using tracking
            _loggingService.LogInfo("debugtemplog: Re-cloning translation files with tracking");
            var success = await CloneTranslationFilesWithTrackingAsync(trackedOps, progress);
            
            if (success)
            {
                // 停止追踪并获取结果
                tracker.StopTracking();
                var result = tracker.GetResult();
                var processedFiles = result.GetAllProcessedFiles();

                _loggingService.LogInfo("debugtemplog: Translation update completed. Total processed files: {0}", processedFiles.Count);

                // 更新manifest中的翻译文件列表
                var autoTranslatorPath = Path.Combine(gameRootPath2, "AutoTranslator");
                _installManifest.TranslationRepoFiles = processedFiles
                    .Where(f => 
                    {
                        var fullPath = Path.Combine(gameRootPath2, f);
                        return fullPath.StartsWith(autoTranslatorPath, StringComparison.OrdinalIgnoreCase);
                    })
                    .ToList();

                await SaveManifestAsync();
                _loggingService.LogInfo(Strings.TranslationFilesUpdated);
            }

            return success;
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, "debugtemplog: Translation update error: {0}", ex.Message);
            _loggingService.LogError(ex, Strings.TranslationUpdateError);
            return false;
        }
    }

    public async Task<bool> UninstallTranslationAsync()
    {
        try
        {
            _loggingService.LogInfo(Strings.UninstallingTranslation);

            await LoadManifestAsync();
            var gameRootPath = _settingsService.Settings.GameRootPath;

            // Remove all translation files
            var allFiles = _installManifest.XUnityAutoTranslatorFiles
                .Concat(_installManifest.TranslationRepoFiles)
                .ToList();

            foreach (var relativePath in allFiles)
            {
                var fullPath = Path.Combine(gameRootPath, relativePath);
                if (File.Exists(fullPath))
                {
                    File.Delete(fullPath);
                }
            }

            // Remove empty directories
            var directories = allFiles
                .Select(f => Path.GetDirectoryName(Path.Combine(gameRootPath, f)))
                .Where(d => !string.IsNullOrEmpty(d))
                .Distinct()
                .OrderByDescending(d => d!.Length);

            foreach (var directory in directories)
            {
                if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory);
                }
            }

            // Clear manifest
            _installManifest = new TranslationInstallManifest();
            await SaveManifestAsync();

            _loggingService.LogInfo(Strings.TranslationUninstalled);
            return true;
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.TranslationUninstallError);
            return false;
        }
    }

    public async Task<List<string>> GetAvailableLanguagesAsync()
    {
        try
        {
            var gameRootPath = _settingsService.Settings.GameRootPath;
            var translationPath = Path.Combine(gameRootPath, "AutoTranslator", "Translation");

            if (!Directory.Exists(translationPath))
                return new List<string>();

            return Directory.GetDirectories(translationPath)
                .Select(d => Path.GetFileName(d))
                .Where(name => !string.IsNullOrEmpty(name))
                .ToList()!;
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.LanguageListError);
            return new List<string>();
        }
    }

    public async Task<string> GetCurrentLanguageAsync()
    {
        try
        {
            var gameRootPath = _settingsService.Settings.GameRootPath;
            var configPath = Path.Combine(gameRootPath, "AutoTranslator", "Config.ini");

            if (!File.Exists(configPath))
                return "";

            var lines = await File.ReadAllLinesAsync(configPath);
            var inGeneralSection = false;

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                
                if (trimmed == "[General]")
                {
                    inGeneralSection = true;
                    continue;
                }

                if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                {
                    inGeneralSection = false;
                    continue;
                }

                if (inGeneralSection && trimmed.StartsWith("Language="))
                {
                    return trimmed.Substring("Language=".Length).Trim();
                }
            }

            return "";
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.CurrentLanguageError);
            return "";
        }
    }

    public async Task<bool> SetLanguageAsync(string language)
    {
        try
        {
            var gameRootPath = _settingsService.Settings.GameRootPath;
            var configPath = Path.Combine(gameRootPath, "AutoTranslator", "Config.ini");

            if (!File.Exists(configPath))
            {
                _loggingService.LogError(Strings.AutoTranslatorConfigNotFound);
                return false;
            }

            var lines = await File.ReadAllLinesAsync(configPath);
            var newLines = new List<string>();
            var inGeneralSection = false;
            var languageLineFound = false;

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                
                if (trimmed == "[General]")
                {
                    inGeneralSection = true;
                    newLines.Add(line);
                    continue;
                }

                if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                {
                    inGeneralSection = false;
                    newLines.Add(line);
                    continue;
                }

                if (inGeneralSection && trimmed.StartsWith("Language="))
                {
                    newLines.Add($"Language={language}");
                    languageLineFound = true;
                    continue;
                }

                newLines.Add(line);
            }

            // If Language line wasn't found, add it to the General section
            if (!languageLineFound)
            {
                for (int i = 0; i < newLines.Count; i++)
                {
                    if (newLines[i].Trim() == "[General]")
                    {
                        newLines.Insert(i + 1, $"Language={language}");
                        break;
                    }
                }
            }

            await File.WriteAllLinesAsync(configPath, newLines);
            _loggingService.LogInfo(Strings.TranslationLanguageSet, language);
            return true;
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.SetLanguageError, language);
            return false;
        }
    }

    private async Task LoadManifestAsync()
    {
        try
        {
            var manifestPath = Path.Combine(_settingsService.AppDataPath, "translation_install_manifest.json");
            if (File.Exists(manifestPath))
            {
                var json = await File.ReadAllTextAsync(manifestPath);
                var manifest = JsonConvert.DeserializeObject<TranslationInstallManifest>(json);
                _installManifest = manifest ?? new TranslationInstallManifest();
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.TranslationManifestLoadError);
        }
    }

    private async Task SaveManifestAsync()
    {
        try
        {
            var manifestPath = Path.Combine(_settingsService.AppDataPath, "translation_install_manifest.json");
            var json = JsonConvert.SerializeObject(_installManifest, Formatting.Indented);
            await File.WriteAllTextAsync(manifestPath, json);
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.TranslationManifestSaveError);
        }
    }

    private async Task<List<string>> GetDirectoryFilesAsync(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
            return new List<string>();

        return Directory.GetFiles(directoryPath, "*", SearchOption.AllDirectories).ToList();
    }

    private async Task CopyDirectoryAsync(string sourceDir, string destinationDir, string[] excludePatterns)
    {
        _loggingService.LogInfo(Strings.DirectoryCopyStarted, sourceDir, destinationDir);
        _loggingService.LogInfo(Strings.DirectoryCopyExcludePatterns, string.Join(", ", excludePatterns));
        
        // Get all directories first, but exclude the ones we don't want to process
        var allDirectories = Directory.GetDirectories(sourceDir, "*", SearchOption.TopDirectoryOnly)
            .Where(dirPath => !ShouldExcludePath(Path.GetFileName(dirPath), excludePatterns))
            .ToList();
        
        // Recursively get subdirectories for allowed directories only
        var allowedDirectories = new List<string>();
        foreach (var topDir in allDirectories)
        {
            allowedDirectories.Add(topDir);
            allowedDirectories.AddRange(GetSubDirectoriesRecursively(topDir, sourceDir, excludePatterns));
        }
        
        // Create directories
        foreach (var dirPath in allowedDirectories)
        {
            var relativePath = Path.GetRelativePath(sourceDir, dirPath);
            var destDirPath = Path.Combine(destinationDir, relativePath);
            Directory.CreateDirectory(destDirPath);
            _loggingService.LogInfo(Strings.DirectoryCopyCreateDirectory, relativePath);
        }

        // Copy files from allowed directories only - run on background thread to avoid UI blocking
        await Task.Run(() =>
        {
            try
            {
                foreach (var dirPath in new[] { sourceDir }.Concat(allowedDirectories))
                {
                    foreach (var filePath in Directory.GetFiles(dirPath, "*", SearchOption.TopDirectoryOnly))
                    {
                        var relativePath = Path.GetRelativePath(sourceDir, filePath);
                        
                        // Double-check file exclusion
                        if (ShouldExcludePath(relativePath, excludePatterns))
                        {
                            _loggingService.LogInfo(Strings.DirectoryCopySkipFile, relativePath);
                            continue;
                        }

                        var destFilePath = Path.Combine(destinationDir, relativePath);
                        var destDir = Path.GetDirectoryName(destFilePath);
                        
                        if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                        {
                            Directory.CreateDirectory(destDir);
                        }
                        
                        File.Copy(filePath, destFilePath, true);
                        _loggingService.LogInfo(Strings.DirectoryCopyFile, relativePath);
                    }
                }
            }
            catch (Exception ex)
            {
                _loggingService.LogError(ex, "Directory copy failed: {0}", ex.Message);
                throw;
            }
        });
        
        _loggingService.LogInfo(Strings.DirectoryCopyComplete);
    }
    
    private List<string> GetSubDirectoriesRecursively(string directory, string sourceRoot, string[] excludePatterns)
    {
        var result = new List<string>();
        
        try
        {
            foreach (var subDir in Directory.GetDirectories(directory, "*", SearchOption.TopDirectoryOnly))
            {
                var relativePath = Path.GetRelativePath(sourceRoot, subDir);
                
                if (!ShouldExcludePath(relativePath, excludePatterns))
                {
                    result.Add(subDir);
                    result.AddRange(GetSubDirectoriesRecursively(subDir, sourceRoot, excludePatterns));
                }
                else
                {
                    _loggingService.LogInfo(Strings.DirectoryCopySkipTree, relativePath);
                }
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning(Strings.DirectoryAccessWarning, directory, ex.Message);
        }
        
        return result;
    }
    
    private bool ShouldExcludePath(string relativePath, string[] excludePatterns)
    {
        var normalizedPath = relativePath.Replace(Path.DirectorySeparatorChar, '/');
        
        foreach (var pattern in excludePatterns)
        {
            // Check if the path starts with the exclude pattern
            if (normalizedPath.StartsWith(pattern, StringComparison.OrdinalIgnoreCase))
                return true;
            
            // Check if any part of the path matches the exclude pattern
            var pathParts = normalizedPath.Split('/');
            if (pathParts.Any(part => string.Equals(part, pattern, StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        
        return false;
    }
    
    /// <summary>
    /// Safely removes a git directory by removing read-only attributes first
    /// </summary>
    private void RemoveGitDirectory(string gitPath)
    {
        try
        {
            // Recursively remove read-only attributes from all files in .git folder
            foreach (var file in Directory.GetFiles(gitPath, "*", SearchOption.AllDirectories))
            {
                try
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                catch
                {
                    // Ignore individual file attribute errors
                }
            }

            // Remove read-only attributes from directories
            foreach (var dir in Directory.GetDirectories(gitPath, "*", SearchOption.AllDirectories))
            {
                try
                {
                    File.SetAttributes(dir, FileAttributes.Normal);
                }
                catch
                {
                    // Ignore individual directory attribute errors
                }
            }

            // Now delete the .git directory
            Directory.Delete(gitPath, true);
        }
        catch (Exception ex)
        {
            // Log but don't throw - this is cleanup and shouldn't fail the main operation
            _loggingService.LogWarning(Strings.GitDirectoryRemovalWarning, ex.Message);
        }
    }
}

public class ProgressSplitter
{
    private readonly IProgress<DownloadProgress>? _baseProgress;
    private readonly int _totalSteps;

    public ProgressSplitter(IProgress<DownloadProgress>? baseProgress, int totalSteps)
    {
        _baseProgress = baseProgress;
        _totalSteps = totalSteps;
    }

    public IProgress<DownloadProgress>? GetProgress(int step)
    {
        if (_baseProgress == null) return null;

        return new Progress<DownloadProgress>(progress =>
        {
            var totalProgress = (step * 100 + progress.ProgressPercentage) / _totalSteps;
            _baseProgress.Report(new DownloadProgress
            {
                BytesReceived = progress.BytesReceived,
                TotalBytes = progress.TotalBytes,
                ProgressPercentage = totalProgress,
                SpeedBytesPerSecond = progress.SpeedBytesPerSecond,
                ElapsedTime = progress.ElapsedTime,
                EstimatedTimeRemaining = progress.EstimatedTimeRemaining
            });
        });
    }
}