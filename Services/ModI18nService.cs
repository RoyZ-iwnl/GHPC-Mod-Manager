using GHPC_Mod_Manager.Models;
using Newtonsoft.Json;
using System.IO;
using System.Globalization;
using GHPC_Mod_Manager.Resources;

namespace GHPC_Mod_Manager.Services;

public interface IModI18nService
{
    Task<ModI18nManager> LoadModI18nConfigAsync();
    string GetLocalizedLabel(string modId, string configKey, string fallback);
    string GetLocalizedComment(string modId, string commentKey, string fallback);
    Task RefreshModI18nConfigAsync();
}

public class ModI18nService : IModI18nService
{
    private readonly INetworkService _networkService;
    private readonly ISettingsService _settingsService;
    private readonly ILoggingService _loggingService;
    private ModI18nManager _modI18nManager = new();
    private readonly string _cacheFilePath;

    public ModI18nService(
        INetworkService networkService, 
        ISettingsService settingsService,
        ILoggingService loggingService)
    {
        _networkService = networkService;
        _settingsService = settingsService;
        _loggingService = loggingService;
        _cacheFilePath = Path.Combine(_settingsService.AppDataPath, "mod_i18n.json");
        
        _ = LoadModI18nConfigAsync();
    }

    public async Task<ModI18nManager> LoadModI18nConfigAsync()
    {
        // Try to load from cache first
        if (File.Exists(_cacheFilePath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(_cacheFilePath);
                var cached = JsonConvert.DeserializeObject<ModI18nManager>(json);
                if (cached != null)
                {
                    _modI18nManager = cached;
                    _loggingService.LogInfo(Strings.ModI18nConfigLoadedFromCache);
                }
            }
            catch (Exception ex)
            {
                _loggingService.LogError(ex, Strings.ModI18nConfigCacheLoadError);
            }
        }

        // Try to refresh from remote
        await RefreshModI18nConfigAsync();
        
        return _modI18nManager;
    }

    public async Task RefreshModI18nConfigAsync()
    {
        try
        {
            var remoteConfig = await _networkService.GetModI18nConfigAsync(_settingsService.Settings.ModI18nUrl);
            if (remoteConfig?.ModConfigs != null && remoteConfig.ModConfigs.Any())
            {
                _modI18nManager = remoteConfig;
                
                // Cache the result
                var json = JsonConvert.SerializeObject(_modI18nManager, Formatting.Indented);
                await File.WriteAllTextAsync(_cacheFilePath, json);
                
                _loggingService.LogInfo(Strings.ModI18nConfigRefreshedWithCount, _modI18nManager.ModConfigs.Count);
            }
            else
            {
                _loggingService.LogWarning(Strings.ModI18nConfigEmptyOrInaccessible);
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.ModI18nConfigRefreshError);
        }
    }

    public string GetLocalizedLabel(string modId, string configKey, string fallback)
    {
        var currentCulture = _settingsService.Settings.Language;
        
        if (_modI18nManager.ModConfigs.TryGetValue(modId, out var modConfig) &&
            modConfig.ConfigLabels.TryGetValue(configKey, out var configLabels) &&
            configLabels.TryGetValue(currentCulture, out var localizedLabel))
        {
            return localizedLabel;
        }

        // Try fallback to English if current culture is not available
        if (currentCulture != "en-US" && 
            _modI18nManager.ModConfigs.TryGetValue(modId, out modConfig) &&
            modConfig.ConfigLabels.TryGetValue(configKey, out var fallbackLabels) &&
            fallbackLabels.TryGetValue("en-US", out var englishLabel))
        {
            return englishLabel;
        }

        return fallback;
    }

    public string GetLocalizedComment(string modId, string commentKey, string fallback)
    {
        var currentCulture = _settingsService.Settings.Language;
        
        if (_modI18nManager.ModConfigs.TryGetValue(modId, out var modConfig) &&
            modConfig.ConfigComments.TryGetValue(commentKey, out var comments) &&
            comments.TryGetValue(currentCulture, out var localizedComment))
        {
            return localizedComment;
        }

        // Try fallback to English if current culture is not available
        if (currentCulture != "en-US" && 
            _modI18nManager.ModConfigs.TryGetValue(modId, out modConfig) &&
            modConfig.ConfigComments.TryGetValue(commentKey, out comments) &&
            comments.TryGetValue("en-US", out var englishComment))
        {
            return englishComment;
        }

        return fallback;
    }
}