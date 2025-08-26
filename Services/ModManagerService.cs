using GHPC_Mod_Manager.Models;
using GHPC_Mod_Manager.ViewModels;
using Newtonsoft.Json;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using GHPC_Mod_Manager.Resources;

namespace GHPC_Mod_Manager.Services;

public interface IModManagerService
{
    Task<List<ModViewModel>> GetModListAsync();
    Task<bool> InstallModAsync(ModConfig modConfig, string version, IProgress<DownloadProgress>? progress = null);
    Task<bool> UninstallModAsync(string modId);
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
    private List<ModConfig> _availableMods = new();
    private ModInstallManifest _installManifest = new();
    private Dictionary<string, Dictionary<string, string>> _configComments = new(); // Store comments for each mod
    private Dictionary<string, List<string>> _standaloneComments = new(); // Store standalone comments for each mod

    public ModManagerService(ISettingsService settingsService, INetworkService networkService, ILoggingService loggingService, IModI18nService modI18nService)
    {
        _settingsService = settingsService;
        _networkService = networkService;
        _loggingService = loggingService;
        _modI18nService = modI18nService;
    }

    public async Task<List<ModViewModel>> GetModListAsync()
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
                    viewModel.InstalledVersion = GHPC_Mod_Manager.Resources.Strings.Manual;
                    viewModel.IsEnabled = IsModEnabled(modConfig.MainBinaryFileName, modsPath);
                }
            }

            try
            {
                var releases = await _networkService.GetGitHubReleasesAsync(
                    GetRepoOwner(modConfig.ReleaseUrl),
                    GetRepoName(modConfig.ReleaseUrl)
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
        await AddTranslationPluginAsync(result, modsPath);
        
        return result;
    }

    private async Task AddTranslationPluginAsync(List<ModViewModel> modList, string modsPath)
    {
        await Task.Run(() =>
        {
            const string translationPluginFileName = "XUnity.AutoTranslator.Plugin.MelonMod.dll";
            
            if (IsModInstalled(translationPluginFileName, modsPath))
            {
                // Check if already exists in list
                if (!modList.Any(m => m.Id == "translation_plugin"))
                {
                    modList.Add(new ModViewModel
                    {
                        Id = "translation_plugin",
                        DisplayName = GHPC_Mod_Manager.Resources.Strings.TranslationPlugin,
                        IsInstalled = true,
                        IsTranslationPlugin = true,  // Special flag for translation plugin
                        IsEnabled = IsModEnabled(translationPluginFileName, modsPath),
                        InstalledVersion = GHPC_Mod_Manager.Resources.Strings.Installed,
                        LatestVersion = ""
                    });
                }
            }
        });
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
            var dllFiles = Directory.GetFiles(modsPath, "*.dll", SearchOption.AllDirectories);
            var bakFiles = Directory.GetFiles(modsPath, "*.dll.bak", SearchOption.AllDirectories);
            
            // Combine both active and backup files for scanning
            var allModFiles = new List<string>();
            allModFiles.AddRange(dllFiles);
            allModFiles.AddRange(bakFiles.Select(f => f.Replace(".bak", "")));
            
            var uniqueModFiles = allModFiles.Distinct().ToList();
            
            foreach (var modFile in uniqueModFiles)
            {
                var fileName = Path.GetFileName(modFile);
                
                // Skip if already in available mods list
                if (modList.Any(m => m.Config.MainBinaryFileName == fileName))
                    continue;

                // Skip if already in install manifest
                if (_installManifest.InstalledMods.Values.Any(m => m.InstalledFiles.Contains(Path.GetRelativePath(_settingsService.Settings.GameRootPath, modFile))))
                    continue;

                // Skip XUnity AutoTranslator (translation plugin)
                if (fileName == "XUnity.AutoTranslator.Plugin.MelonMod.dll")
                    continue;

                modList.Add(new ModViewModel
                {
                    Id = $"manual_{Path.GetFileNameWithoutExtension(fileName)}",
                    DisplayName = Path.GetFileNameWithoutExtension(fileName),
                    IsInstalled = true,
                    IsManuallyInstalled = true,
                    IsEnabled = IsModEnabled(fileName, modsPath),
                    InstalledVersion = GHPC_Mod_Manager.Resources.Strings.Manual,
                    LatestVersion = GHPC_Mod_Manager.Resources.Strings.Unknown
                });
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

            var filesBeforeInstall = await GetDirectoryFilesAsync(modsPath);

            if (modConfig.InstallMethod == InstallMethod.Scripted && !string.IsNullOrEmpty(modConfig.InstallScript_Base64))
            {
                var confirmed = await ShowScriptWarningAsync();
                if (!confirmed) return false;

                await ExecuteInstallScriptAsync(modConfig.InstallScript_Base64, asset.DownloadUrl, gameRootPath, progress);
            }
            else
            {
                var downloadData = await _networkService.DownloadFileAsync(asset.DownloadUrl, progress);
                await ExtractToModsDirectoryAsync(downloadData, modsPath);
            }

            var filesAfterInstall = await GetDirectoryFilesAsync(modsPath);
            var newFiles = filesAfterInstall.Except(filesBeforeInstall).ToList();

            var installInfo = new ModInstallInfo
            {
                ModId = modConfig.Id,
                Version = version,
                InstalledFiles = newFiles.Select(f => Path.GetRelativePath(gameRootPath, f)).ToList(),
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

            foreach (var relativePath in installInfo.InstalledFiles)
            {
                var fullPath = Path.Combine(gameRootPath, relativePath);
                if (File.Exists(fullPath))
                {
                    File.Delete(fullPath);
                }
            }

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
            var gameRootPath = _settingsService.Settings.GameRootPath;
            var modsPath = Path.Combine(gameRootPath, "Mods");
            
            // Special handling for translation plugin
            if (modId == "translation_plugin")
            {
                const string translationPluginFileName = "XUnity.AutoTranslator.Plugin.MelonMod.dll";
                var modFile = Path.Combine(modsPath, translationPluginFileName);
                var backupFile = modFile + ".bak";
                return await ToggleModFileAsync(modFile, backupFile, enable, modId);
            }
            
            // For managed mods, use config info
            var modConfig = _availableMods.FirstOrDefault(m => m.Id == modId);
            if (modConfig != null)
            {
                var modFile = Path.Combine(modsPath, modConfig.MainBinaryFileName);
                var backupFile = modFile + ".bak";
                return await ToggleModFileAsync(modFile, backupFile, enable, modId);
            }
            
            // For manual mods, derive filename from modId
            if (modId.StartsWith("manual_"))
            {
                var fileName = modId.Substring("manual_".Length) + ".dll";
                var modFile = Path.Combine(modsPath, fileName);
                var backupFile = modFile + ".bak";
                return await ToggleModFileAsync(modFile, backupFile, enable, modId);
            }

            _loggingService.LogError(Strings.ModNotFound, modId);
            return false;
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.ModToggleError, modId, enable);
            return false;
        }
    }

    private async Task<bool> ToggleModFileAsync(string modFile, string backupFile, bool enable, string modId)
    {
        try
        {
            if (enable)
            {
                if (File.Exists(backupFile) && !File.Exists(modFile))
                {
                    File.Move(backupFile, modFile);
                    _loggingService.LogInfo(Strings.ModEnabled, modId);
                    return true;
                }
            }
            else
            {
                if (File.Exists(modFile) && !File.Exists(backupFile))
                {
                    File.Move(modFile, backupFile);
                    _loggingService.LogInfo(Strings.ModDisabled, modId);
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.ModToggleFileError, modId, enable);
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
                            
                            var value = valueAndComment.Trim().Trim('"');

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
                            
                            var value = valueAndComment.Trim().Trim('"');

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

                            var formattedValue = newValue is bool ? newValue.ToString().ToLower() : $"\"{newValue}\"";
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
        var backupFile = modFile + ".bak";
        
        // If the original file exists, mod is enabled
        if (File.Exists(modFile))
            return true;
            
        // If backup file exists, mod is installed but disabled
        if (File.Exists(backupFile))
            return false;
            
        // Neither exists, mod is not installed
        return false;
    }

    private bool IsModInstalled(string binaryFileName, string modsPath)
    {
        var modFile = Path.Combine(modsPath, binaryFileName);
        var backupFile = modFile + ".bak";
        
        // Mod is installed if either the active file or backup file exists
        return File.Exists(modFile) || File.Exists(backupFile);
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

    private async Task<List<string>> GetDirectoryFilesAsync(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
            return new List<string>();

        return Directory.GetFiles(directoryPath, "*", SearchOption.AllDirectories).ToList();
    }

    private async Task ExtractToModsDirectoryAsync(byte[] zipData, string modsPath)
    {
        using var zipStream = new MemoryStream(zipData);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);

        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;

            var destinationPath = Path.Combine(modsPath, entry.FullName);
            var destinationDir = Path.GetDirectoryName(destinationPath);
            
            if (!string.IsNullOrEmpty(destinationDir))
            {
                Directory.CreateDirectory(destinationDir);
            }

            entry.ExtractToFile(destinationPath, true);
        }
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

    private async Task ExecuteInstallScriptAsync(string base64Script, string downloadUrl, string gameRootPath, IProgress<DownloadProgress>? progress)
    {
        var scriptContent = Encoding.UTF8.GetString(Convert.FromBase64String(base64Script));
        var tempBatFile = Path.Combine(_settingsService.TempPath, $"install_{Guid.NewGuid()}.bat");
        
        await File.WriteAllTextAsync(tempBatFile, scriptContent);
        
        var processInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = tempBatFile,
            WorkingDirectory = gameRootPath,
            Arguments = $"\"{downloadUrl}\"",
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = System.Diagnostics.Process.Start(processInfo);
        if (process != null)
        {
            await process.WaitForExitAsync();
        }
        
        File.Delete(tempBatFile);
    }

    public string GetLocalizedConfigLabel(string modId, string configKey)
    {
        return _modI18nService.GetLocalizedLabel(modId, configKey, configKey);
    }

    public string GetLocalizedConfigComment(string modId, string commentKey)
    {
        return _modI18nService.GetLocalizedComment(modId, commentKey, commentKey);
    }
}