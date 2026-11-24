using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Xml.Linq;
using GHPC_Mod_Manager.Models;
using Newtonsoft.Json;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using GHPC_Mod_Manager.Resources;
using System.Windows;
using GHPC_Mod_Manager.Helpers;

namespace GHPC_Mod_Manager.Services.Network;

public interface INetworkService
{
    Task<bool> CheckNetworkConnectionAsync(); // 主程序启动检测main.json
    Task<bool> CheckGitHubConnectionAsync(); // SetupWizard检测GitHub连接
    Task<List<ModConfig>> GetModConfigAsync(string url, bool forceRefresh = false);
    Task<ModI18nManager> GetModI18nConfigAsync(string url, bool forceRefresh = false);
    Task<List<GitHubRelease>> GetGitHubReleasesAsync(string repoOwner, string repoName, bool forceRefresh = false);
    /// <summary>
    /// 只取最新版本（Tag + 发布时间），优先走不受API限额的Releases订阅源，失败再回退API
    /// </summary>
    Task<GitHubRelease?> GetLatestGitHubReleaseAsync(string repoOwner, string repoName, bool forceRefresh = false);
    /// <summary>
    /// 查询GitHub API剩余额度（rate_limit接口本身不计入额度），token为空时查匿名额度
    /// </summary>
    Task<GitHubRateLimitStatus> GetGitHubRateLimitAsync(string? token);
    /// <summary>
    /// 读取Mod作者仓库里的 ghpcmm.json（作者自己维护的推荐等信息），没有该文件或读取失败返回null
    /// </summary>
    Task<CreatorModFile?> GetCreatorModFileAsync(string repoOwner, string repoName);
    Task<(string Content, string BaseUrl)> GetGitHubReadmeAsync(string repoOwner, string repoName);
    Task<byte[]> DownloadFileAsync(string url, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default, long? expectedSize = null, string? expectedDigest = null, string? assetName = null, string? displayName = null, string? sourceText = null);
    /// <summary>直接下载，不进入全局下载队列，仅用于 SetupWizard 初始化流程。</summary>
    Task<byte[]> DownloadFileDirectAsync(string url, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default, long? expectedSize = null, string? expectedDigest = null, string? assetName = null);
    void ClearCache(); // Clear all cached data (including GitHub persistent cache)
    void ClearSessionCache(); // Clear session cache for non-GitHub resources
    void ClearRateLimitBlocks(); // Clear rate limit blocks to allow manual refresh
    HttpClient HttpClient { get; } // 暴露HttpClient用于代理请求
}

public class NetworkService : INetworkService
{
    private readonly HttpClient _httpClient;

    public HttpClient HttpClient => _httpClient;
    private readonly ILoggingService _loggingService;
    private readonly ISettingsService _settingsService;
    private readonly IMainConfigService _mainConfigService;
    private readonly IDownloadQueueService _downloadQueueService;
    private readonly INavigationService _navigationService;

    // token 被拒时每次启动只提示一次
    private static bool _invalidTokenWarned;
    // 最新版本缓存有效期：订阅源不占API额度，可以比完整Releases缓存短
    private static readonly TimeSpan LatestReleaseCacheExpiry = TimeSpan.FromHours(24);

    // github.com 网页来源（订阅源/跳转）每条线路的连续失败次数，键 = 来源|线路（direct 或代理域名）
    // 被墙或代理不放行时，连续失败3次后本次启动跳过这条线路，换下一条，避免每个Mod都白等
    private static readonly ConcurrentDictionary<string, int> _webSourceFailures = new();
    private static readonly ConcurrentDictionary<string, bool> _webSourceLogged = new();
    private const int WebSourceMaxFailures = 3;
    // Mod作者放在自己仓库根目录的信息文件
    private const string CreatorFileName = "ghpcmm.json";
    private static readonly TimeSpan WebSourceTimeout = TimeSpan.FromSeconds(8);
    // 同时最多8个订阅源/跳转请求，100多个Mod一起刷新时不压垮代理
    private static readonly SemaphoreSlim _webSourceGate = new(8, 8);
    private List<string>? _builtinProxyDomains;

    // Rate limit tracking
    private static readonly ConcurrentDictionary<string, DateTime> _rateLimitBlocks = new();
    private static DateTime _lastRateLimitWarning = DateTime.MinValue;
    private const int RateLimitWarningCooldownMinutes = 5; // Show warning at most once every 5 minutes

    // Cache expiry times
    private readonly TimeSpan _githubApiCacheExpiry = TimeSpan.FromHours(24); // GitHub API with rate limits
    private static readonly TimeSpan DownloadInactivityTimeout = TimeSpan.FromSeconds(30);
    private const int DownloadRetryAttempts = 3;

    // 本次启动的内存缓存（非GitHub资源）
    private readonly ConcurrentDictionary<string, object> _sessionCache = new();

    public NetworkService(HttpClient httpClient, ILoggingService loggingService, ISettingsService settingsService, IMainConfigService mainConfigService, IDownloadQueueService downloadQueueService, INavigationService navigationService)
    {
        _navigationService = navigationService;
        _httpClient = httpClient;
        _loggingService = loggingService;
        _settingsService = settingsService;
        _mainConfigService = mainConfigService;
        _downloadQueueService = downloadQueueService;

        _downloadQueueService.RegisterDefaultDownloader(
            (request, queuedProgress, queuedToken) => DownloadFileCoreAsync(
                request.Url,
                queuedProgress,
                queuedToken,
                request.ExpectedSize,
                request.ExpectedDigest,
                request.AssetName));

        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/139.0.0.0 Safari/537.36 Edg/139.0.0.0");
        _httpClient.Timeout = TimeSpan.FromMinutes(10);
    }

    /// <summary>
    /// Apply GitHub proxy if enabled in settings for supported GitHub URLs
    /// </summary>
    private string ApplyGitHubProxy(string originalUrl)
    {
        if (!_settingsService.Settings.UseGitHubProxy)
        {
            _loggingService.LogInfo(Strings.GitHubProxyDisabled, originalUrl);
            return originalUrl;
        }

        try
        {
            // Check if it's a supported GitHub URL
            var uri = new Uri(originalUrl);
            var isGitHubUrl = uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
                             uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase) ||
                             uri.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
                             uri.Host.Equals("gist.githubusercontent.com", StringComparison.OrdinalIgnoreCase);

            if (!isGitHubUrl)
            {
                _loggingService.LogInfo(Strings.URLNotGitHub, originalUrl);
                return originalUrl;
            }

            // Get selected proxy server
            var proxyDomain = GetProxyDomain(_settingsService.Settings.GitHubProxyServer);
            
            // Apply proxy prefix for supported URL patterns or git clone URLs
            var path = uri.PathAndQuery;
            var supportedPatterns = new[]
            {
                "/archive/", // Branch/tag source code archives
                "/releases/download/", // Release files
                "/blob/", // File content
                "/raw/" // Raw file content (gist support)
            };

            bool isSupported = uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase) ||
                              supportedPatterns.Any(pattern => path.Contains(pattern, StringComparison.OrdinalIgnoreCase)) ||
                              originalUrl.EndsWith(".git", StringComparison.OrdinalIgnoreCase); // Git clone URLs
            
