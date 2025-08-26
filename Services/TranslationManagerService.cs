using GHPC_Mod_Manager.Models;
using LibGit2Sharp;
using Newtonsoft.Json;
using System.IO;
using System.IO.Compression;
using GHPC_Mod_Manager.Resources;

namespace GHPC_Mod_Manager.Services;

public interface ITranslationManagerService
{
    Task<bool> IsTranslationInstalledAsync();
    Task<List<GitHubRelease>> GetXUnityReleasesAsync();
    Task<bool> InstallTranslationAsync(string xUnityVersion, IProgress<DownloadProgress>? progress = null);
    Task<bool> UpdateTranslationFilesAsync(IProgress<DownloadProgress>? progress = null);
    Task<bool> UninstallTranslationAsync();
    Task<List<string>> GetAvailableLanguagesAsync();
    Task<string> GetCurrentLanguageAsync();
    Task<bool> SetLanguageAsync(string language);
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
        var configPath = Path.Combine(gameRootPath, "AutoTranslator", "Config.ini");

        return File.Exists(autoTranslatorPath) && File.Exists(configPath);
    }

    public async Task<List<GitHubRelease>> GetXUnityReleasesAsync()
    {
        try
        {
            var releases = await _networkService.GetGitHubReleasesAsync("bbepis", "XUnity.AutoTranslator");
            
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
            _loggingService.LogInfo(Strings.InstallingTranslation, xUnityVersion);

            var gameRootPath = _settingsService.Settings.GameRootPath;
            var filesBeforeInstall = await GetDirectoryFilesAsync(gameRootPath);

            var progressSplit = new ProgressSplitter(progress, 2);

            // 1. Install XUnity AutoTranslator
            var success = await InstallXUnityAutoTranslatorAsync(xUnityVersion, progressSplit.GetProgress(0));
            if (!success) return false;

            // 2. Clone translation files
            success = await CloneTranslationFilesAsync(progressSplit.GetProgress(1));
            if (!success) return false;

            var filesAfterInstall = await GetDirectoryFilesAsync(gameRootPath);
            var newFiles = filesAfterInstall.Except(filesBeforeInstall).ToList();

            // Split files into XUnity and translation repo files
            var modsPath = Path.Combine(gameRootPath, "Mods");
            var autoTranslatorPath = Path.Combine(gameRootPath, "AutoTranslator");

            _installManifest.XUnityAutoTranslatorFiles = newFiles
                .Where(f => f.StartsWith(modsPath) || f.Contains("XUnity") || f.Contains("AutoTranslator"))
                .Select(f => Path.GetRelativePath(gameRootPath, f))
                .ToList();

            _installManifest.TranslationRepoFiles = newFiles
                .Where(f => f.StartsWith(autoTranslatorPath) && !_installManifest.XUnityAutoTranslatorFiles.Contains(Path.GetRelativePath(gameRootPath, f)))
                .Select(f => Path.GetRelativePath(gameRootPath, f))
                .ToList();

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

    private async Task<bool> InstallXUnityAutoTranslatorAsync(string version, IProgress<DownloadProgress>? progress)
    {
        try
        {
            var releases = await GetXUnityReleasesAsync();
            var targetRelease = releases.FirstOrDefault(r => r.TagName == version);
            
            if (targetRelease == null)
            {
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
                return false;
            }

            var downloadData = await _networkService.DownloadFileAsync(asset.DownloadUrl, progress);
            var gameRootPath = _settingsService.Settings.GameRootPath;

            using var zipStream = new MemoryStream(downloadData);
            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);

            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;

                var destinationPath = Path.Combine(gameRootPath, entry.FullName);
                var destinationDir = Path.GetDirectoryName(destinationPath);
                
                if (!string.IsNullOrEmpty(destinationDir))
                {
                    Directory.CreateDirectory(destinationDir);
                }

                entry.ExtractToFile(destinationPath, true);
            }

            return true;
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.XUnityInstallError);
            return false;
        }
    }

    private async Task<bool> CloneTranslationFilesAsync(IProgress<DownloadProgress>? progress)
    {
        try
        {
            var translationConfig = await _networkService.GetTranslationConfigAsync(_settingsService.Settings.TranslationConfigUrl);
            if (string.IsNullOrEmpty(translationConfig.RepoUrl))
            {
                _loggingService.LogError(Strings.TranslationRepoUrlEmpty);
                return false;
            }

            var tempRepoPath = Path.Combine(_settingsService.TempPath, $"translation_repo_{Guid.NewGuid()}");
            var gameRootPath = _settingsService.Settings.GameRootPath;

            // Create a progress tracker for Git clone operation
            var startTime = DateTime.Now;
            var estimatedTotalBytes = 10 * 1024 * 1024; // Estimate 10MB for translation files
            
            progress?.Report(new DownloadProgress 
            { 
                ProgressPercentage = 10,
                BytesReceived = estimatedTotalBytes * 10 / 100,
                TotalBytes = estimatedTotalBytes,
                ElapsedTime = DateTime.Now - startTime,
                SpeedBytesPerSecond = (estimatedTotalBytes * 10 / 100) / Math.Max(1, (DateTime.Now - startTime).TotalSeconds)
            });

            // Configure clone options to handle certificate issues in proxy environments
            var cloneOptions = new CloneOptions();
            cloneOptions.FetchOptions.CertificateCheck = (certificate, valid, host) => 
            {
                // Accept certificates even if revocation status cannot be verified
                // This is common in proxy environments or when using self-signed certificates
                return true;
            };

            Repository.Clone(translationConfig.RepoUrl, tempRepoPath, cloneOptions);

            var cloneTime = DateTime.Now;
            progress?.Report(new DownloadProgress 
            { 
                ProgressPercentage = 70,
                BytesReceived = estimatedTotalBytes * 70 / 100,
                TotalBytes = estimatedTotalBytes,
                ElapsedTime = cloneTime - startTime,
                SpeedBytesPerSecond = (estimatedTotalBytes * 70 / 100) / Math.Max(1, (cloneTime - startTime).TotalSeconds)
            });

            await CopyDirectoryAsync(tempRepoPath, gameRootPath, new[] { ".git", ".gitignore", "README.md" });

            var endTime = DateTime.Now;
            progress?.Report(new DownloadProgress 
            { 
                ProgressPercentage = 100,
                BytesReceived = estimatedTotalBytes,
                TotalBytes = estimatedTotalBytes,
                ElapsedTime = endTime - startTime,
                SpeedBytesPerSecond = estimatedTotalBytes / Math.Max(1, (endTime - startTime).TotalSeconds)
            });

            // Safe cleanup: Remove .git folder first to avoid locked file issues during directory deletion
            var gitPath = Path.Combine(tempRepoPath, ".git");
            if (Directory.Exists(gitPath))
            {
                try
                {
                    // Force remove read-only attributes and delete .git folder
                    RemoveGitDirectory(gitPath);
                }
                catch (Exception ex)
                {
                    _loggingService.LogWarning(Strings.GitDirectoryRemovalWarning, ex.Message);
                    // Continue anyway, this is not critical for functionality
                }
            }

            Directory.Delete(tempRepoPath, true);

            // Update translation config
            translationConfig.LastUpdated = DateTime.Now;
            var configPath = Path.Combine(_settingsService.AppDataPath, "translationconfig.json");
            var configJson = JsonConvert.SerializeObject(translationConfig, Formatting.Indented);
            await File.WriteAllTextAsync(configPath, configJson);

            return true;
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.TranslationFilesCloneError);
            return false;
        }
    }

    public async Task<bool> UpdateTranslationFilesAsync(IProgress<DownloadProgress>? progress = null)
    {
        try
        {
            _loggingService.LogInfo(Strings.UpdatingTranslationFiles);

            // Remove existing translation files
            if (_installManifest.TranslationRepoFiles.Any())
            {
                var gameRootPath = _settingsService.Settings.GameRootPath;
                foreach (var relativePath in _installManifest.TranslationRepoFiles)
                {
                    var fullPath = Path.Combine(gameRootPath, relativePath);
                    if (File.Exists(fullPath))
                    {
                        File.Delete(fullPath);
                    }
                }
            }

            // Re-clone translation files
            var success = await CloneTranslationFilesAsync(progress);
            if (success)
            {
                await SaveManifestAsync();
                _loggingService.LogInfo(Strings.TranslationFilesUpdated);
            }

            return success;
        }
        catch (Exception ex)
        {
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

        // Copy files from allowed directories only
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