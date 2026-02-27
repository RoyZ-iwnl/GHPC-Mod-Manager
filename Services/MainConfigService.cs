using GHPC_Mod_Manager.Models;
using Newtonsoft.Json;
using System.IO;
using System.Net.Http;

namespace GHPC_Mod_Manager.Services;

// 硬编码的内置默认地址，作为最后兜底
file static class BuiltinDefaults
{
    public const string MainConfigPrimary  = "https://GHPC.DMR.gg/config/main.json";
    public const string MainConfigFallback = "https://ghpcmm.link/config/main.json";
    public const string ModConfig          = "https://GHPC.DMR.gg/config/modconfig.json";
    public const string TranslationConfig  = "https://github.com/RoyZ-iwnl/ghpc-translation";
    public const string ModI18n            = "https://GHPC.DMR.gg/config/mod_i18n.json";
}

public interface IMainConfigService
{
    MainConfig? Config { get; }
    bool IsLoaded { get; }
    Task LoadAsync();
    /// <summary>
    /// 强制重新加载主配置（等待完成），用于手动刷新
    /// </summary>
    Task ForceReloadAsync();
    string GetModConfigUrl();
    string GetTranslationConfigUrl();
    string GetModI18nUrl();
    /// <summary>
    /// 获取下发的代理服务器列表，未下发时返回 null
    /// </summary>
    List<MainConfigProxyServer>? GetRemoteProxyServers();
    /// <summary>
    /// 主动打印当前生效的主配置内容到日志
    /// </summary>
    void LogCurrentConfig();
}

public class MainConfigService : IMainConfigService
{
    private const string CacheFileName = "main_config.json";
    private static readonly TimeSpan CacheExpiry = TimeSpan.FromHours(1);

    private readonly HttpClient _httpClient;
    private readonly ISettingsService _settingsService;
    private readonly ILoggingService _loggingService;

    public MainConfig? Config { get; private set; }
    public bool IsLoaded { get; private set; }

    private string CachePath => Path.Combine(_settingsService.AppDataPath, CacheFileName);

    public MainConfigService(HttpClient httpClient, ISettingsService settingsService, ILoggingService loggingService)
    {
        _httpClient = httpClient;
        _settingsService = settingsService;
        _loggingService = loggingService;
    }

    public async Task ForceReloadAsync()
    {
        // dev模式：重新读本地路径
        if (DevMode.IsEnabled)
        {
            await LoadAsync();
            return;
        }

        // 正常模式：直接等待远程拉取，不走后台线程
        var fresh = await FetchRemoteAsync();
        if (fresh != null)
        {
            Config = fresh;
            IsLoaded = true;
            await SaveCacheAsync(fresh);
            LogConfigValues("远程(强制刷新)", fresh);
        }
        else
        {
            _loggingService.LogInfo("主配置强制刷新失败，保留当前配置");
            LogCurrentConfig();
        }
    }

    public async Task LoadAsync()
    {
        // dev模式：尝试加载 MainConfigUrlOverride（支持本地路径），打印下发内容
        if (DevMode.IsEnabled)
        {
            if (!string.IsNullOrWhiteSpace(DevMode.MainConfigUrlOverride))
            {
                var devConfig = await TryLoadFromPathOrUrlAsync(DevMode.MainConfigUrlOverride);
                if (devConfig != null)
                {
                    Config = devConfig;
                    _loggingService.LogInfo($"Dev mode: 已从手动地址加载主配置：{DevMode.MainConfigUrlOverride}");
                    LogConfigValues("Dev手动", devConfig);
                }
                else
                {
                    _loggingService.LogInfo($"Dev mode: 手动地址加载失败，使用内置默认：{DevMode.MainConfigUrlOverride}");
                    LogDevOverrides();
                }
            }
            else
            {
                _loggingService.LogInfo("Dev mode: 未设置主配置地址，使用内置默认");
                LogDevOverrides();
            }
            IsLoaded = true;
            return;
        }

        // 先尝试读取本地缓存（快速启动）
        var cached = await TryLoadCacheAsync();
        if (cached != null)
        {
            Config = cached;
            IsLoaded = true;
            LogConfigValues("缓存", cached);
        }

        // 后台拉取最新配置（不阻塞启动）
        _ = Task.Run(async () =>
        {
            var fresh = await FetchRemoteAsync();
            if (fresh != null)
            {
                Config = fresh;
                IsLoaded = true;
                await SaveCacheAsync(fresh);
                LogConfigValues("远程", fresh);
            }
            else if (Config == null)
            {
                _loggingService.LogInfo("主配置拉取失败，使用内置默认值");
            }
        });
    }

    private void LogDevOverrides()
    {
        _loggingService.LogInfo("Dev mode: 未加载主配置，当前生效数据源（内置默认）：");
        _loggingService.LogInfo($"  ModConfigUrl         = {BuiltinDefaults.ModConfig}");
        _loggingService.LogInfo($"  TranslationConfigUrl = {BuiltinDefaults.TranslationConfig}");
        _loggingService.LogInfo($"  ModI18nUrl           = {BuiltinDefaults.ModI18n}");
    }