            if (isSupported)
            {
                var proxyUrl = $"https://{proxyDomain}/{originalUrl}";
                _loggingService.LogInfo(Strings.GitHubProxyTransforming, originalUrl, proxyUrl);
                return proxyUrl;
            }
            else
            {
                _loggingService.LogInfo(Strings.URLPatternNotSupported, originalUrl, path);
                return originalUrl;
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.ErrorApplyingGitHubProxy, originalUrl);
            return originalUrl;
        }
    }

    /// <summary>
    /// Get proxy domain based on selected proxy server.
    /// 优先使用主配置下发的代理列表，回退到本地枚举。
    /// </summary>
    private string GetProxyDomain(GitHubProxyServer proxyServer)
    {
        // 优先使用下发的代理列表
        var remoteServers = _mainConfigService.GetRemoteProxyServers();
        if (remoteServers != null)
        {
            var match = remoteServers.FirstOrDefault(s =>
                string.Equals(s.Id, proxyServer.ToString(), StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                _loggingService.LogInfo($"代理域名来自下发配置: [{match.Id}] {match.Domain}");
                return match.Domain;
            }
        }

        // 回退到本地枚举
        return proxyServer switch
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

    public async Task<bool> CheckNetworkConnectionAsync()
    {
        // 主程序启动检测：使用 main.json 的 fallback 链测试连通性
        var urls = _mainConfigService.GetMainConfigUrlCandidates();

        foreach (var url in urls)
        {
            try
            {
                _loggingService.LogInfo(Strings.TestingDirectNetworkConnection, url);

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                using var request = new HttpRequestMessage(HttpMethod.Head, url);
                using var response = await _httpClient.SendAsync(request, cts.Token);

                if (response.IsSuccessStatusCode)
                {
                    _loggingService.LogInfo(Strings.NetworkTestSuccessfulViaDirect);
                    return true;
                }
            }
            catch (Exception ex)
            {
                _loggingService.LogError(ex, Strings.NetworkTestFailed, ex.Message);
            }
        }

        _loggingService.LogError(Strings.NetworkCheckFailed);
        return false;
    }

    public async Task<bool> CheckGitHubConnectionAsync()
    {
        // SetupWizard检测：测试GitHub API连通性（遵循代理设置）
        const string testUrl = "https://api.github.com/repos/RoyZ-iwnl/GHPC-Mod-Manager/releases/latest";

        if (_settingsService.Settings.UseGitHubProxy)
        {
            var proxyDomain = GetProxyDomain(_settingsService.Settings.GitHubProxyServer);
            var proxyUrl = $"https://{proxyDomain}/https://api.github.com/repos/RoyZ-iwnl/GHPC-Mod-Manager/releases/latest";
            _loggingService.LogInfo(Strings.TestingNetworkConnectionThroughProxy, proxyDomain);
            return await TestConnectionAsync(proxyUrl);
        }
        else
        {
            _loggingService.LogInfo(Strings.TestingDirectNetworkConnection, testUrl);
            return await TestConnectionAsync(testUrl);
        }
    }

    private async Task<bool> TestConnectionAsync(string url)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", "GHPC-Mod-Manager");

            var token = _settingsService.Settings.GitHubApiToken;
            if (!string.IsNullOrWhiteSpace(token))
                request.Headers.Add("Authorization", $"token {token}");

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            var content = await response.Content.ReadAsStringAsync(cts.Token);

            if (content.Contains("API rate limit exceeded") || content.Contains("rate limit"))
            {
                _loggingService.LogError(Strings.NetworkCheckFailed);
                return false;
            }

            if (response.IsSuccessStatusCode && (content.Contains("tag_name") || content.Contains("\"id\":")))
            {
                var isProxy = url.Contains("dmr.gg") || url.Contains("gh-proxy.com");
                _loggingService.LogInfo(isProxy ? Strings.NetworkTestSuccessfulViaProxy : Strings.NetworkTestSuccessfulViaDirect);
                return true;
            }

            _loggingService.LogError(Strings.NetworkCheckFailed);
            return false;
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.NetworkTestFailed, ex.Message);
            _loggingService.LogError(ex, Strings.NetworkCheckFailed);
            return false;
        }
    }

    public async Task<List<ModConfig>> GetModConfigAsync(string url, bool forceRefresh = false)
    {
        try
        {
            // Check if it's a local file path
            if (IsLocalPath(url))
            {
                _loggingService.LogInfo(Strings.LoadingModConfigFromLocalPath, url);
                return await LoadModConfigFromLocalPath(url);
            }

            var cacheKey = $"modconfig_{url}";

            // 优先使用会话缓存（非GitHub资源）
            if (!forceRefresh && _sessionCache.TryGetValue(cacheKey, out var sessionCached))
            {
                _loggingService.LogInfo(Strings.ModConfigLoadedFromCache);
                return (List<ModConfig>)sessionCached;
            }

            _loggingService.LogInfo(Strings.FetchingModConfig, url);
            var json = await _httpClient.GetStringAsync(url);
            var configs = JsonConvert.DeserializeObject<List<ModConfig>>(json);
            var result = configs ?? new List<ModConfig>();

            // 保存到会话缓存
            _sessionCache[cacheKey] = result;

            return result;
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.ModConfigFetchError, url);

            if (IsLocalPath(url))
                return new List<ModConfig>();

            // 失败时尝试从会话缓存读取
            var cacheKey = $"modconfig_{url}";
            if (_sessionCache.TryGetValue(cacheKey, out var sessionCached))
            {
                return (List<ModConfig>)sessionCached;
            }

            return new List<ModConfig>();
        }
    }

    public async Task<ModI18nManager> GetModI18nConfigAsync(string url, bool forceRefresh = false)
    {
        try
        {
            if (IsLocalPath(url))
            {
                _loggingService.LogInfo(Strings.LoadingModI18nConfigFromLocalPath, url);
                return await LoadModI18nConfigFromLocalPath(url);
            }

            var cacheKey = $"modi18nconfig_{url}";

            // 优先使用会话缓存（避免同一次启动重复请求）
            if (!forceRefresh && _sessionCache.TryGetValue(cacheKey, out var sessionCached))
            {
                _loggingService.LogInfo(Strings.ModI18nConfigLoadedFromCache);
                return (ModI18nManager)sessionCached;
            }

            _loggingService.LogInfo(Strings.FetchingModI18nConfig, url);
            var json = await _httpClient.GetStringAsync(url);
            var config = JsonConvert.DeserializeObject<ModI18nManager>(json);
            var result = config ?? new ModI18nManager();

            // 保存到会话缓存（上层Service会自己处理持久化）
            _sessionCache[cacheKey] = result;
            _loggingService.LogInfo(Strings.ModI18nConfigRefreshed);

            return result;
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.ModI18nConfigFetchError, url);

            if (IsLocalPath(url))
                return new ModI18nManager();

            // 失败时尝试从会话缓存读取
            var cacheKey = $"modi18nconfig_{url}";
            if (_sessionCache.TryGetValue(cacheKey, out var sessionCached))
            {
                return (ModI18nManager)sessionCached;
            }

            return new ModI18nManager();
        }
    }

    public async Task<(string Content, string BaseUrl)> GetGitHubReadmeAsync(string repoOwner, string repoName)
    {
        var apiUrl = $"https://api.github.com/repos/{Uri.EscapeDataString(repoOwner)}/{Uri.EscapeDataString(repoName)}/readme";
        var hasToken = !string.IsNullOrWhiteSpace(_settingsService.Settings.GitHubApiToken);
        var requestUrl = _settingsService.Settings.UseGitHubProxy && !hasToken
            ? ApplyGitHubProxy(apiUrl)
            : apiUrl;

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        if (hasToken)
            request.Headers.Add("Authorization", $"Bearer {_settingsService.Settings.GitHubApiToken}");

        using var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        var readme = JsonConvert.DeserializeObject<GitHubReadmeResponse>(json)
                     ?? throw new InvalidDataException("GitHub returned an empty README response.");
        var content = System.Text.RegularExpressions.Regex.Replace(readme.Content, @"\s+", string.Empty);

        try
        {
            var markdown = Encoding.UTF8.GetString(Convert.FromBase64String(content));
            var baseUrl = GetGitHubReadmeBaseUrl(readme, repoOwner, repoName);
            return (markdown, baseUrl);
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("GitHub returned invalid README content.", ex);
        }
    }

    private static string GetGitHubReadmeBaseUrl(GitHubReadmeResponse readme, string owner, string repo)
    {
        if (Uri.TryCreate(readme.DownloadUrl, UriKind.Absolute, out var downloadUri) &&
            downloadUri.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
        {
            var segments = downloadUri.AbsolutePath.Trim('/').Split('/');
            if (segments.Length >= 3)
                return $"https://raw.githubusercontent.com/{string.Join('/', segments.Take(segments.Length - 1))}/";
        }

        var branch = string.IsNullOrWhiteSpace(readme.Ref) ? "HEAD" : Uri.EscapeDataString(readme.Ref);
        var directory = Path.GetDirectoryName(readme.Path.Replace('/', Path.DirectorySeparatorChar))?.Replace('\\', '/');
        var path = string.IsNullOrWhiteSpace(directory) ? string.Empty : $"{directory}/";
        return $"https://github.com/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/blob/{branch}/{path}";
    }

    private sealed class GitHubReadmeResponse
    {
        [JsonProperty("content")]
        public string Content { get; set; } = string.Empty;

        [JsonProperty("download_url")]
        public string DownloadUrl { get; set; } = string.Empty;

        [JsonProperty("path")]
        public string Path { get; set; } = "README.md";

        [JsonProperty("ref")]
        public string Ref { get; set; } = string.Empty;
    }

    public async Task<List<GitHubRelease>> GetGitHubReleasesAsync(string repoOwner, string repoName, bool forceRefresh = false)
    {
        try
        {
            var cacheKey = $"github_releases_{repoOwner}_{repoName}";
            
            // Check persistent cache first (unless force refresh is requested)
            if (!forceRefresh)
            {
                var cachedReleases = await GetGitHubReleasesFromPersistentCacheAsync(repoOwner, repoName);
                if (cachedReleases != null)
                {
                    _loggingService.LogInfo(Strings.GitHubReleasesLoadedFromPersistentCache, repoOwner, repoName);
                    return cachedReleases;
                }
            }
            
            // Check if we're currently rate limited for this repository
            // GitHub primary rate limit 每小时重置，标记项 1 小时后过期允许重试
            var rateLimitKey = $"github_api_{repoOwner}_{repoName}";
            if (_rateLimitBlocks.TryGetValue(rateLimitKey, out var blockedAt) && blockedAt > DateTime.Now.AddHours(-1))
            {
                _loggingService.LogInfo(Strings.SkippingGitHubApiDueToRateLimit, repoOwner, repoName);
                // Try to return stale cache if available
                var staleCache = await GetGitHubReleasesFromPersistentCacheAsync(repoOwner, repoName, ignoreExpiry: true);
                return staleCache ?? new List<GitHubRelease>();
            }
            // 过期项清理，允许继续走 API 重试（避免永久跳过）
            _rateLimitBlocks.TryRemove(rateLimitKey, out _);
            
            // Make API request
            var token = _settingsService.Settings.GitHubApiToken;
            var hasToken = !string.IsNullOrWhiteSpace(token);
            var apiUrl = $"https://api.github.com/repos/{repoOwner}/{repoName}/releases";
            GitHubApiResponse apiResponse;

            // 有token时强制直连，忽略代理设置
            if (hasToken)
            {
                _loggingService.LogInfo(Strings.FetchingGitHubReleasesWithToken, apiUrl);
                apiResponse = await RequestGitHubReleasesAsync(apiUrl, token);

                // token 过期或填错：提示一次，并改用匿名请求，避免所有Mod版本都变成"未知"
                if (apiResponse.StatusCode == HttpStatusCode.Unauthorized)
                {
                    await ShowInvalidTokenWarningAsync();
                    apiResponse = await RequestGitHubReleasesAsync(apiUrl, null);
                }
            }
            else if (!_settingsService.Settings.UseGitHubProxy)
            {
                _loggingService.LogInfo(Strings.FetchingGitHubReleases, apiUrl);
                apiResponse = await RequestGitHubReleasesAsync(apiUrl, null);
            }
            else
            {
                // Use proxy for GitHub API access
                var proxyDomain = GetProxyDomain(_settingsService.Settings.GitHubProxyServer);
                var url = $"https://{proxyDomain}/{apiUrl}";
                _loggingService.LogInfo(Strings.FetchingGitHubReleases, url);
                apiResponse = await RequestGitHubReleasesAsync(url, null);
            }

            if (apiResponse.IsRateLimited)
            {
                _rateLimitBlocks[rateLimitKey] = DateTime.Now;
                _loggingService.LogWarning(Strings.GitHubReleasesFetchError, repoOwner, repoName);
                await ShowRateLimitWarningAsync(apiResponse.RateLimitReset);

                var staleCache = await GetGitHubReleasesFromPersistentCacheAsync(repoOwner, repoName, ignoreExpiry: true);
                return staleCache ?? new List<GitHubRelease>();
            }

            if (apiResponse.Releases == null)
                throw new HttpRequestException($"GitHub API returned {(int?)apiResponse.StatusCode} for {repoOwner}/{repoName}");

            var result = apiResponse.Releases;

            // 按 PublishedAt 倒序排列，确保调用方 FirstOrDefault() 取到最新发布版本
            // 而非依赖 GitHub API 默认的 created_at 排序（修改发布时间后两者顺序可能不一致）
            result = result.OrderByDescending(r => r.PublishedAt).ToList();

            // Save to persistent cache (always save fresh data)
            await SaveGitHubReleasesToPersistentCacheAsync(repoOwner, repoName, result);

            return result;
        }
        catch (HttpRequestException httpEx) when (httpEx.Message.Contains("403") || httpEx.Message.Contains("rate limit"))
        {
            _loggingService.LogError(httpEx, Strings.GitHubReleasesFetchError, repoOwner, repoName);
            
            // Mark this repository as rate limited
            var rateLimitKey = $"github_api_{repoOwner}_{repoName}";
            _rateLimitBlocks[rateLimitKey] = DateTime.Now;
            
            // Show rate limit popup with localized message
            await ShowRateLimitWarningAsync(null);
            
            // Try to return stale cache if available
            var staleCache = await GetGitHubReleasesFromPersistentCacheAsync(repoOwner, repoName, ignoreExpiry: true);
            return staleCache ?? new List<GitHubRelease>();
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.GitHubReleasesFetchError, repoOwner, repoName);
            
            // Try to return stale cache if available
            var staleCache = await GetGitHubReleasesFromPersistentCacheAsync(repoOwner, repoName, ignoreExpiry: true);
            return staleCache ?? new List<GitHubRelease>();
        }
    }

    public Task<byte[]> DownloadFileAsync(string url, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default, long? expectedSize = null, string? expectedDigest = null, string? assetName = null, string? displayName = null, string? sourceText = null)
    {
        var fileName = string.IsNullOrWhiteSpace(assetName) ? GetDownloadTargetName(url) : assetName;
        return _downloadQueueService.EnqueueAsync(
            new DownloadRequest
            {
                Url = url,
                FileName = fileName,
                Source = InferDownloadSource(url, fileName),
                DisplayName = displayName,
                SourceText = sourceText,
                ExpectedSize = expectedSize,
                ExpectedDigest = expectedDigest,
                AssetName = assetName
            },
            (queuedProgress, queuedToken) => DownloadFileCoreAsync(url, queuedProgress, queuedToken, expectedSize, expectedDigest, assetName),
            progress,
            cancellationToken);
    }

    public Task<byte[]> DownloadFileDirectAsync(string url, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default, long? expectedSize = null, string? expectedDigest = null, string? assetName = null)
    {
        return DownloadFileCoreAsync(url, progress, cancellationToken, expectedSize, expectedDigest, assetName);
    }

    private static string InferDownloadSource(string url, string fileName)
    {
        var value = $"{url} {fileName}".ToLowerInvariant();
        if (value.Contains("melonloader")) return "MelonLoader";
        if (value.Contains("translation") || value.Contains("xunity") || value.Contains("translator")) return "Translation";
        if (value.EndsWith(".exe") || value.Contains("manager-update")) return "Application update";
        if (value.Contains("cleanup") || value.Contains("version")) return "Background maintenance";
        return "MOD / resource";
    }

    private async Task<byte[]> DownloadFileCoreAsync(string url, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken, long? expectedSize, string? expectedDigest, string? assetName)
    {
        var finalUrl = ApplyGitHubProxy(url);
        var candidateUrls = finalUrl != url
            ? new[] { finalUrl, url }.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : new[] { url }.ToList();

        var downloadTargetName = string.IsNullOrWhiteSpace(assetName)
            ? GetDownloadTargetName(url)
            : assetName;

        _loggingService.LogInfo(Strings.DownloadStarted, finalUrl);

        Exception? lastException = null;

        for (int attempt = 1; attempt <= DownloadRetryAttempts; attempt++)
        {
            foreach (var candidateUrl in candidateUrls)
            {
                try
                {
                    if (candidateUrl != url)
                    {
                        var proxyDomain = GetProxyDomain(_settingsService.Settings.GitHubProxyServer);
                        await TestProxyConnectivityAsync(proxyDomain, cancellationToken);
                    }

                    var downloadData = await DownloadFromUrlAsync(candidateUrl, progress, cancellationToken);
                    ValidateDownloadIntegrity(downloadData, expectedSize, expectedDigest, downloadTargetName);
                    // 下载完成后明确上报100%，消除进度条因最后一次上报偏差留下的缺口
                    progress?.Report(new DownloadProgress
                    {
                        BytesReceived = downloadData.LongLength,
                        TotalBytes = downloadData.LongLength,
                        ProgressPercentage = 100.0,
                        SpeedBytesPerSecond = 0
                    });
                    return downloadData;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    lastException = ex;

                    if (candidateUrl != url)
                    {
                        _loggingService.LogWarning(Strings.GitHubProxyFailed, ex.Message, url);
                    }

                    var isLastCandidate = candidateUrl == candidateUrls.Last();
                    var isLastAttempt = attempt == DownloadRetryAttempts;
                    if (!isLastAttempt || !isLastCandidate)
                    {
                        _loggingService.LogWarning("DownloadAttemptRetrying", attempt, DownloadRetryAttempts, downloadTargetName, ex.Message);
                    }
                }
            }

            if (attempt < DownloadRetryAttempts)
            {
                var delayMs = (int)TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)).TotalMilliseconds;
                await Task.Delay(delayMs, cancellationToken);
            }
        }

        if (lastException != null)
        {
            _loggingService.LogError(lastException, Strings.DownloadError, url);
            throw lastException;
        }

        throw new InvalidOperationException(string.Format(GetResourceString("DownloadFailedWithoutException", "Download failed without exception: {0}"), downloadTargetName));
    }

    private async Task TestProxyConnectivityAsync(string hostname, CancellationToken cancellationToken)
    {
        try
        {
            // Test proxy connectivity by trying a small, commonly available file
            // Use VS Code's package.json which is likely to exist and be small
            var testUrl = $"https://{hostname}/https://raw.githubusercontent.com/microsoft/vscode/main/package.json";
            var testRequest = new HttpRequestMessage(HttpMethod.Head, testUrl);
            
            // Add realistic headers to avoid bot detection
            testRequest.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");
            testRequest.Headers.Add("Accept", "*/*");
            testRequest.Headers.Add("Accept-Language", "en-US,en;q=0.9");
            
            using var testResponse = await _httpClient.SendAsync(testRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            
            // Accept both 200 (success) and 404 (file not found) as valid proxy responses
            // 404 means the proxy is working but the specific file doesn't exist
            // Only fail on connectivity errors (5xx) or proxy errors (530, etc.)
            if (testResponse.IsSuccessStatusCode || testResponse.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _loggingService.LogInfo(Strings.ProxyConnectivityTestSuccessful, hostname);
            }
            else
            {
                throw new HttpRequestException($"Proxy returned status: {testResponse.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.ProxyConnectivityTestFailed, hostname, ex.Message);
            throw;
        }
    }

    private async Task<byte[]> DownloadFromUrlAsync(string url, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        // First, test if server supports range requests
        var supportsRangeRequests = await TestRangeRequestSupportAsync(url, cancellationToken);
        
        // Get file size with HEAD request
        var headRequest = new HttpRequestMessage(HttpMethod.Head, url);
        using var headResponse = await _httpClient.SendAsync(headRequest, cancellationToken);
        headResponse.EnsureSuccessStatusCode();
        
        var totalBytes = headResponse.Content.Headers.ContentLength ?? -1;
        
        // Use multi-threaded download only if:
        // 1. Server supports range requests
        // 2. File size is known and >= 512KB
        if (supportsRangeRequests && totalBytes >= 512 * 1024)
        {
            _loggingService.LogInfo(Strings.UsingMultiThreadedDownload, $"{totalBytes:N0}");
            return await DownloadFileMultiThreadedAsync(url, totalBytes, progress, cancellationToken);
        }
        else
        {
            var reason = !supportsRangeRequests ? "server doesn't support range requests" :
                       totalBytes < 512 * 1024 ? "file is small" : "unknown file size";
            _loggingService.LogInfo(Strings.UsingSingleThreadedDownload, reason, $"{totalBytes:N0}");
            return await DownloadFileSequentialAsync(url, progress, cancellationToken, totalBytes);
        }
    }

    private async Task<bool> TestRangeRequestSupportAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            // Test with a small range request (first 1024 bytes)
            using var testRequest = new HttpRequestMessage(HttpMethod.Get, url);
            testRequest.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 1023);
            
            using var testResponse = await _httpClient.SendAsync(testRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            
            // Server supports range requests if it returns 206 (Partial Content)
            var supportsRange = testResponse.StatusCode == System.Net.HttpStatusCode.PartialContent;
            _loggingService.LogInfo(Strings.RangeRequestSupportTest, supportsRange ? "Supported" : "Not supported");
            
            return supportsRange;
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning(Strings.RangeRequestTestFailed, ex.Message);
            return false;
        }
    }

    private async Task<byte[]> DownloadFileSequentialAsync(string url, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken, long totalBytes)
    {
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        // Update total bytes if we got it from the actual response
        if (totalBytes < 0)
            totalBytes = response.Content.Headers.ContentLength ?? -1;

        using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var memoryStream = new MemoryStream();

        var buffer = new byte[8192];
        var totalBytesRead = 0L;
        var startTime = DateTime.Now;
        var lastUpdateTime = startTime;
        var lastBytesRead = 0L;

        while (true)
        {
            var bytesRead = await ReadWithInactivityTimeoutAsync(contentStream, buffer, cancellationToken, GetDownloadTargetName(url));
            if (bytesRead == 0) break;

            await memoryStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
            totalBytesRead += bytesRead;

            if (progress != null && totalBytes > 0)
            {
                var currentTime = DateTime.Now;
                var elapsedTime = currentTime - startTime;
                
                // Calculate speed (update every 100ms to avoid too frequent updates)
                var speedBytesPerSecond = 0.0;
                if (elapsedTime.TotalSeconds > 0.1)
                {
                    var timeSinceLastUpdate = currentTime - lastUpdateTime;
                    if (timeSinceLastUpdate.TotalMilliseconds >= 100)
                    {
                        var bytesSinceLastUpdate = totalBytesRead - lastBytesRead;
                        if (timeSinceLastUpdate.TotalSeconds > 0)
                        {
                            speedBytesPerSecond = bytesSinceLastUpdate / timeSinceLastUpdate.TotalSeconds;
                        }
                        lastUpdateTime = currentTime;
                        lastBytesRead = totalBytesRead;
                    }
                    else
                    {
                        speedBytesPerSecond = totalBytesRead / elapsedTime.TotalSeconds;
                    }
                }

                // 进度百分比上限为 100%，防止服务器 Content-Length 不准确导致超限
                var percentage = Math.Min(100.0, (double)totalBytesRead / totalBytes * 100);
                var estimatedTimeRemaining = TimeSpan.Zero;
                if (speedBytesPerSecond > 0 && percentage < 100)
                {
                    var remainingBytes = Math.Max(0, totalBytes - totalBytesRead);
                    estimatedTimeRemaining = TimeSpan.FromSeconds(remainingBytes / speedBytesPerSecond);
                }

                progress.Report(new DownloadProgress
                {
                    BytesReceived = totalBytesRead,
                    TotalBytes = totalBytes,
                    ProgressPercentage = percentage,
                    SpeedBytesPerSecond = speedBytesPerSecond,
                    ElapsedTime = elapsedTime,
                    EstimatedTimeRemaining = estimatedTimeRemaining
                });
            }
        }

        _loggingService.LogInfo(Strings.DownloadCompleted, url);
        return memoryStream.ToArray();
    }

    private async Task<byte[]> DownloadFileMultiThreadedAsync(string url, long totalBytes, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        // IDM-style parameters with Work Stealing support
        const int idealChunkSize = 512 * 1024; // 512KB chunks for better granularity
        const int minChunkSize = 256 * 1024;   // 256KB minimum
        const int maxChunkSize = 2 * 1024 * 1024; // 2MB maximum
        const int maxConcurrentThreads = 16; // Maximum concurrent connections

        // Work Stealing parameters
        const double slowChunkThreshold = 0.3;  // Speed < 30% of average is considered slow
        const double slowChunkMinTime = 2.0;   // Minimum 2 seconds before checking for slow chunks
        const long stealMinRemaining = 256 * 1024; // Minimum 256KB remaining to steal
        const long minSplitSize = 128 * 1024;   // Minimum 128KB split unit

        // Calculate chunk size based on file size
        var chunkSize = idealChunkSize;
        if (totalBytes < 10 * 1024 * 1024) // < 10MB
        {
            chunkSize = minChunkSize;
        }
        else if (totalBytes > 100 * 1024 * 1024) // > 100MB
        {
            chunkSize = maxChunkSize;
        }

        // Calculate total number of chunks
        var totalChunks = (int)Math.Ceiling((double)totalBytes / chunkSize);

        // Calculate optimal thread count
        var optimalThreads = Math.Min(maxConcurrentThreads, Math.Max(4, totalChunks / 4));

        _loggingService.LogInfo(Strings.StartingIdmDownload, totalChunks, optimalThreads, $"{chunkSize:N0}");

        // Create initial chunk states
        var chunkStates = new Dictionary<int, ChunkState>();
        var chunkQueue = new Queue<ChunkState>();

        for (int i = 0; i < totalChunks; i++)
        {
            var startByte = (long)i * chunkSize;
            var endByte = Math.Min(startByte + chunkSize - 1, totalBytes - 1);
            var state = new ChunkState
            {
                ChunkIndex = i,
                StartByte = startByte,
                EndByte = endByte,
                Status = ChunkStatus.Pending
            };
            chunkStates[i] = state;
            chunkQueue.Enqueue(state);
        }

        // Progress tracking
        var stateLock = new object();
        var queueLock = new object();
        var completedBytes = 0L;
        var startTime = DateTime.Now;
        var lastUpdateTime = startTime;
        var lastCompletedBytes = 0L;
        // 分块快照（100ms更新一次），供IDM风格进度条渲染
        var lastSegmentSnapshot = (IReadOnlyList<ChunkSegmentInfo>?)null;

        // Helper: Calculate global average speed
        double CalculateGlobalAvgSpeed()
        {
            lock (stateLock)
            {
                var activeSpeeds = chunkStates.Values
                    .Where(cs => cs.Status == ChunkStatus.Downloading && !cs.IsCancelled && cs.CurrentSpeed > 0)
                    .Select(cs => cs.CurrentSpeed)
                    .ToList();

                return activeSpeeds.Any() ? activeSpeeds.Average() : 0.0;
            }
        }

        // Helper: Find slow chunk to steal
        ChunkState? FindSlowChunkToSteal()
        {
            lock (stateLock)
            {
                var avgSpeed = CalculateGlobalAvgSpeed();
                if (avgSpeed == 0) return null;

                var slowCandidates = chunkStates.Values
                    .Where(cs => cs.Status == ChunkStatus.Downloading &&
                                !cs.IsCancelled &&
                                cs.ElapsedTime.TotalSeconds > slowChunkMinTime &&
                                cs.CurrentSpeed > 0 &&
                                cs.CurrentSpeed < avgSpeed * slowChunkThreshold &&
                                cs.RemainingBytes > stealMinRemaining)
                    .ToList();

                if (!slowCandidates.Any()) return null;

                // Select slowest chunk
                var slowest = slowCandidates.OrderBy(cs => cs.CurrentSpeed).First();

                // Mark as cancelled
                slowest.IsCancelled = true;

                _loggingService.LogInfo(Strings.WorkStealingDetectedSlowChunk,
                    slowest.ChunkIndex,
                    slowest.CurrentSpeed / 1024 / 1024,
                    avgSpeed / 1024 / 1024,
                    slowest.ProgressPercentage,
                    slowest.ElapsedTime.TotalSeconds);

                return slowest;
            }
        }

        // Helper: Split stolen range
        void SplitStolenRange(ChunkState oldChunk, long stealStart, long stealEnd, byte[]? partialData)
        {
            lock (stateLock)
            {
                // Save partial data if exists
                if (partialData != null && partialData.Length > 0)
                {
                    var partialIndex = chunkStates.Keys.Max() + 1000;
                    var partialChunk = new ChunkState
                    {
                        ChunkIndex = partialIndex,
                        StartByte = oldChunk.StartByte,
                        EndByte = oldChunk.StartByte + partialData.Length - 1,
                        Status = ChunkStatus.Completed,
                        Data = partialData,
                        BytesDownloaded = partialData.Length
                    };
                    chunkStates[partialIndex] = partialChunk;
                    _loggingService.LogInfo(Strings.WorkStealingSavedPartialData,
                        oldChunk.ChunkIndex, partialData.Length, partialIndex);
                }
                else
                {
                    _loggingService.LogWarning(Strings.WorkStealingNoPartialData, oldChunk.ChunkIndex);
                }

                // Calculate split count
                var totalSize = stealEnd - stealStart + 1;
                int splitCount;
                if (totalSize < minSplitSize * 2)
                    splitCount = 1;
                else if (totalSize < 2 * 1024 * 1024)
                    splitCount = 2;
                else if (totalSize < 5 * 1024 * 1024)
                    splitCount = 4;
                else
                    splitCount = 8;

                var splitSize = totalSize / splitCount;

                _loggingService.LogInfo(Strings.WorkStealingSplitRange, totalSize, splitCount);

                // Create new chunks
                lock (queueLock)
                {
                    for (int i = 0; i < splitCount; i++)
                    {
                        var chunkStart = stealStart + i * splitSize;
                        var chunkEnd = i < splitCount - 1 ? chunkStart + splitSize - 1 : stealEnd;

                        var newIndex = chunkStates.Keys.Max() + 1;
                        var newState = new ChunkState
                        {
                            ChunkIndex = newIndex,
                            StartByte = chunkStart,
                            EndByte = chunkEnd,
                            Status = ChunkStatus.Pending
                        };
                        chunkStates[newIndex] = newState;
                        chunkQueue.Enqueue(newState);

                        _loggingService.LogInfo(Strings.WorkStealingSplitChunkInfo,
                            newIndex, chunkStart, chunkEnd, chunkEnd - chunkStart + 1);
                    }
                }

                // Mark old chunk as cancelled
                oldChunk.Status = ChunkStatus.Cancelled;
            }
        }

        // Worker thread function
        async Task WorkerThread(int workerId)
        {
            while (true)
            {
                ChunkState? chunkState = null;

                // Try to get task from queue
                lock (queueLock)
                {
                    if (chunkQueue.Count > 0)
                    {
                        chunkState = chunkQueue.Dequeue();
                    }
                }

                // If queue empty, try work stealing
                if (chunkState == null)
                {
                    var slowChunk = FindSlowChunkToSteal();
                    if (slowChunk != null)
                    {
                        var stealStart = slowChunk.StartByte + slowChunk.BytesDownloaded;
                        var stealEnd = slowChunk.EndByte;
                        var partialData = slowChunk.Data;

                        SplitStolenRange(slowChunk, stealStart, stealEnd, partialData);
                        continue; // Try again to get from queue
                    }
                    else
                    {
                        break; // No more work
                    }
                }

                // Download chunk with tracking
                try
                {
                    await DownloadChunkWithTrackingAsync(url, chunkState, workerId,
                        (bytesReceived) =>
                        {
                            lock (stateLock)
                            {
                                completedBytes += bytesReceived;
                                if (progress != null)
                                {
                                    var currentTime = DateTime.Now;
                                    var elapsedTime = currentTime - startTime;
                                    var speedBytesPerSecond = 0.0;

                                    if (elapsedTime.TotalSeconds > 0.1)
                                    {
                                        var timeSinceLastUpdate = currentTime - lastUpdateTime;
                                        if (timeSinceLastUpdate.TotalMilliseconds >= 100)
                                        {
                                            var bytesSinceLastUpdate = completedBytes - lastCompletedBytes;
                                            if (timeSinceLastUpdate.TotalSeconds > 0)
                                            {
                                                speedBytesPerSecond = bytesSinceLastUpdate / timeSinceLastUpdate.TotalSeconds;
                                            }
                                            lastUpdateTime = currentTime;
                                            lastCompletedBytes = completedBytes;

                                            // 快照当前分块状态，跳过已取消且无数据的分块
                                            lastSegmentSnapshot = chunkStates.Values
                                                .Where(cs => cs.Status != ChunkStatus.Cancelled)
                                                .OrderBy(cs => cs.StartByte)
                                                .Select(cs => new ChunkSegmentInfo
                                                {
                                                    StartByte = cs.StartByte,
                                                    EndByte = cs.EndByte,
                                                    BytesDownloaded = cs.BytesDownloaded,
                                                    SegmentStatus = cs.Status switch
                                                    {
                                                        ChunkStatus.Completed => ChunkSegmentStatus.Completed,
                                                        ChunkStatus.Downloading => ChunkSegmentStatus.Downloading,
                                                        _ => ChunkSegmentStatus.Pending
                                                    }
                                                })
                                                .ToList();
                                        }
                                        else
                                        {
                                            speedBytesPerSecond = completedBytes / elapsedTime.TotalSeconds;
                                        }
                                    }

                                    // 进度百分比上限为 100%，防止竞态条件或 Content-Length 不准确导致超限
                                    var percentage = Math.Min(100.0, (double)completedBytes / totalBytes * 100);
                                    var estimatedTimeRemaining = TimeSpan.Zero;
                                    if (speedBytesPerSecond > 0 && percentage < 100)
                                    {
                                        var remainingBytes = Math.Max(0, totalBytes - completedBytes);
                                        estimatedTimeRemaining = TimeSpan.FromSeconds(remainingBytes / speedBytesPerSecond);
                                    }

                                    progress.Report(new DownloadProgress
                                    {
                                        BytesReceived = completedBytes,
                                        TotalBytes = totalBytes,
                                        ProgressPercentage = percentage,
                                        SpeedBytesPerSecond = speedBytesPerSecond,
                                        ElapsedTime = elapsedTime,
                                        EstimatedTimeRemaining = estimatedTimeRemaining,
                                        ChunkSegments = lastSegmentSnapshot
                                    });
                                }
                            }
                        }, cancellationToken);
                }
                catch (Exception ex)
                {
                    _loggingService.LogError(ex, "Worker {0} chunk {1} download error", workerId, chunkState.ChunkIndex);
                    throw;
                }
            }
        }

        // Start worker threads
        var workerTasks = new List<Task>();
        for (int i = 0; i < optimalThreads; i++)
        {
            var workerId = i;
            workerTasks.Add(Task.Run(() => WorkerThread(workerId), cancellationToken));
        }

        // Wait for all workers to complete
        await Task.WhenAll(workerTasks);

        // Validate and merge results
        var totalDownloaded = 0L;
        var completedChunks = 0;
        var cancelledChunks = 0;

        foreach (var kvp in chunkStates.OrderBy(x => x.Key))
        {
            var cs = kvp.Value;
            if (cs.Status == ChunkStatus.Completed)
            {
                var isVirtual = cs.ChunkIndex >= 1000;
                var actualDataSize = cs.Data?.Length ?? 0;

                if (isVirtual)
                {
                    if (actualDataSize > 0)
                    {
                        totalDownloaded += actualDataSize;
                        _loggingService.LogInfo(Strings.ChunkVirtualValidation, cs.ChunkIndex, actualDataSize);
                    }
                    else
                    {
                        _loggingService.LogInfo(Strings.ChunkVirtualEmpty, cs.ChunkIndex);
                    }
                }
                else
                {
                    totalDownloaded += actualDataSize;
                    _loggingService.LogInfo(Strings.ChunkValidation, cs.ChunkIndex, $"{actualDataSize:N0}");
                }
                completedChunks++;
            }
            else if (cs.Status == ChunkStatus.Cancelled)
            {
                cancelledChunks++;
            }
        }

        if (totalDownloaded != totalBytes)
        {
            _loggingService.LogWarning(Strings.DownloadStatisticsDifference,
                totalBytes, totalDownloaded, Math.Abs(totalBytes - totalDownloaded));
            _loggingService.LogInfo(Strings.DownloadStatisticsNote);
        }

        // Merge chunks by byte range
        var result = new byte[totalBytes];
        foreach (var cs in chunkStates.Values.OrderBy(x => x.StartByte))
        {
            if (cs.Status == ChunkStatus.Completed && cs.Data != null && cs.Data.Length > 0)
            {
                Array.Copy(cs.Data, 0, result, cs.StartByte, cs.Data.Length);
                _loggingService.LogInfo(Strings.ChunkMergedToOffset,
                    cs.ChunkIndex, cs.StartByte, cs.EndByte, cs.Data.Length);
            }
        }

        _loggingService.LogInfo(Strings.WorkStealingDownloadComplete,
            result.Length, chunkStates.Count, completedChunks, cancelledChunks);

        return result;
    }

    private async Task DownloadChunkWithTrackingAsync(string url, ChunkState chunkState, int workerId,
        Action<long> onProgress, CancellationToken cancellationToken)
    {
        const int maxRetries = 3;
        const int baseDelayMs = 1000;

        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                // Update status
                chunkState.Status = ChunkStatus.Downloading;
                chunkState.StartTime = DateTime.Now;

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(chunkState.StartByte, chunkState.EndByte);

                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                if (response.StatusCode != System.Net.HttpStatusCode.PartialContent)
                {
                    response.EnsureSuccessStatusCode();
                }

                using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var chunkStream = new MemoryStream();

                var buffer = new byte[8192];
                var lastProgressReport = 0L;
                var lastUpdate = DateTime.Now;
                var lastBytes = 0L;

                while (true)
                {
                    // Check if cancelled
                    if (chunkState.IsCancelled)
                    {
                        chunkState.Data = chunkStream.ToArray();
                        chunkState.BytesDownloaded = chunkStream.Length;
                        _loggingService.LogInfo(Strings.ChunkCancelledWithData, chunkState.ChunkIndex, chunkStream.Length);
                        return;
                    }

                    var bytesRead = await ReadWithInactivityTimeoutAsync(contentStream, buffer, cancellationToken, $"chunk {chunkState.ChunkIndex} of {GetDownloadTargetName(url)}");
                    if (bytesRead == 0) break;

                    await chunkStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);

                    // Update state with real-time data
                    var currentTime = DateTime.Now;
                    chunkState.BytesDownloaded = chunkStream.Length;
                    chunkState.Data = chunkStream.ToArray(); // Real-time update

                    // Calculate speed every 100ms
                    if ((currentTime - lastUpdate).TotalMilliseconds >= 100)
                    {
                        var timeDiff = currentTime - lastUpdate;
                        var bytesDiff = chunkStream.Length - lastBytes;
                        chunkState.CurrentSpeed = bytesDiff / timeDiff.TotalSeconds;
                        chunkState.LastUpdateTime = currentTime;
                        lastUpdate = currentTime;
                        lastBytes = chunkStream.Length;
                    }

                    // Report progress every 64KB
                    if (chunkStream.Length - lastProgressReport >= 65536)
                    {
                        onProgress(chunkStream.Length - lastProgressReport);
                        lastProgressReport = chunkStream.Length;
                    }
                }

                // Report remaining progress
                if (chunkStream.Length > lastProgressReport)
                {
                    onProgress(chunkStream.Length - lastProgressReport);
                }

                // Mark as completed
                chunkState.Status = ChunkStatus.Completed;
                chunkState.Data = chunkStream.ToArray();
                chunkState.BytesDownloaded = chunkStream.Length;

                _loggingService.LogInfo(Strings.ChunkCompleted, chunkState.ChunkIndex, $"{chunkState.Data.Length:N0}");
                return; // Success
            }
            catch (Exception ex) when (attempt < maxRetries - 1 && !chunkState.IsCancelled)
            {
                var delay = baseDelayMs * (int)Math.Pow(2, attempt);
                _loggingService.LogWarning(Strings.ChunkFailedRetrying, chunkState.ChunkIndex, attempt + 1, maxRetries, delay, ex.Message);
                await Task.Delay(delay, cancellationToken);
            }
        }

        if (!chunkState.IsCancelled)
        {
            chunkState.Status = ChunkStatus.Pending; // Mark as failed for potential retry
            throw new Exception(string.Format(GHPC_Mod_Manager.Resources.Strings.ChunkDownloadMaxRetriesExceeded, chunkState.ChunkIndex, maxRetries));
        }
    }

    public void ClearCache()
    {
        // Clear rate limit blocks
        _rateLimitBlocks.Clear();

        // Clear session cache
        _sessionCache.Clear();

        // Clear DoH DNS cache
        DnsOverHttpsConnector.ClearCache();

        // Clear persistent cache files
        var cacheDir = Path.Combine(_settingsService.AppDataPath, "cache");
        if (Directory.Exists(cacheDir))
        {
            try
            {
                Directory.Delete(cacheDir, true);
                _loggingService.LogInfo(Strings.AllCachesClearedSuccessfully);
            }
            catch (Exception ex)
            {
                _loggingService.LogWarning(Strings.FailedToClearPersistentCache, ex.Message);
            }
        }
        else
        {
            _loggingService.LogInfo(Strings.AllCachesClearedNoFiles);
        }
    }

    public void ClearSessionCache()
    {
        _sessionCache.Clear();
        _loggingService.LogInfo(Strings.SessionCacheCleared);
    }

    public void ClearRateLimitBlocks()
    {
        _rateLimitBlocks.Clear();
        _loggingService.LogInfo(Strings.RateLimitBlocksCleared);
    }

    private string SanitizeFileName(string fileName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(fileName.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    /// <summary>
    /// Determine if URL should be cached based on domain
    /// </summary>
    private bool ShouldCacheUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            
            // Don't cache GHPC.DMR.gg URLs for quick updates
            if (uri.Host.Equals("ghpc.dmr.gg", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            
            // Cache other URLs (like GitHub raw content, external APIs, etc.)
            return true;
        }
        catch
        {
            // If URL parsing fails, default to caching
            return true;
        }
    }

    /// <summary>
    /// Check if a path is a local file system path (not a URL)
    /// </summary>
    private bool IsLocalPath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        // Check if it's a URL scheme
        if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase))
            return false;

        // Check if it looks like a Windows or Unix path
        return path.Contains(':') || path.StartsWith('/') || path.StartsWith('\\') || path.Contains('\\');
    }

    /// <summary>
    /// Load ModConfig from local file system path
    /// </summary>
    private async Task<List<ModConfig>> LoadModConfigFromLocalPath(string path)
    {
        try
        {
            string jsonPath;
            
            // If path is a directory, look for modconfig.json inside
            if (Directory.Exists(path))
            {
                jsonPath = Path.Combine(path, "modconfig.json");
                if (!File.Exists(jsonPath))
                {
                    _loggingService.LogError(Strings.ModConfigJsonNotFound, path);
                    return new List<ModConfig>();
                }
            }
            // If it's a file, use it directly
            else if (File.Exists(path))
            {
                jsonPath = path;
            }
            else
            {
                _loggingService.LogError(Strings.LocalPathNotFound, path);
                return new List<ModConfig>();
            }

            _loggingService.LogInfo(Strings.ReadingModConfigFrom, jsonPath);
            var json = await File.ReadAllTextAsync(jsonPath);
            var configs = JsonConvert.DeserializeObject<List<ModConfig>>(json);
            return configs ?? new List<ModConfig>();
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, "Failed to load ModConfig from local path: {0}", path);
            return new List<ModConfig>();
        }
    }

    /// <summary>
    /// Load ModI18nManager from local file system path
    /// </summary>
    private async Task<ModI18nManager> LoadModI18nConfigFromLocalPath(string path)
    {
        try
        {
            string jsonPath;
            
            // If path is a directory, look for mod_i18n.json inside
            if (Directory.Exists(path))
            {
                jsonPath = Path.Combine(path, "mod_i18n.json");
                if (!File.Exists(jsonPath))
                {
                    _loggingService.LogError(Strings.ModI18nJsonNotFound, path);
                    return new ModI18nManager();
                }
            }
            // If it's a file, use it directly
            else if (File.Exists(path))
            {
                jsonPath = path;
            }
            else
            {
                _loggingService.LogError(Strings.LocalPathNotFound, path);
                return new ModI18nManager();
            }

            _loggingService.LogInfo(Strings.ReadingModI18nConfigFrom, jsonPath);
            var json = await File.ReadAllTextAsync(jsonPath);
            var config = JsonConvert.DeserializeObject<ModI18nManager>(json);
            return config ?? new ModI18nManager();
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, "Failed to load ModI18nConfig from local path: {0}", path);
            return new ModI18nManager();
        }
    }

    /// <summary>
    /// Show rate limit warning popup with localized message (with debounce logic)
    /// </summary>
    /// <param name="resetAt">额度恢复时间（本地时间），未知时为null</param>
    private async Task ShowRateLimitWarningAsync(DateTime? resetAt)
    {
        // Check if we've shown a warning recently (within cooldown period)
        var timeSinceLastWarning = DateTime.Now - _lastRateLimitWarning;
        if (timeSinceLastWarning.TotalMinutes < RateLimitWarningCooldownMinutes)
        {
            // Log instead of showing popup
            _loggingService.LogWarning(Strings.GitHubRateLimitMessage);
            return;
        }

        // Update last warning timestamp
        _lastRateLimitWarning = DateTime.Now;

        // "18:42 恢复（约 23 分钟后）"，不知道恢复时间时留空
        var resetText = resetAt.HasValue
            ? string.Format(Strings.GitHubRateLimitResetsAt, resetAt.Value.ToString("HH:mm"), Math.Max(1, (int)Math.Ceiling((resetAt.Value - DateTime.Now).TotalMinutes)))
            : string.Empty;
        var hasToken = !string.IsNullOrWhiteSpace(_settingsService.Settings.GitHubApiToken);

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (hasToken)
            {
                MessageDialogHelper.ShowWarning(
                    string.Format(Strings.GitHubRateLimitTokenMessage, resetText).Trim(),
                    Strings.GitHubRateLimitTitle);
                return;
            }

            // 无token：说明原因，并提供打开设置页添加token
            if (MessageDialogHelper.Confirm(
                    string.Format(Strings.GitHubRateLimitNoTokenMessage, resetText).Trim(),
                    Strings.GitHubRateLimitTitle))
            {
                _navigationService.NavigateToSettings();
            }
        });
    }

    /// <summary>
    /// token 被GitHub拒绝（401）时提示一次
    /// </summary>
    private async Task ShowInvalidTokenWarningAsync()
    {
        _loggingService.LogWarning(Strings.GitHubTokenRejectedLog);
        if (_invalidTokenWarned)
            return;
        _invalidTokenWarned = true;

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (MessageDialogHelper.Confirm(Strings.GitHubTokenRejectedMessage, Strings.GitHubTokenTitle))
                _navigationService.NavigateToSettings();
        });
    }

    // GitHub API 请求结果：Releases 为 null 表示失败
    private sealed record GitHubApiResponse(List<GitHubRelease>? Releases, HttpStatusCode? StatusCode, bool IsRateLimited, DateTime? RateLimitReset);

    /// <summary>
    /// 请求Releases列表并检查状态码，区分限额用尽、token失效和其他错误
    /// </summary>
    private async Task<GitHubApiResponse> RequestGitHubReleasesAsync(string url, string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(token))
            request.Headers.Add("Authorization", $"token {token}");

        // using 确保响应资源释放，与同文件其他 SendAsync 调用一致
        using var response = await _httpClient.SendAsync(request);
        var resetAt = ReadRateLimitReset(response);
        var json = await response.Content.ReadAsStringAsync();

        if (response.IsSuccessStatusCode)
        {
            var releases = JsonConvert.DeserializeObject<List<GitHubRelease>>(json) ?? new List<GitHubRelease>();
            return new GitHubApiResponse(releases, response.StatusCode, false, resetAt);
        }

        // 限额用尽：403/429，且剩余额度为0或返回体提到 rate limit
        var remaining = response.Headers.TryGetValues("X-RateLimit-Remaining", out var values) ? values.FirstOrDefault() : null;
        var isRateLimited = (response.StatusCode == HttpStatusCode.Forbidden || response.StatusCode == HttpStatusCode.TooManyRequests) &&
                            (remaining == "0" || json.Contains("rate limit", StringComparison.OrdinalIgnoreCase));

        return new GitHubApiResponse(null, response.StatusCode, isRateLimited, resetAt);
    }

    // X-RateLimit-Reset 是 Unix 秒，转换为本地时间
    private static DateTime? ReadRateLimitReset(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("X-RateLimit-Reset", out var values) &&
            long.TryParse(values.FirstOrDefault(), out var unixSeconds))
        {
            return DateTimeOffset.FromUnixTimeSeconds(unixSeconds).LocalDateTime;
        }
        return null;
    }

    public async Task<GitHubRateLimitStatus> GetGitHubRateLimitAsync(string? token)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/rate_limit");
            if (!string.IsNullOrWhiteSpace(token))
                request.Headers.Add("Authorization", $"token {token.Trim()}");

            using var response = await _httpClient.SendAsync(request, cts.Token);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return new GitHubRateLimitStatus { TokenRejected = true };
            if (!response.IsSuccessStatusCode)
                return new GitHubRateLimitStatus { Error = $"HTTP {(int)response.StatusCode}" };

            // {"resources":{"core":{"limit":60,"remaining":57,"reset":1700000000}}}
            var json = Newtonsoft.Json.Linq.JObject.Parse(await response.Content.ReadAsStringAsync(cts.Token));
            var core = json["resources"]?["core"];
            return new GitHubRateLimitStatus
            {
                Success = true,
                Limit = core?.Value<int>("limit") ?? 0,
                Remaining = core?.Value<int>("remaining") ?? 0,
                ResetAt = core?["reset"] != null ? DateTimeOffset.FromUnixTimeSeconds(core.Value<long>("reset")).LocalDateTime : null
            };
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning("GitHub rate limit check failed: {0}", ex.Message);
            return new GitHubRateLimitStatus { Error = ex.Message };
        }
    }

    public async Task<GitHubRelease?> GetLatestGitHubReleaseAsync(string repoOwner, string repoName, bool forceRefresh = false)
    {
        if (!forceRefresh)
        {
            var cached = await ReadLatestReleaseCacheAsync(repoOwner, repoName);
            if (cached.Found)
                return cached.Release;
        }

        var routes = GetWebSourceRoutes();

        // 1. Releases 订阅源（releases.atom）：不占API额度，带发布时间
        //    按线路依次尝试（所选代理 → 其他代理 → 直连，或 直连 → 代理），某条线路被墙/被代理拦截就换下一条
        if (IsWebSourceEnabled("feed"))
        {
            foreach (var route in routes.Where(r => IsWebSourceRouteUsable("feed", r)))
            {
                (bool Success, GitHubRelease? Release) fromFeed;
                await _webSourceGate.WaitAsync();
                try { fromFeed = await TryGetLatestReleaseFromFeedAsync(repoOwner, repoName, route); }
                finally { _webSourceGate.Release(); }
                RecordWebSourceResult("feed", route, fromFeed.Success);
                if (fromFeed.Success)
                {
                    await SaveLatestReleaseCacheAsync(repoOwner, repoName, fromFeed.Release);
                    return fromFeed.Release;
                }
            }
        }

        // 2. releases/latest 跳转：同样不占额度，但只有Tag（不含预发布版和日期）
        if (IsWebSourceEnabled("latest"))
        {
            foreach (var route in routes.Where(r => IsWebSourceRouteUsable("latest", r)))
            {
                GitHubRelease? fromRedirect;
                await _webSourceGate.WaitAsync();
                try { fromRedirect = await TryGetLatestReleaseFromRedirectAsync(repoOwner, repoName, route); }
                finally { _webSourceGate.Release(); }
                RecordWebSourceResult("latest", route, fromRedirect != null);
                if (fromRedirect != null)
                {
                    await SaveLatestReleaseCacheAsync(repoOwner, repoName, fromRedirect);
                    return fromRedirect;
                }
            }
        }

        // 3. 最后才用API（非强制刷新时会先读7天的完整缓存）
        var releases = await GetGitHubReleasesAsync(repoOwner, repoName, forceRefresh);
        return releases.FirstOrDefault();
    }

    /// <summary>
    /// 访问 github.com 公开页面（订阅源/跳转）的线路顺序，"direct" = 直连，其余为代理域名
    /// 只读取公开的版本信息，所以可以借用列表里的其他代理
    /// </summary>
    private List<string> GetWebSourceRoutes()
    {
        var settings = _settingsService.Settings;

        // 有token时其他请求都直连，这里保持一致
        if (!string.IsNullOrWhiteSpace(settings.GitHubApiToken))
            return new List<string> { "direct" };

        // 只用内置的几个代理（域名可被下发配置按同名Id覆盖），不轮询下发列表里的其他第三方代理
        // 只算一次：GetProxyDomain 每次都会写日志，100多个Mod会刷屏
        var proxies = _builtinProxyDomains ??= Enum.GetValues<GitHubProxyServer>().Select(GetProxyDomain).ToList();

        var routes = new List<string>();
        if (settings.UseGitHubProxy)
        {
            // 所选代理 → 其他代理 → 直连（被墙时直连最慢，放最后）
            routes.Add(GetProxyDomain(settings.GitHubProxyServer));
            routes.AddRange(proxies);
            routes.Add("direct");
        }
        else
        {
            // 没开代理：直连 → 代理（直连在国内可能不通）
            routes.Add("direct");
            routes.AddRange(proxies);
        }

        return routes.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    // 主配置可远程关闭某个来源（LatestVersionSources）
    private bool IsWebSourceEnabled(string source)
    {
        var allowed = _mainConfigService.Config?.LatestVersionSources;
        return allowed == null || allowed.Contains(source, StringComparer.OrdinalIgnoreCase);
    }

    // 某来源在某线路上连续失败太多次，本次启动就不再走这条线路
    private bool IsWebSourceRouteUsable(string source, string route)
    {
        return _webSourceFailures.GetValueOrDefault($"{source}|{route}") < WebSourceMaxFailures;
    }

    private void RecordWebSourceResult(string source, string route, bool success)
    {
        var key = $"{source}|{route}";
        if (success)
        {
            // 每条线路只记一次，避免100多个Mod刷屏
            if (_webSourceLogged.TryAdd($"ok|{key}", true))
                _loggingService.LogInfo("Latest version source '{0}' works via {1}", source, route);
            // 成功只抵消一次失败：时好时坏的线路（如 gh1.dmr.gg）不会被反复重试
            _webSourceFailures.AddOrUpdate(key, 0, (_, n) => Math.Max(0, n - 1));
            return;
        }

        var failures = _webSourceFailures.AddOrUpdate(key, 1, (_, n) => n + 1);
        if (failures >= WebSourceMaxFailures && _webSourceLogged.TryAdd($"skip|{key}", true))
            _loggingService.LogWarning("Latest version source '{0}' failed {1} times via {2}, skipping that route for the rest of this session", source, failures, route);
    }

    // github.com 公开页面地址，route 为代理域名时走 https://代理/https://github.com/...
    private static string BuildGitHubWebUrl(string path, string route)
    {
        var url = $"https://github.com/{path}";
        return route == "direct" ? url : $"https://{route}/{url}";
    }

    /// <summary>
    /// 从 releases.atom 读最新一条Release，Success=false 表示这条线路不可用
    /// </summary>
    private async Task<(bool Success, GitHubRelease? Release)> TryGetLatestReleaseFromFeedAsync(string repoOwner, string repoName, string route)
    {
        try
        {
            using var cts = new CancellationTokenSource(WebSourceTimeout);
            var url = BuildGitHubWebUrl($"{repoOwner}/{repoName}/releases.atom", route);
            using var response = await _httpClient.GetAsync(url, cts.Token);
            if (!response.IsSuccessStatusCode)
                return (false, null);

            var xml = await response.Content.ReadAsStringAsync(cts.Token);
            XNamespace atom = "http://www.w3.org/2005/Atom";
            var feed = XDocument.Parse(xml).Root;
            // 代理出错时可能返回HTML页面，不是Atom就当失败
            if (feed == null || feed.Name != atom + "feed")
                return (false, null);

            // 订阅源按发布顺序倒序，第一条就是最新的；没有entry说明仓库还没有Release
            var entry = feed.Elements(atom + "entry").FirstOrDefault();
            if (entry == null)
                return (true, null);

            // href = https://github.com/owner/repo/releases/tag/v1.2.3
            // 必须是这个仓库自己的Tag链接，防止代理返回别的内容
            var tagMarker = $"/{repoOwner}/{repoName}/releases/tag/";
            var href = entry.Elements(atom + "link")
                .Select(l => (string?)l.Attribute("href"))
                .FirstOrDefault(h => h != null && h.Contains(tagMarker, StringComparison.OrdinalIgnoreCase));
            if (href == null)
                return (false, null);

            var tag = Uri.UnescapeDataString(href.Substring(href.IndexOf(tagMarker, StringComparison.OrdinalIgnoreCase) + tagMarker.Length));
            DateTime.TryParse((string?)entry.Element(atom + "updated"), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var updated);

            return (true, new GitHubRelease
            {
                TagName = tag,
                Name = (string?)entry.Element(atom + "title") ?? tag,
                PublishedAt = DateTime.SpecifyKind(updated, DateTimeKind.Utc)
            });
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning("Releases feed failed for {0}/{1} via {2}: {3}", repoOwner, repoName, route, ex.Message);
            return (false, null);
        }
    }

    /// <summary>
    /// 读 releases/latest 跳转后的地址拿Tag（订阅源失败时的备用）
    /// </summary>
    private async Task<GitHubRelease?> TryGetLatestReleaseFromRedirectAsync(string repoOwner, string repoName, string route)
    {
        try
        {
            using var cts = new CancellationTokenSource(WebSourceTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Head, BuildGitHubWebUrl($"{repoOwner}/{repoName}/releases/latest", route));
            using var response = await _httpClient.SendAsync(request, cts.Token);

            // 自动跟随跳转后，最终地址形如 .../owner/repo/releases/tag/v1.2.3
            // 代理若在服务端跟随跳转，最终地址里没有Tag，按失败处理
            var tagMarker = $"/{repoOwner}/{repoName}/releases/tag/";
            var finalUrl = response.RequestMessage?.RequestUri?.AbsoluteUri ?? string.Empty;
            var index = finalUrl.IndexOf(tagMarker, StringComparison.OrdinalIgnoreCase);
            if (!response.IsSuccessStatusCode || index < 0)
                return null;

            var tag = Uri.UnescapeDataString(finalUrl.Substring(index + tagMarker.Length).TrimEnd('/'));
            return new GitHubRelease { TagName = tag, Name = tag };
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning("Releases latest redirect failed for {0}/{1} via {2}: {3}", repoOwner, repoName, route, ex.Message);
            return null;
        }
    }

    public async Task<CreatorModFile?> GetCreatorModFileAsync(string repoOwner, string repoName)
    {
        var cachePath = GetCreatorFileCachePath(repoOwner, repoName);

        // 24小时缓存，包括"仓库里没有这个文件"的结果，避免每次打开详情页都请求
        try
        {
            if (File.Exists(cachePath))
            {
                var cached = JsonConvert.DeserializeObject<CreatorFileCacheEntry>(await File.ReadAllTextAsync(cachePath));
                if (cached != null && DateTime.Now - cached.CachedAt <= LatestReleaseCacheExpiry)
                    return ParseCreatorModFile(cached.Content, repoOwner, repoName);
            }
        }
        catch
        {
            // 缓存损坏时重新获取
        }

        // 和订阅源一样按线路尝试：raw.githubusercontent.com 在国内也可能被墙
        var rawUrl = $"https://raw.githubusercontent.com/{repoOwner}/{repoName}/HEAD/{CreatorFileName}";
        foreach (var route in GetWebSourceRoutes().Where(r => IsWebSourceRouteUsable("creator", r)))
        {
            var url = route == "direct" ? rawUrl : $"https://{route}/{rawUrl}";
            string? content;
            bool reached;

            await _webSourceGate.WaitAsync();
            try
            {
                using var cts = new CancellationTokenSource(WebSourceTimeout);
                using var response = await _httpClient.GetAsync(url, cts.Token);
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    // 仓库没有这个文件：确定结果，同样缓存
                    reached = true;
                    content = null;
                }
                else if (response.IsSuccessStatusCode)
                {
                    reached = true;
                    content = await response.Content.ReadAsStringAsync(cts.Token);
                    // 太大的文件不处理（正常只有几百字节）
                    if (content.Length > 64 * 1024)
                        content = null;
                }
                else
                {
                    reached = false;
                    content = null;
                }
            }
            catch (Exception ex)
            {
                _loggingService.LogWarning("Creator file request failed for {0}/{1} via {2}: {3}", repoOwner, repoName, route, ex.Message);
                reached = false;
                content = null;
            }
            finally
            {
                _webSourceGate.Release();
            }

            RecordWebSourceResult("creator", route, reached);
            if (!reached)
                continue;

            try
            {
                await File.WriteAllTextAsync(cachePath, JsonConvert.SerializeObject(new CreatorFileCacheEntry { CachedAt = DateTime.Now, Content = content }));
            }
            catch (Exception ex)
            {
                _loggingService.LogWarning("Failed to cache creator file for {0}/{1}: {2}", repoOwner, repoName, ex.Message);
            }

            return ParseCreatorModFile(content, repoOwner, repoName);
        }

        return null;
    }

    private CreatorModFile? ParseCreatorModFile(string? content, string repoOwner, string repoName)
    {
        if (string.IsNullOrWhiteSpace(content))
            return null;

        try
        {
            return JsonConvert.DeserializeObject<CreatorModFile>(content);
        }
        catch (Exception ex)
        {
            // 作者写错格式时忽略整个文件，不影响详情页
            _loggingService.LogWarning("Invalid {0} in {1}/{2}: {3}", CreatorFileName, repoOwner, repoName, ex.Message);
            return null;
        }
    }

    private string GetCreatorFileCachePath(string repoOwner, string repoName)
    {
        var cacheDir = Path.Combine(_settingsService.AppDataPath, "cache", "creator_files");
        Directory.CreateDirectory(cacheDir);
        return Path.Combine(cacheDir, $"{SanitizeFileName(repoOwner)}_{SanitizeFileName(repoName)}.json");
    }

    // 缓存格式: {"CachedAt": "...", "Content": 原始文件内容 或 null(仓库没有该文件)}
    private sealed class CreatorFileCacheEntry
    {
        public DateTime CachedAt { get; set; }
        public string? Content { get; set; }
    }

    private string GetLatestReleaseCachePath(string repoOwner, string repoName)
    {
        var cacheDir = Path.Combine(_settingsService.AppDataPath, "cache", "github_latest");
        Directory.CreateDirectory(cacheDir);
        return Path.Combine(cacheDir, $"{SanitizeFileName(repoOwner)}_{SanitizeFileName(repoName)}.json");
    }

    // 缓存格式: {"CachedAt": "...", "Release": {...} 或 null}
    private sealed class LatestReleaseCacheEntry
    {
        public DateTime CachedAt { get; set; }
        public GitHubRelease? Release { get; set; }
    }

    private async Task<(bool Found, GitHubRelease? Release)> ReadLatestReleaseCacheAsync(string repoOwner, string repoName)
    {
        try
        {
            var path = GetLatestReleaseCachePath(repoOwner, repoName);
            if (!File.Exists(path))
                return (false, null);

            var entry = JsonConvert.DeserializeObject<LatestReleaseCacheEntry>(await File.ReadAllTextAsync(path));
            if (entry == null || DateTime.Now - entry.CachedAt > LatestReleaseCacheExpiry)
                return (false, null);

            return (true, entry.Release);
        }
        catch
        {
            return (false, null);
        }
    }

    private async Task SaveLatestReleaseCacheAsync(string repoOwner, string repoName, GitHubRelease? release)
    {
        try
        {
            var entry = new LatestReleaseCacheEntry { CachedAt = DateTime.Now, Release = release };
            await File.WriteAllTextAsync(GetLatestReleaseCachePath(repoOwner, repoName), JsonConvert.SerializeObject(entry, Formatting.Indented));
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning("Failed to save latest release cache for {0}/{1}: {2}", repoOwner, repoName, ex.Message);
        }
    }

    /// <summary>
    /// Get GitHub releases from persistent cache with date-based file naming
    /// </summary>
    private async Task<List<GitHubRelease>?> GetGitHubReleasesFromPersistentCacheAsync(string repoOwner, string repoName, bool ignoreExpiry = false)
    {
        try
        {
            var cacheDir = Path.Combine(_settingsService.AppDataPath, "cache", "github_releases");
            Directory.CreateDirectory(cacheDir);
            
            var sanitizedRepoName = $"{SanitizeFileName(repoOwner)}_{SanitizeFileName(repoName)}";
            
            // Look for cache files for this repository (could be from different dates)
            var pattern = $"{sanitizedRepoName}_*.json";
            var cacheFiles = Directory.GetFiles(cacheDir, pattern);
            
            // Find the most recent valid cache file
            foreach (var cacheFile in cacheFiles.OrderByDescending(f => f))
            {
                try
                {
                    var fileName = Path.GetFileNameWithoutExtension(cacheFile);
                    var parts = fileName.Split('_');
                    
                    // Expected format: {owner}_{repo}_{YYYYMMDD}
                    if (parts.Length >= 3)
                    {
                        var datePart = parts[^1]; // Last part should be date
                        if (DateTime.TryParseExact(datePart, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var cacheDate))
                        {
                            var age = DateTime.Now.Date - cacheDate.Date;
                            
                            // Check if cache is still valid (7 days) or if we're ignoring expiry
                            if (ignoreExpiry || age.TotalDays <= 7)
                            {
                                var cacheContent = await File.ReadAllTextAsync(cacheFile);
                                var releases = JsonConvert.DeserializeObject<List<GitHubRelease>>(cacheContent);
                                
                                if (releases != null)
                                {
                                    _loggingService.LogInfo(Strings.GitHubReleasesLoadedFromSessionCache, repoOwner, repoName);
                                    return releases;
                                }
                            }
                            else
                            {
                                // Cache is expired, delete it
                                _loggingService.LogInfo(Strings.DeletingExpiredGitHubCache, repoOwner, repoName, age.TotalDays);
                                File.Delete(cacheFile);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _loggingService.LogWarning(Strings.FailedToProcessCacheFile, cacheFile, ex.Message);
                    // Try to delete corrupted cache file
                    try { File.Delete(cacheFile); } catch { }
                }
            }
            
            return null;
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning(Strings.FailedToReadGitHubReleasesFromCache, repoOwner, repoName, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Save GitHub releases to persistent cache with date-based file naming
    /// </summary>
    private async Task SaveGitHubReleasesToPersistentCacheAsync(string repoOwner, string repoName, List<GitHubRelease> releases)
    {
        try
        {
            var cacheDir = Path.Combine(_settingsService.AppDataPath, "cache", "github_releases");
            Directory.CreateDirectory(cacheDir);
            
            var sanitizedRepoName = $"{SanitizeFileName(repoOwner)}_{SanitizeFileName(repoName)}";
            var today = DateTime.Now.ToString("yyyyMMdd");
            var cacheFile = Path.Combine(cacheDir, $"{sanitizedRepoName}_{today}.json");
            
            // Clean up old cache files for this repository first
            var pattern = $"{sanitizedRepoName}_*.json";
            var oldCacheFiles = Directory.GetFiles(cacheDir, pattern);
            foreach (var oldFile in oldCacheFiles)
            {
                if (oldFile != cacheFile) // Don't delete the file we're about to create
                {
                    try
                    {
                        File.Delete(oldFile);
                    }
                    catch (Exception ex)
                    {
                        _loggingService.LogWarning(Strings.FailedToDeleteOldCacheFile, oldFile, ex.Message);
                    }
                }
            }
            
            // Save new cache
            var json = JsonConvert.SerializeObject(releases, Formatting.Indented);
            await File.WriteAllTextAsync(cacheFile, json);

            // 完整列表是最新信息，同步更新最新版本缓存
            await SaveLatestReleaseCacheAsync(repoOwner, repoName, releases.FirstOrDefault());
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning(Strings.FailedToSaveGitHubReleasesToCache, repoOwner, repoName, ex.Message);
        }
    }

    private async Task<int> ReadWithInactivityTimeoutAsync(Stream contentStream, byte[] buffer, CancellationToken cancellationToken, string context)
    {
        try
        {
            return await contentStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)
                .WaitAsync(DownloadInactivityTimeout, cancellationToken);
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException(string.Format(GetResourceString("DownloadNoProgressTimeout", "No download progress for {1} seconds while downloading {0}.") , context, DownloadInactivityTimeout.TotalSeconds.ToString("0")), ex);
        }
    }

    private void ValidateDownloadIntegrity(byte[] downloadData, long? expectedSize, string? expectedDigest, string assetName)
    {
        if (downloadData.Length == 0)
        {
            throw new InvalidDataException(string.Format(GetResourceString("DownloadIntegrityCheckFailed", "Downloaded file failed integrity validation: {0}"), assetName));
        }

        if (expectedSize.HasValue && expectedSize.Value > 0 && downloadData.LongLength != expectedSize.Value)
        {
            throw new InvalidDataException(string.Format(Strings.DownloadSizeMismatchException, expectedSize.Value, downloadData.LongLength));
        }

        if (expectedSize.HasValue && expectedSize.Value > 0)
        {
            _loggingService.LogInfo("DownloadSizeVerified", assetName, expectedSize.Value);
        }

        if (string.IsNullOrWhiteSpace(expectedDigest))
        {
            return;
        }

        var (algorithm, expectedHash) = ParseDigest(expectedDigest);
        if (!algorithm.Equals("sha256", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(expectedHash))
        {
            _loggingService.LogInfo("DownloadDigestUnsupported", assetName, expectedDigest);
            return;
        }

        var actualHash = Convert.ToHexString(SHA256.HashData(downloadData)).ToLowerInvariant();
        if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(string.Format(GetResourceString("DownloadDigestMismatch", "SHA-256 mismatch for {0}. Expected {1}, got {2}."), assetName, expectedHash, actualHash));
        }

        _loggingService.LogInfo("DownloadDigestVerified", assetName, actualHash);
    }

    private static (string Algorithm, string Hash) ParseDigest(string digest)
    {
        var trimmedDigest = digest.Trim();
        var separatorIndex = trimmedDigest.IndexOf(':');
        if (separatorIndex <= 0 || separatorIndex >= trimmedDigest.Length - 1)
        {
            return ("sha256", trimmedDigest.ToLowerInvariant());
        }

        return (
            trimmedDigest[..separatorIndex].Trim(),
            trimmedDigest[(separatorIndex + 1)..].Trim().ToLowerInvariant());
    }

    private static string GetDownloadTargetName(string url)
    {
        try
        {
            return Path.GetFileName(new Uri(url).AbsolutePath);
        }
        catch
        {
            return url;
        }
    }

    private static string GetResourceString(string key, string fallback)
    {
        return Strings.ResourceManager.GetString(key, Strings.Culture) ?? fallback;
    }
}

public class DownloadProgress
{
    public long BytesReceived { get; set; }
    public long TotalBytes { get; set; }
    public double ProgressPercentage { get; set; }
    public double SpeedBytesPerSecond { get; set; }
    public TimeSpan ElapsedTime { get; set; }
    public TimeSpan EstimatedTimeRemaining { get; set; }
    public IReadOnlyList<ChunkSegmentInfo>? ChunkSegments { get; set; }
    
    public string GetFormattedSpeed()
    {
        return FormatBytes(SpeedBytesPerSecond) + "/s";
    }

    /// <summary>
    /// 状态文字后缀：速度>0时显示百分比+速度，否则只显示百分比
    /// </summary>
    public string GetProgressSuffix()
    {
        return SpeedBytesPerSecond > 0
            ? $"{ProgressPercentage:F1}% - {GetFormattedSpeed()}"
            : $"{ProgressPercentage:F1}%";
    }
    
    public string GetFormattedProgress()
    {
        return $"{FormatBytes(BytesReceived)}/{FormatBytes(TotalBytes)}";
    }
    
    private static string FormatBytes(double bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB" };
        int order = 0;
        while (bytes >= 1024 && order < sizes.Length - 1)
        {
            order++;
            bytes /= 1024;
        }
        return $"{bytes:0.##} {sizes[order]}";
    }
}

public class CacheItem<T>
{
    public T Data { get; set; } = default!;
    public DateTime Timestamp { get; set; }
}

// Chunk status enum for Work Stealing algorithm
internal enum ChunkStatus
{
    Pending,
    Downloading,
    Completed,
    Cancelled
}

// Chunk state tracking for Work Stealing
internal class ChunkState
{
    public int ChunkIndex { get; set; }
    public long StartByte { get; set; }
    public long EndByte { get; set; }
    public ChunkStatus Status { get; set; } = ChunkStatus.Pending;
    public DateTime? StartTime { get; set; }
    public long BytesDownloaded { get; set; }
    public double CurrentSpeed { get; set; }
    public DateTime LastUpdateTime { get; set; }
    public bool IsCancelled { get; set; }
    public byte[]? Data { get; set; }

    public long TotalSize => EndByte - StartByte + 1;
    public long RemainingBytes => TotalSize - BytesDownloaded;
    public TimeSpan ElapsedTime => StartTime.HasValue ? DateTime.Now - StartTime.Value : TimeSpan.Zero;
    public double ProgressPercentage => TotalSize == 0 ? 100.0 : (BytesDownloaded / (double)TotalSize) * 100;
}

/// <summary>
/// GitHub API 额度查询结果
/// </summary>
public class GitHubRateLimitStatus
{
    public bool Success { get; set; }
    public bool TokenRejected { get; set; }
    public string? Error { get; set; }
    public int Limit { get; set; }
    public int Remaining { get; set; }
    public DateTime? ResetAt { get; set; }
}

/// <summary>
/// Mod作者仓库里的 ghpcmm.json：只允许安全的展示信息（推荐搭配等），不能包含下载地址
/// </summary>
public class CreatorModFile
{
    [JsonProperty("Recommends")]
    [JsonConverter(typeof(ModRecommendationListConverter))]
    public List<ModRecommendation> Recommends { get; set; } = new();
}
