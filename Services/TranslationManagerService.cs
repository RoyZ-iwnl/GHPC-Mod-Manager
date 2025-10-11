using GHPC_Mod_Manager.Models;
using Newtonsoft.Json;
using System.IO;
using System.IO.Compression;
using GHPC_Mod_Manager.Resources;

namespace GHPC_Mod_Manager.Services;

public interface ITranslationManagerService
{
    Task<bool> IsTranslationInstalledAsync();
    Task<bool> IsTranslationManuallyInstalledAsync();
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
    Task<bool> IsTranslationUpdateAvailableAsync(bool forceRefresh = false);
    Task<DateTime?> GetLatestTranslationReleaseTimeAsync(bool forceRefresh = false);
    Task<bool> IsXUnityUpdateAvailableAsync(bool forceRefresh = false);
    Task<bool> UpdateXUnityPluginAsync(IProgress<DownloadProgress>? progress = null);
    Task<string> GetLatestXUnityVersionAsync(bool forceRefresh = false);
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

    public async Task<bool> IsTranslationManuallyInstalledAsync()
    {
        var gameRootPath = _settingsService.Settings.GameRootPath;
        if (string.IsNullOrEmpty(gameRootPath))
            return false;

        // Check if translation files exist
        var autoTranslatorPath = Path.Combine(gameRootPath, "Mods", "XUnity.AutoTranslator.Plugin.MelonMod.dll");
        var autoTranslatorBackupPath = Path.Combine(gameRootPath, "Mods", "XUnity.AutoTranslator.Plugin.MelonMod.dllbak");
        var configPath = Path.Combine(gameRootPath, "AutoTranslator", "Config.ini");
        var filesExist = File.Exists(configPath) && (File.Exists(autoTranslatorPath) || File.Exists(autoTranslatorBackupPath));

        if (!filesExist)
            return false; // No translation files, definitely not manually installed

        // Check if manifest file exists
        var manifestPath = Path.Combine(_settingsService.AppDataPath, "translation_install_manifest.json");
        if (!File.Exists(manifestPath))
            return true; // Files exist but no manifest = manually installed

        // Manifest exists, load and check if it's valid
        await LoadManifestAsync();
        var manifestIsValid = _installManifest.XUnityAutoTranslatorFiles.Any() ||
                              _installManifest.TranslationRepoFiles.Any();

        // Files exist but manifest is invalid or too old = manually installed
        return !manifestIsValid || _installManifest.InstallDate.Year < 2024;
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
            _loggingService.LogInfo(Strings.TranslationInstallationStarted, xUnityVersion);
            _loggingService.LogInfo(Strings.InstallingTranslation, xUnityVersion);

            var gameRootPath = _settingsService.Settings.GameRootPath;
            
            // 创建文件操作追踪器
            var tracker = new FileOperationTracker(_loggingService, _settingsService);
            var trackedOps = new TrackedFileOperations(tracker, _loggingService);

            // 开始追踪文件操作
            tracker.StartTracking($"translation_install_{xUnityVersion}", gameRootPath);

            var progressSplit = new ProgressSplitter(progress, 2);

            // 1. Install XUnity AutoTranslator
            var success = await InstallXUnityAutoTranslatorWithTrackingAsync(xUnityVersion, trackedOps, progressSplit.GetProgress(0));
            if (!success) return false;

            // 2. Clone translation files
            success = await CloneTranslationFilesWithTrackingAsync(trackedOps, progressSplit.GetProgress(1));
            if (!success) return false;

            // 停止追踪并获取结果
            tracker.StopTracking();
            var result = tracker.GetResult();
            var processedFiles = result.GetAllProcessedFiles();


            if (!processedFiles.Any())
            {
                _loggingService.LogError(Strings.TranslationInstallError);
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
                    _loggingService.LogError(Strings.TranslationRepoUrlEmpty);
                    return new List<GitHubRelease>();
                }
                owner = parseResult.Value.owner;
                repoName = parseResult.Value.repoName;
            }
            else
            {
                _loggingService.LogError(Strings.TranslationRepoUrlEmpty);
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
                _loggingService.LogWarning(Strings.URLIsNotGitHubRepository, repoUrl);
                return null;
            }