    /// <summary>
    /// 支持本地路径或 HTTP URL 加载 MainConfig
    /// </summary>
    private async Task<MainConfig?> TryLoadFromPathOrUrlAsync(string pathOrUrl)
    {
        try
        {
            string json;
            if (pathOrUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                pathOrUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                json = await _httpClient.GetStringAsync(pathOrUrl);
            }
            else
            {
                if (!File.Exists(pathOrUrl)) return null;
                json = await File.ReadAllTextAsync(pathOrUrl);
            }
            return JsonConvert.DeserializeObject<MainConfig>(json);
        }
        catch (Exception ex)
        {
            _loggingService.LogInfo($"主配置加载失败 ({pathOrUrl}): {ex.Message}");
            return null;
        }
    }

    public void LogCurrentConfig()
    {
        if (DevMode.IsEnabled)
        {
            if (Config != null)
                LogConfigValues("Dev手动", Config);
            else
                LogDevOverrides();
        }
        else
        {
            if (Config != null)
                LogConfigValues("当前缓存", Config);
            else
                _loggingService.LogInfo("主配置尚未加载，使用内置默认值");
        }
    }

    private void LogConfigValues(string source, MainConfig config)
    {
        _loggingService.LogInfo($"主配置已从{source}加载，实际生效数据源：");
        _loggingService.LogInfo($"  ModConfigUrl         = {config.ModConfigUrl ?? BuiltinDefaults.ModConfig}{(config.ModConfigUrl == null ? " (内置默认)" : " (下发)")}");
        _loggingService.LogInfo($"  TranslationConfigUrl = {config.TranslationConfigUrl ?? BuiltinDefaults.TranslationConfig}{(config.TranslationConfigUrl == null ? " (内置默认)" : " (下发)")}");
        _loggingService.LogInfo($"  ModI18nUrl           = {config.ModI18nUrl ?? BuiltinDefaults.ModI18n}{(config.ModI18nUrl == null ? " (内置默认)" : " (下发)")}");

        if (config.ProxyServers != null && config.ProxyServers.Count > 0)
        {
            _loggingService.LogInfo($"  ProxyServers         = {config.ProxyServers.Count} 个节点已下发：");
            foreach (var ps in config.ProxyServers)
            {
                var displayZh = ps.DisplayName.TryGetValue("zh-CN", out var zh) ? zh : ps.Domain;
                _loggingService.LogInfo($"    [{ps.Id}] {ps.Domain} ({displayZh})");
            }
        }
        else
        {
            _loggingService.LogInfo("  ProxyServers         = (未下发，使用本地枚举)");
        }
    }

    private async Task<MainConfig?> FetchRemoteAsync()
    {
        var primaryUrl = DevMode.MainConfigUrlOverride ?? BuiltinDefaults.MainConfigPrimary;
        foreach (var url in new[] { primaryUrl, BuiltinDefaults.MainConfigFallback })
        {
            try
            {
                _loggingService.LogInfo($"正在拉取主配置：{url}");
                var json = await _httpClient.GetStringAsync(url);
                var config = JsonConvert.DeserializeObject<MainConfig>(json);
                if (config != null)
                {
                    _loggingService.LogInfo($"主配置拉取成功：{url}");
                    return config;
                }
            }
            catch (Exception ex)
            {
                _loggingService.LogInfo($"主配置拉取失败 ({url}): {ex.Message}");
            }
        }
        return null;
    }

    private async Task<MainConfig?> TryLoadCacheAsync()
    {
        try
        {
            if (!File.Exists(CachePath)) return null;
            var info = new FileInfo(CachePath);
            if (DateTime.UtcNow - info.LastWriteTimeUtc > CacheExpiry) return null;
            var json = await File.ReadAllTextAsync(CachePath);
            return JsonConvert.DeserializeObject<MainConfig>(json);
        }
        catch { return null; }
    }

    private async Task SaveCacheAsync(MainConfig config)
    {
        try
        {
            var json = JsonConvert.SerializeObject(config, Formatting.Indented);
            await File.WriteAllTextAsync(CachePath, json);
        }
        catch { }
    }

    // dev模式：从加载的 Config 取，否则内置默认
    // 正常模式：下发配置优先，否则内置默认
    public string GetModConfigUrl() =>
        Config?.ModConfigUrl ?? BuiltinDefaults.ModConfig;

    public string GetTranslationConfigUrl() =>
        Config?.TranslationConfigUrl ?? BuiltinDefaults.TranslationConfig;

    public string GetModI18nUrl() =>
        Config?.ModI18nUrl ?? BuiltinDefaults.ModI18n;

    // dev模式下也返回 Config 里的 ProxyServers（从手动路径加载的 main.json）
    public List<MainConfigProxyServer>? GetRemoteProxyServers() =>
        Config?.ProxyServers;
}
