using GHPC_Mod_Manager.Models;
using GHPC_Mod_Manager.Resources;
using Newtonsoft.Json;
using System.IO;
using System.Net.Http;

namespace GHPC_Mod_Manager.Services;

// 硬编码的内置默认地址，作为最后兜底
file static class BuiltinDefaults
{
    public const string MainConfigPrimary  = "https://GHPC.DMR.gg/config/main.json";
    public const string MainConfigFallback = "https://ghpc1.dmr.gg/config/main.json";
    public const string MainConfigRaw = "https://raw.githubusercontent.com/RoyZ-iwnl/GHPC-Mod-Manager-Web/refs/heads/main/config/main.json";
    public const string ModConfig          = "https://GHPC.DMR.gg/config/modconfig.json";
    public const string ModConfigFallback  = "https://ghpc1.dmr.gg/config/modconfig.json";
    public const string ModConfigRaw = "https://raw.githubusercontent.com/RoyZ-iwnl/GHPC-Mod-Manager-Web/refs/heads/main/config/modconfig.json";
    public const string TranslationConfig  = "https://github.com/RoyZ-iwnl/ghpc-translation";
    public const string ModI18n            = "https://GHPC.DMR.gg/config/mod_i18n.json";
    public const string ModI18nFallback    = "https://ghpc1.dmr.gg/config/mod_i18n.json";
    public const string ModI18nRaw = "https://raw.githubusercontent.com/RoyZ-iwnl/GHPC-Mod-Manager-Web/refs/heads/main/config/mod_i18n.json";
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
    IReadOnlyList<string> GetMainConfigUrlCandidates();
    string GetModConfigUrl();
    string GetModConfigUrlFallback();
    string? GetModConfigUrlFallback2();
    IReadOnlyList<string> GetModConfigUrlCandidates();
    string GetTranslationConfigUrl();
    string GetModI18nUrl();
    string GetModI18nUrlFallback();
    string? GetModI18nUrlFallback2();
    IReadOnlyList<string> GetModI18nUrlCandidates();
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
    private static readonly TimeSpan MainConfigRequestTimeout = TimeSpan.FromSeconds(8);
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
            _loggingService.LogInfo(Strings.MainConfigForceRefreshFailed);
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
                    _loggingService.LogInfo(string.Format(Strings.MainConfigLoadedFromManualUrl, DevMode.MainConfigUrlOverride));
                    LogConfigValues("Dev手动", devConfig);
                }
                else
                {
                    _loggingService.LogInfo(string.Format(Strings.MainConfigManualUrlLoadFailed, DevMode.MainConfigUrlOverride));
                    LogDevOverrides();
                }
            }
            else
            {
                _loggingService.LogInfo(Strings.MainConfigNoManualUrl);
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
                _loggingService.LogInfo(Strings.MainConfigFetchFailedUsingDefaults);
            }
        });
    }

    private void LogDevOverrides()
    {
        _loggingService.LogInfo(Strings.MainConfigDevModeNotLoaded);
        _loggingService.LogInfo($"  ModConfigUrl         = {BuiltinDefaults.ModConfig}");
        _loggingService.LogInfo($"  ModConfigUrlfallback = {BuiltinDefaults.ModConfigFallback}");
        _loggingService.LogInfo($"  TranslationConfigUrl = {BuiltinDefaults.TranslationConfig}");
        _loggingService.LogInfo($"  ModI18nUrl           = {BuiltinDefaults.ModI18n}");
        _loggingService.LogInfo($"  ModI18nUrlfallback   = {BuiltinDefaults.ModI18nFallback}");
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
            _loggingService.LogInfo(string.Format(Strings.MainConfigLoadFailed, pathOrUrl, ex.Message));
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
                _loggingService.LogInfo(Strings.MainConfigNotLoadedUsingDefaults);
        }
    }

    private void LogConfigValues(string source, MainConfig config)
    {
        var builtinDefault = Strings.MainConfigBuiltinDefault;
        var remote = Strings.MainConfigRemote;

        _loggingService.LogInfo(string.Format(Strings.MainConfigLoadedFromSource, source));
        _loggingService.LogInfo($"  ModConfigUrl          = {config.ModConfigUrl ?? BuiltinDefaults.ModConfig}{(config.ModConfigUrl == null ? builtinDefault : remote)}");
        _loggingService.LogInfo($"  ModConfigUrlfallback  = {config.ModConfigUrlFallback ?? BuiltinDefaults.ModConfigFallback}{(config.ModConfigUrlFallback == null ? builtinDefault : remote)}");
        if (!string.IsNullOrWhiteSpace(config.ModConfigUrlFallback2))
            _loggingService.LogInfo($"  ModConfigUrlfallback2 = {config.ModConfigUrlFallback2}{remote}");
        _loggingService.LogInfo($"  TranslationConfigUrl  = {config.TranslationConfigUrl ?? BuiltinDefaults.TranslationConfig}{(config.TranslationConfigUrl == null ? builtinDefault : remote)}");
        _loggingService.LogInfo($"  ModI18nUrl            = {config.ModI18nUrl ?? BuiltinDefaults.ModI18n}{(config.ModI18nUrl == null ? builtinDefault : remote)}");
        _loggingService.LogInfo($"  ModI18nUrlfallback    = {config.ModI18nUrlFallback ?? BuiltinDefaults.ModI18nFallback}{(config.ModI18nUrlFallback == null ? builtinDefault : remote)}");
        if (!string.IsNullOrWhiteSpace(config.ModI18nUrlFallback2))
            _loggingService.LogInfo($"  ModI18nUrlfallback2   = {config.ModI18nUrlFallback2}{remote}");

        if (config.ProxyServers != null && config.ProxyServers.Count > 0)
        {
            _loggingService.LogInfo(string.Format(Strings.MainConfigProxyServersDelivered, config.ProxyServers.Count));
            foreach (var ps in config.ProxyServers)
            {
                var displayZh = ps.DisplayName.TryGetValue("zh-CN", out var zh) ? zh : ps.Domain;
                _loggingService.LogInfo($"    [{ps.Id}] {ps.Domain} ({displayZh})");
            }
        }
        else
        {
            _loggingService.LogInfo($"  ProxyServers          = {Strings.MainConfigProxyServersNotDelivered}");
        }
    }

    private async Task<MainConfig?> FetchRemoteAsync()
    {
        var urls = GetMainConfigUrlCandidates();
        for (var i = 0; i < urls.Count; i++)
        {
            var url = urls[i];
            var result = await TryFetchMainConfigFromUrlAsync(url);
            if (result.config != null)
                return result.config;

            var hasNext = i < urls.Count - 1;
            _loggingService.LogInfo(string.Format(Strings.MainConfigFetchUrlFailed, url, result.error));
            if (hasNext)
                _loggingService.LogInfo(Strings.MainConfigTriggerFallback);
        }
        return null;
    }

    private async Task<(MainConfig? config, string error)> TryFetchMainConfigFromUrlAsync(string url)
    {
        try
        {
            _loggingService.LogInfo(string.Format(Strings.MainConfigFetchingFrom, url));
            using var cts = new CancellationTokenSource(MainConfigRequestTimeout);
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!response.IsSuccessStatusCode)
                return (null, $"HTTP {(int)response.StatusCode}");

            var json = await response.Content.ReadAsStringAsync(cts.Token);
            var config = JsonConvert.DeserializeObject<MainConfig>(json);
            if (config == null)
                return (null, Strings.MainConfigDeserializationFailed);

            _loggingService.LogInfo(string.Format(Strings.MainConfigFetchSuccess, url));
            return (config, string.Empty);
        }
        catch (OperationCanceledException)
        {
            return (null, string.Format(Strings.MainConfigFetchTimeout, MainConfigRequestTimeout.TotalSeconds.ToString("0")));
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    private static List<string> BuildUniqueUrlList(params string?[] urls)
    {
        var result = new List<string>();
        foreach (var url in urls)
        {
            if (string.IsNullOrWhiteSpace(url))
                continue;

            if (result.Any(existing => string.Equals(existing, url, StringComparison.OrdinalIgnoreCase)))
                continue;

            result.Add(url);
        }

        return result;
    }

    private List<string> BuildRawLastResortCandidates(string rawUrl)
    {
        var result = new List<string>();
        if (_settingsService.Settings.UseGitHubProxy)
        {
            var proxyDomain = GetSelectedProxyDomain();
            if (!string.IsNullOrWhiteSpace(proxyDomain))
                result.Add($"https://{proxyDomain}/{rawUrl}");
        }

        result.Add(rawUrl);
        return result;
    }

    private static string? CandidateAt(IReadOnlyList<string> candidates, int index) =>
        index >= 0 && index < candidates.Count ? candidates[index] : null;

    private string GetSelectedProxyDomain()
    {
        // 优先使用主配置下发节点（如果当前已加载）
        var selectedId = _settingsService.Settings.GitHubProxyServer.ToString();
        var remote = Config?.ProxyServers;
        if (remote != null)
        {
            var match = remote.FirstOrDefault(s =>
                string.Equals(s.Id, selectedId, StringComparison.OrdinalIgnoreCase));
            if (match != null && !string.IsNullOrWhiteSpace(match.Domain))
                return match.Domain;
        }

        // 回退到本地枚举映射
        return _settingsService.Settings.GitHubProxyServer switch
        {
            GitHubProxyServer.GhDmrGg => "gh.dmr.gg",
            GitHubProxyServer.Gh1DmrGg => "gh1.dmr.gg",
            GitHubProxyServer.EdgeOneGhProxyCom => "edgeone.gh-proxy.com",
            GitHubProxyServer.GhProxyCom => "gh-proxy.com",
            GitHubProxyServer.HkGhProxyCom => "hk.gh-proxy.com",
            GitHubProxyServer.CdnGhProxyCom => "cdn.gh-proxy.com",
            _ => "gh.dmr.gg"
        };
    }

    public IReadOnlyList<string> GetMainConfigUrlCandidates()
    {
        var rawLastResort = BuildRawLastResortCandidates(BuiltinDefaults.MainConfigRaw);
        return BuildUniqueUrlList(
            DevMode.MainConfigUrlOverride ?? BuiltinDefaults.MainConfigPrimary,
            BuiltinDefaults.MainConfigFallback,
            CandidateAt(rawLastResort, 0),
            CandidateAt(rawLastResort, 1)
        );
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

    public string GetModConfigUrlFallback() =>
        Config?.ModConfigUrlFallback ?? BuiltinDefaults.ModConfigFallback;

    public string? GetModConfigUrlFallback2() =>
        Config?.ModConfigUrlFallback2;

    public IReadOnlyList<string> GetModConfigUrlCandidates()
    {
        var rawLastResort = BuildRawLastResortCandidates(BuiltinDefaults.ModConfigRaw);
        return BuildUniqueUrlList(
            GetModConfigUrl(),
            GetModConfigUrlFallback(),
            GetModConfigUrlFallback2(),
            CandidateAt(rawLastResort, 0),
            CandidateAt(rawLastResort, 1)
        );
    }

    public string GetTranslationConfigUrl() =>
        Config?.TranslationConfigUrl ?? BuiltinDefaults.TranslationConfig;

    public string GetModI18nUrl() =>
        Config?.ModI18nUrl ?? BuiltinDefaults.ModI18n;

    public string GetModI18nUrlFallback() =>
        Config?.ModI18nUrlFallback ?? BuiltinDefaults.ModI18nFallback;

    public string? GetModI18nUrlFallback2() =>
        Config?.ModI18nUrlFallback2;

    public IReadOnlyList<string> GetModI18nUrlCandidates()
    {
        var rawLastResort = BuildRawLastResortCandidates(BuiltinDefaults.ModI18nRaw);
        return BuildUniqueUrlList(
            GetModI18nUrl(),
            GetModI18nUrlFallback(),
            GetModI18nUrlFallback2(),
            CandidateAt(rawLastResort, 0),
            CandidateAt(rawLastResort, 1)
        );
    }

    // dev模式下也返回 Config 里的 ProxyServers（从手动路径加载的 main.json）
    public List<MainConfigProxyServer>? GetRemoteProxyServers() =>
        Config?.ProxyServers;
}