            // Extract owner and repo from path (e.g., "/RoyZ-iwnl/ghpc-translation")
            var pathParts = uri.AbsolutePath.Trim('/').Split('/');
            if (pathParts.Length < 2)
            {
                _loggingService.LogWarning(Strings.InvalidGitHubRepositoryPath, uri.AbsolutePath);
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
            _loggingService.LogError(ex, "Failed to parse GitHub repo URL");
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
            _loggingService.LogError(ex, "Error checking translation plugin status");
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
            var releases = await GetXUnityReleasesAsync();
            var targetRelease = releases.FirstOrDefault(r => r.TagName == version);
            
            if (targetRelease == null)
            {
                _loggingService.LogError(Strings.XUnityVersionNotFound, version);
                _loggingService.LogError(Strings.XUnityVersionNotFound, version);
                return false;
            }

            var asset = targetRelease.Assets.FirstOrDefault(a => 
                a.Name.Contains("MelonMod") && 
                !a.Name.Contains("IL2CPP") && 
                a.Name.EndsWith(".zip"));

            if (asset == null)
            {
                _loggingService.LogError(Strings.XUnityAssetNotFound);
                _loggingService.LogError(Strings.XUnityAssetNotFound);
                return false;
            }

            var downloadData = await _networkService.DownloadFileAsync(asset.DownloadUrl, progress);
            
            var gameRootPath = _settingsService.Settings.GameRootPath;

            // 使用追踪的ZIP解压操作
            await trackedOps.ExtractZipAsync(downloadData, gameRootPath);
            

            return true;
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.XUnityInstallError);
            _loggingService.LogError(ex, Strings.XUnityInstallError);
            return false;
        }
    }

    private async Task<bool> CloneTranslationFilesWithTrackingAsync(TrackedFileOperations trackedOps, IProgress<DownloadProgress>? progress)
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
                    _loggingService.LogError(Strings.TranslationRepoUrlEmpty);
                    return false;
                }
                owner = parseResult.Value.owner;
                repoName = parseResult.Value.repoName;
            }
            else
            {
                _loggingService.LogError(Strings.TranslationRepoUrlEmpty);
                _loggingService.LogError(Strings.TranslationRepoUrlEmpty);
                return false;
            }

            // 获取翻译仓库的最新Release
            var releases = await _networkService.GetGitHubReleasesAsync(owner, repoName);
            if (!releases.Any())
            {
                _loggingService.LogError(Strings.TranslationReleasesFetchError);
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
                _loggingService.LogError(Strings.TranslationReleasesFetchError);
                _loggingService.LogError(Strings.TranslationReleasesFetchError);
                return false;
            }

            var latestRelease = validReleases.First();
            var targetAssetName = !string.IsNullOrEmpty(translationConfig.TargetAssetName) ? translationConfig.TargetAssetName : ".zip";
            var targetAsset = latestRelease.Assets.FirstOrDefault(a => 
                a.Name.Contains("ghpc-translation-") && a.Name.EndsWith(targetAssetName));

            if (targetAsset == null)
            {
                _loggingService.LogError(Strings.TranslationAssetNotFound);
                _loggingService.LogError(Strings.TranslationAssetNotFound);
                return false;
            }

            _loggingService.LogInfo(Strings.DownloadingTranslationRelease, latestRelease.TagName);

            // 下载翻译ZIP文件
            var zipData = await _networkService.DownloadFileAsync(targetAsset.DownloadUrl, progress);
            if (zipData == null || zipData.Length == 0)
            {
                _loggingService.LogError(Strings.FailedToDownloadTranslationZip);
                return false;
            }


            var gameRootPath = _settingsService.Settings.GameRootPath;

            // 使用追踪的ZIP解压操作，排除不需要的文件
            await trackedOps.ExtractZipAsync(zipData, gameRootPath, new[] { ".git", ".gitignore", "README.md", "LICENSE" });
            

            // 更新翻译配置
            translationConfig.LastUpdated = DateTime.Now;
            var configPath = Path.Combine(_settingsService.AppDataPath, "translationconfig.json");
            var configJson = JsonConvert.SerializeObject(translationConfig, Formatting.Indented);
            await File.WriteAllTextAsync(configPath, configJson);

            return true;
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.TranslationFilesCloneError);
            _loggingService.LogError(ex, Strings.TranslationFilesCloneError);
            return false;
        }
    }

    public async Task<bool> UpdateTranslationFilesAsync(IProgress<DownloadProgress>? progress = null)
    {
        try
        {
            _loggingService.LogInfo(Strings.UpdatingTranslationFiles);

            await LoadManifestAsync();

            // Remove existing translation files
            if (_installManifest.TranslationRepoFiles.Any())
            {
                _loggingService.LogInfo(Strings.TranslationFilesRemoved, _installManifest.TranslationRepoFiles.Count);
                var gameRootPath = _settingsService.Settings.GameRootPath;
                foreach (var relativePath in _installManifest.TranslationRepoFiles)
                {
                    var fullPath = Path.Combine(gameRootPath, relativePath);
                    if (File.Exists(fullPath))
                    {
                        File.Delete(fullPath);
                        _loggingService.LogInfo(Strings.TranslationFileRemoved, relativePath);
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
            var success = await CloneTranslationFilesWithTrackingAsync(trackedOps, progress);
            
            if (success)
            {
                // 停止追踪并获取结果
                tracker.StopTracking();
                var result = tracker.GetResult();
                var processedFiles = result.GetAllProcessedFiles();


                // 更新manifest中的翻译文件列表
                var autoTranslatorPath = Path.Combine(gameRootPath2, "AutoTranslator");
                _installManifest.TranslationRepoFiles = processedFiles
                    .Where(f => 
                    {
                        var fullPath = Path.Combine(gameRootPath2, f);
                        return fullPath.StartsWith(autoTranslatorPath, StringComparison.OrdinalIgnoreCase);
                    })
                    .ToList();

                _installManifest.InstallDate = DateTime.Now;

                await SaveManifestAsync();
                _loggingService.LogInfo(Strings.TranslationFilesUpdated);
            }

            return success;
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.TranslationUpdateError);
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

    public Task<List<string>> GetAvailableLanguagesAsync()
    {
        try
        {
            var gameRootPath = _settingsService.Settings.GameRootPath;
            var translationPath = Path.Combine(gameRootPath, "AutoTranslator", "Translation");

            if (!Directory.Exists(translationPath))
                return Task.FromResult(new List<string>());

            var languageFolders = Directory.GetDirectories(translationPath)
                .Select(d => Path.GetFileName(d))
                .Where(name => !string.IsNullOrEmpty(name))
                .ToList();

            // Return language codes directly
            return Task.FromResult(languageFolders!);
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.LanguageListError);
            return Task.FromResult(new List<string>());
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
                            continue;
                        }

                        var destFilePath = Path.Combine(destinationDir, relativePath);
                        var destDir = Path.GetDirectoryName(destFilePath);

                        if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                        {
                            Directory.CreateDirectory(destDir);
                        }

                        File.Copy(filePath, destFilePath, true);
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

    /// <summary>
    /// 从release文件名中解析时间戳 (格式: ghpc-translation-YYYYMMDD-HHMMSS.zip)
    /// </summary>
    private DateTime? ParseReleaseTimestamp(string fileName)
    {
        try
        {
            // 匹配格式: ghpc-translation-20250922-163645.zip
            var pattern = @"ghpc-translation-(\d{8})-(\d{6})\.(?:zip|tar\.gz)$";
            var match = System.Text.RegularExpressions.Regex.Match(fileName, pattern);
            
            if (!match.Success)
                return null;

            var datePart = match.Groups[1].Value; // 20250922
            var timePart = match.Groups[2].Value; // 163645

            // 解析日期部分
            if (datePart.Length != 8 || !int.TryParse(datePart.Substring(0, 4), out var year) ||
                !int.TryParse(datePart.Substring(4, 2), out var month) ||
                !int.TryParse(datePart.Substring(6, 2), out var day))
                return null;

            // 解析时间部分
            if (timePart.Length != 6 || !int.TryParse(timePart.Substring(0, 2), out var hour) ||
                !int.TryParse(timePart.Substring(2, 2), out var minute) ||
                !int.TryParse(timePart.Substring(4, 2), out var second))
                return null;

            return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc);
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning(Strings.TranslationTimestampParseError + ": '{0}' - {1}", fileName, ex.Message);
            return null;
        }
    }

    public async Task<DateTime?> GetLatestTranslationReleaseTimeAsync(bool forceRefresh = false)
    {
        try
        {
            var releases = await GetTranslationReleasesAsync(forceRefresh);
            if (!releases.Any())
                return null;

            DateTime? latestTime = null;

            foreach (var release in releases)
            {
                var asset = release.Assets.FirstOrDefault(a =>
                    a.Name.Contains("ghpc-translation-") &&
                    (a.Name.EndsWith(".zip") || a.Name.EndsWith(".tar.gz")));

                if (asset != null)
                {
                    var timestamp = ParseReleaseTimestamp(asset.Name);
                    if (timestamp.HasValue && (!latestTime.HasValue || timestamp.Value > latestTime.Value))
                    {
                        latestTime = timestamp.Value;
                    }
                }
            }

            return latestTime;
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.TranslationReleaseTimeError);
            return null;
        }
    }

    public async Task<bool> IsTranslationUpdateAvailableAsync(bool forceRefresh = false)
    {
        try
        {
            // 检查是否已安装翻译
            if (!await IsTranslationInstalledAsync())
                return false;

            // 加载安装清单
            await LoadManifestAsync();

            // 获取最新Release时间
            var latestReleaseTime = await GetLatestTranslationReleaseTimeAsync(forceRefresh);
            if (!latestReleaseTime.HasValue)
                return false;

            // 对比安装时间与最新Release时间（统一转换为UTC时间对比）
            var installTimeUtc = _installManifest.InstallDate.ToUniversalTime();
            var hasUpdate = installTimeUtc < latestReleaseTime.Value;
            
            _loggingService.LogInfo(Strings.TranslationUpdateCheckResult, 
                _installManifest.InstallDate.ToString("yyyy-MM-dd HH:mm:ss"), 
                latestReleaseTime.Value.ToString("yyyy-MM-dd HH:mm:ss"), 
                hasUpdate);

            return hasUpdate;
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.TranslationUpdateCheckError);
            return false;
        }
    }

    public async Task<bool> IsXUnityUpdateAvailableAsync(bool forceRefresh = false)
    {
        try
        {
            // 检查是否已安装翻译
            if (!await IsTranslationInstalledAsync())
                return false;

            // 加载安装清单获取当前版本
            await LoadManifestAsync();
            if (string.IsNullOrEmpty(_installManifest.XUnityVersion))
                return false;

            // 获取最新版本
            var latestVersion = await GetLatestXUnityVersionAsync(forceRefresh);
            if (string.IsNullOrEmpty(latestVersion))
                return false;

            // 比较版本
            var hasUpdate = _installManifest.XUnityVersion != latestVersion;

            _loggingService.LogInfo(Strings.TranslationUpdateCheckResult,
                _installManifest.XUnityVersion,
                latestVersion,
                hasUpdate);

            return hasUpdate;
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.TranslationUpdateCheckError);
            return false;
        }
    }

    public async Task<string> GetLatestXUnityVersionAsync(bool forceRefresh = false)
    {
        try
        {
            var releases = await GetXUnityReleasesAsync(forceRefresh);
            return releases.FirstOrDefault()?.TagName ?? string.Empty;
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.XUnityReleasesFetchError);
            return string.Empty;
        }
    }

    public async Task<bool> UpdateXUnityPluginAsync(IProgress<DownloadProgress>? progress = null)
    {
        try
        {
            _loggingService.LogInfo(Strings.UpdatingTranslationPlugin);

            // 检查是否已安装翻译
            if (!await IsTranslationInstalledAsync())
            {
                _loggingService.LogError(Strings.TranslationSystemNotInstalled);
                return false;
            }

            // 获取最新版本
            var latestVersion = await GetLatestXUnityVersionAsync();
            if (string.IsNullOrEmpty(latestVersion))
            {
                _loggingService.LogError(Strings.CannotGetTranslationVersions);
                return false;
            }

            await LoadManifestAsync();
            var gameRootPath = _settingsService.Settings.GameRootPath;

            // 创建文件操作追踪器
            var tracker = new FileOperationTracker(_loggingService, _settingsService);
            var trackedOps = new TrackedFileOperations(tracker, _loggingService);

            // 开始追踪文件操作
            tracker.StartTracking($"xunity_update_{latestVersion}", gameRootPath);

            // 备份当前插件文件
            var success = await BackupCurrentXUnityFilesAsync(trackedOps);
            if (!success) return false;

            // 安装新版本
            success = await InstallXUnityAutoTranslatorWithTrackingAsync(latestVersion, trackedOps, progress);
            if (!success) return false;

            // 停止追踪并更新清单
            tracker.StopTracking();
            var result = tracker.GetResult();
            var processedFiles = result.GetAllProcessedFiles();

            if (!processedFiles.Any())
            {
                _loggingService.LogError(Strings.TranslationPluginUpdateFailed);
                return false;
            }

            // 更新清单中的插件文件列表
            var modsPath = Path.Combine(gameRootPath, "Mods");
            var userLibsPath = Path.Combine(gameRootPath, "UserLibs");

            // 保留原有的翻译资源文件，只更新插件文件
            var translationRepoFiles = _installManifest.TranslationRepoFiles.ToList();

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

            _installManifest.TranslationRepoFiles = translationRepoFiles;
            _installManifest.XUnityVersion = latestVersion;
            await SaveManifestAsync();

            _loggingService.LogInfo(Strings.TranslationPluginUpdateSuccessful);
            return true;
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.TranslationPluginUpdateFailed);
            return false;
        }
    }

    private async Task<bool> BackupCurrentXUnityFilesAsync(TrackedFileOperations trackedOps)
    {
        try
        {
            await LoadManifestAsync();
            var gameRootPath = _settingsService.Settings.GameRootPath;

            // 备份当前的XUnity文件
            foreach (var file in _installManifest.XUnityAutoTranslatorFiles)
            {
                var fullPath = Path.Combine(gameRootPath, file);
                if (File.Exists(fullPath))
                {
                    // 创建备份路径
                    var backupDir = Path.Combine(_settingsService.AppDataPath, "translation_backup", "xunity", DateTime.Now.ToString("yyyyMMdd_HHmmss"));
                    Directory.CreateDirectory(backupDir);

                    var backupPath = Path.Combine(backupDir, Path.GetFileName(file));
                    File.Copy(fullPath, backupPath, true);

                    // 记录操作用于追踪
                    await trackedOps.CopyFileAsync(fullPath, backupPath);
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, "Failed to backup XUnity files");
            return false;
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