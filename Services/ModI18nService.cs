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
    Task RefreshModI18nConfigAsync(bool forceRefresh = false);
}

public class ModI18nService : IModI18nService
{
    private readonly INetworkService _networkService;
    private readonly ISettingsService _settingsService;
    private readonly ILoggingService _loggingService;
    private readonly IMainConfigService _mainConfigService;
    private ModI18nManager _modI18nManager = new();
    private readonly string _cacheFilePath;

    public ModI18nService(
        INetworkService networkService,
        ISettingsService settingsService,
        ILoggingService loggingService,
        IMainConfigService mainConfigService)
    {
        _networkService = networkService;
        _settingsService = settingsService;
        _loggingService = loggingService;
        _mainConfigService = mainConfigService;
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

    public async Task RefreshModI18nConfigAsync(bool forceRefresh = false)
    {
        try
        {
            ModI18nManager? remoteConfig = null;
            var urls = _mainConfigService.GetModI18nUrlCandidates();

            for (var i = 0; i < urls.Count; i++)
            {
                var url = urls[i];
                var candidate = await _networkService.GetModI18nConfigAsync(url, forceRefresh);
                if (candidate?.ModConfigs != null && candidate.ModConfigs.Any())
                {
                    remoteConfig = candidate;
                    break;
                }

                var hasNext = i < urls.Count - 1;
                if (hasNext)
                    _loggingService.LogWarning("ModI18n配置为空或不可访问，触发fallback，尝试下一个渠道。当前渠道: {0}", url);
                else
                    _loggingService.LogWarning("ModI18n配置为空或不可访问，且已无可用fallback。最后渠道: {0}", url);
            }

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
