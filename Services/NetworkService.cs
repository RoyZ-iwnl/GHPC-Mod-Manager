using GHPC_Mod_Manager.Models;
using Newtonsoft.Json;
using System.IO;
using System.Net.Http;
using System.Text;
using GHPC_Mod_Manager.Resources;
using System.Windows;

namespace GHPC_Mod_Manager.Services;

public interface INetworkService
{
    Task<bool> CheckNetworkConnectionAsync();
    Task<List<ModConfig>> GetModConfigAsync(string url);
    Task<TranslationConfig> GetTranslationConfigAsync(string url);
    Task<ModI18nManager> GetModI18nConfigAsync(string url);
    Task<List<GitHubRelease>> GetGitHubReleasesAsync(string repoOwner, string repoName, bool forceRefresh = false);
    Task<byte[]> DownloadFileAsync(string url, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default);
    void ClearCache(); // Clear all cached data
    void ClearRateLimitBlocks(); // Clear rate limit blocks to allow manual refresh
    HttpClient HttpClient { get; } // 暴露HttpClient用于代理请求
}

public class NetworkService : INetworkService
{
    private readonly HttpClient _httpClient;

    public HttpClient HttpClient => _httpClient;
    private readonly ILoggingService _loggingService;
    private readonly ISettingsService _settingsService;
    
    
    // Rate limit tracking
    private static readonly Dictionary<string, DateTime> _rateLimitBlocks = new();
    private static DateTime _lastRateLimitWarning = DateTime.MinValue;
    private const int RateLimitWarningCooldownMinutes = 5; // Show warning at most once every 5 minutes
    
    // Cache expiry times
    private readonly TimeSpan _githubApiCacheExpiry = TimeSpan.FromHours(24); // GitHub API with rate limits
    private readonly TimeSpan _dmrCacheExpiry = TimeSpan.FromMinutes(10); // DMR.gg data, short cache for quick updates
    
    public NetworkService(HttpClient httpClient, ILoggingService loggingService, ISettingsService settingsService)
    {
        _httpClient = httpClient;
        _loggingService = loggingService;
        _settingsService = settingsService;
        
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

            bool isSupported = supportedPatterns.Any(pattern => path.Contains(pattern, StringComparison.OrdinalIgnoreCase)) ||
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
    /// Get proxy domain based on selected proxy server
    /// </summary>
    private string GetProxyDomain(GitHubProxyServer proxyServer)
    {
        return proxyServer switch
        {
            GitHubProxyServer.GhDmrGg => "gh.dmr.gg",
            GitHubProxyServer.GhProxyCom => "gh-proxy.com",
            GitHubProxyServer.HkGhProxyCom => "hk.gh-proxy.com",
            GitHubProxyServer.CdnGhProxyCom => "cdn.gh-proxy.com",
            GitHubProxyServer.EdgeOneGhProxyCom => "edgeone.gh-proxy.com",
            _ => "gh.dmr.gg"
        };
    }

    public async Task<bool> CheckNetworkConnectionAsync()
    {
        try
        {
            var testUrl = "https://api.github.com/repos/RoyZ-iwnl/GHPC-Mod-Manager/releases/latest";
            
            // Apply GitHub proxy if enabled
            if (_settingsService.Settings.UseGitHubProxy)
            {
                var proxyDomain = GetProxyDomain(_settingsService.Settings.GitHubProxyServer);
                testUrl = $"https://{proxyDomain}/https://api.github.com/repos/RoyZ-iwnl/GHPC-Mod-Manager/releases/latest";
                _loggingService.LogInfo(Strings.TestingNetworkConnectionThroughProxy, testUrl);
            }
            else
            {
                _loggingService.LogInfo(Strings.TestingDirectNetworkConnection, testUrl);
            }
            
            var request = new HttpRequestMessage(HttpMethod.Head, testUrl);
            request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/139.0.0.0 Safari/537.36 Edg/139.0.0.0");
            
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            
            if (response.IsSuccessStatusCode)
            {
                _loggingService.LogInfo(_settingsService.Settings.UseGitHubProxy ? Strings.NetworkTestSuccessfulViaProxy : Strings.NetworkTestSuccessfulViaDirect);
            }
            else
            {
                _loggingService.LogWarning(Strings.NetworkTestStatusCode, response.StatusCode);
            }
            
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.NetworkTestFailed, ex.Message);
            _loggingService.LogError(ex, Strings.NetworkCheckFailed);
            return false;
        }
    }

    public async Task<List<ModConfig>> GetModConfigAsync(string url)
    {
        try
        {
            // Check if it's a local file path
            if (IsLocalPath(url))
            {
                _loggingService.LogInfo(Strings.LoadingModConfigFromLocalPath, url);
                return await LoadModConfigFromLocalPath(url);
            }
            
            // For DMR.gg URLs, use short cache or no cache
            var cacheKey = $"modconfig_{url}";
            var shouldCache = ShouldCacheUrl(url);
            List<ModConfig>? cached = null;
            
            if (shouldCache)
            {
                cached = await GetFromPersistentCacheAsync<List<ModConfig>>(cacheKey, GetCacheExpiryForUrl(url));
                if (cached != null)
                {
                    _loggingService.LogInfo(Strings.ModConfigLoadedFromCache);
                    return cached;
                }
            }
            
            _loggingService.LogInfo(Strings.FetchingModConfig, url);
            var json = await _httpClient.GetStringAsync(url);
            var configs = JsonConvert.DeserializeObject<List<ModConfig>>(json);
            var result = configs ?? new List<ModConfig>();
            
            // Save to persistent cache only if caching is enabled for this URL
            if (shouldCache)
            {
                await SaveToPersistentCacheAsync(cacheKey, result);
            }
            
            return result;
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.ModConfigFetchError, url);
            
            // For local paths, don't try cache fallback
            if (IsLocalPath(url))
                return new List<ModConfig>();
            
            // Try to return stale cached data as fallback for URLs (only if caching is enabled)
            var cacheKey = $"modconfig_{url}";
            if (ShouldCacheUrl(url))
            {
                var staleCache = await GetFromPersistentCacheAsync<List<ModConfig>>(cacheKey, GetCacheExpiryForUrl(url), ignoreExpiry: true);
                return staleCache ?? new List<ModConfig>();
            }
            return new List<ModConfig>();
        }
    }

    public async Task<TranslationConfig> GetTranslationConfigAsync(string url)
    {
        try
        {
            // Check if it's a local file path
            if (IsLocalPath(url))
            {
                _loggingService.LogInfo(Strings.LoadingTranslationConfigFromLocalPath, url);
                return await LoadTranslationConfigFromLocalPath(url);
            }
            
            // For DMR.gg URLs, use short cache or no cache
            var cacheKey = $"translationconfig_{url}";
            var shouldCache = ShouldCacheUrl(url);
            TranslationConfig? cached = null;
            
            if (shouldCache)
            {
                cached = await GetFromPersistentCacheAsync<TranslationConfig>(cacheKey, GetCacheExpiryForUrl(url));
                if (cached != null)
                {
                    _loggingService.LogInfo(Strings.TranslationConfigLoadedFromCache);
                    return cached;
                }
            }
            
            _loggingService.LogInfo(Strings.FetchingTranslationConfig, url);
            var json = await _httpClient.GetStringAsync(url);
            var config = JsonConvert.DeserializeObject<TranslationConfig>(json);
            var result = config ?? new TranslationConfig();
            
            // Save to persistent cache only if caching is enabled for this URL
            if (shouldCache)
            {
                await SaveToPersistentCacheAsync(cacheKey, result);
            }
            
            return result;
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.TranslationConfigFetchError, url);
            
            // For local paths, don't try cache fallback
            if (IsLocalPath(url))
                return new TranslationConfig();
            
            // Try to return stale cached data as fallback for URLs (only if caching is enabled)
            var cacheKey = $"translationconfig_{url}";
            if (ShouldCacheUrl(url))
            {
                var staleCache = await GetFromPersistentCacheAsync<TranslationConfig>(cacheKey, GetCacheExpiryForUrl(url), ignoreExpiry: true);
                return staleCache ?? new TranslationConfig();
            }
            return new TranslationConfig();
        }
    }

    public async Task<ModI18nManager> GetModI18nConfigAsync(string url)
    {
        try
        {
            // Check if it's a local file path
            if (IsLocalPath(url))
            {
                _loggingService.LogInfo(Strings.LoadingModI18nConfigFromLocalPath, url);
                return await LoadModI18nConfigFromLocalPath(url);
            }
            
            // For DMR.gg URLs, use short cache or no cache
            var cacheKey = $"modi18nconfig_{url}";
            var shouldCache = ShouldCacheUrl(url);
            ModI18nManager? cached = null;
            
            if (shouldCache)
            {
                cached = await GetFromPersistentCacheAsync<ModI18nManager>(cacheKey, GetCacheExpiryForUrl(url));
                if (cached != null)
                {
                    _loggingService.LogInfo(Strings.ModI18nConfigLoadedFromCache);
                    return cached;
                }
            }
            
            _loggingService.LogInfo(Strings.FetchingModI18nConfig, url);
            var json = await _httpClient.GetStringAsync(url);
            var config = JsonConvert.DeserializeObject<ModI18nManager>(json);
            var result = config ?? new ModI18nManager();
            
            // Save to persistent cache only if caching is enabled for this URL
            if (shouldCache)
            {
                await SaveToPersistentCacheAsync(cacheKey, result);
            }
            _loggingService.LogInfo(Strings.ModI18nConfigRefreshed);
            
            return result;
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.ModI18nConfigFetchError, url);
            
            // For local paths, don't try cache fallback
            if (IsLocalPath(url))
                return new ModI18nManager();
            
            // Try to return stale cached data as fallback for URLs (only if caching is enabled)
            var cacheKey = $"modi18nconfig_{url}";
            if (ShouldCacheUrl(url))
            {
                var staleCache = await GetFromPersistentCacheAsync<ModI18nManager>(cacheKey, GetCacheExpiryForUrl(url), ignoreExpiry: true);
                return staleCache ?? new ModI18nManager();
            }
            return new ModI18nManager();
        }
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
            var rateLimitKey = $"github_api_{repoOwner}_{repoName}";
            if (_rateLimitBlocks.ContainsKey(rateLimitKey))
            {
                _loggingService.LogInfo(Strings.SkippingGitHubApiDueToRateLimit, repoOwner, repoName);
                // Try to return stale cache if available
                var staleCache = await GetGitHubReleasesFromPersistentCacheAsync(repoOwner, repoName, ignoreExpiry: true);
                return staleCache ?? new List<GitHubRelease>();
            }
            
            // Make API request
            List<GitHubRelease> result;
            
            // When GitHub proxy is disabled, access directly
            if (!_settingsService.Settings.UseGitHubProxy)
            {
                var url = $"https://api.github.com/repos/{repoOwner}/{repoName}/releases";
                _loggingService.LogInfo(Strings.FetchingGitHubReleases, url);
                
                var json = await _httpClient.GetStringAsync(url);
                var releases = JsonConvert.DeserializeObject<List<GitHubRelease>>(json);
                result = releases ?? new List<GitHubRelease>();
            }
            else
            {
                // Use proxy for GitHub API access
                var proxyDomain = GetProxyDomain(_settingsService.Settings.GitHubProxyServer);
                var url = $"https://{proxyDomain}/https://api.github.com/repos/{repoOwner}/{repoName}/releases";
                _loggingService.LogInfo(Strings.FetchingGitHubReleases, url);
                
                var json = await _httpClient.GetStringAsync(url);
                var releases = JsonConvert.DeserializeObject<List<GitHubRelease>>(json);
                result = releases ?? new List<GitHubRelease>();
            }

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
            await ShowRateLimitWarningAsync();
            
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

    public async Task<byte[]> DownloadFileAsync(string url, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        try
        {
            // Apply GitHub proxy if enabled
            var finalUrl = ApplyGitHubProxy(url);
            
            _loggingService.LogInfo(Strings.DownloadStarted, finalUrl);
            
            // If we're using GitHub proxy, try proxy first, then fallback to original if it fails
            if (finalUrl != url)
            {
                try
                {
                    // Test connectivity to proxy first
                    var proxyDomain = GetProxyDomain(_settingsService.Settings.GitHubProxyServer);
                    await TestProxyConnectivityAsync(proxyDomain, cancellationToken);
                    return await DownloadFromUrlAsync(finalUrl, progress, cancellationToken);
                }
                catch (Exception proxyEx)
                {
                    _loggingService.LogWarning(Strings.GitHubProxyFailed, proxyEx.Message, url);
                    return await DownloadFromUrlAsync(url, progress, cancellationToken);
                }
            }
            else
            {
                return await DownloadFromUrlAsync(finalUrl, progress, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.DownloadError, url);
            throw;
        }
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
        // 2. File size is known and >= 5MB
        if (supportsRangeRequests && totalBytes >= 5 * 1024 * 1024)
        {
            _loggingService.LogInfo(Strings.UsingMultiThreadedDownload, $"{totalBytes:N0}");
            return await DownloadFileMultiThreadedAsync(url, totalBytes, progress, cancellationToken);
        }
        else
        {
            var reason = !supportsRangeRequests ? "server doesn't support range requests" : 
                       totalBytes < 5 * 1024 * 1024 ? "file is small" : "unknown file size";
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
            var bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
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

                var percentage = (double)totalBytesRead / totalBytes * 100;
                var estimatedTimeRemaining = TimeSpan.Zero;
                if (speedBytesPerSecond > 0)
                {
                    var remainingBytes = totalBytes - totalBytesRead;
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
                                        }
                                        else
                                        {
                                            speedBytesPerSecond = completedBytes / elapsedTime.TotalSeconds;
                                        }
                                    }

                                    var percentage = (double)completedBytes / totalBytes * 100;
                                    var estimatedTimeRemaining = TimeSpan.Zero;
                                    if (speedBytesPerSecond > 0)
                                    {
                                        var remainingBytes = totalBytes - completedBytes;
                                        estimatedTimeRemaining = TimeSpan.FromSeconds(remainingBytes / speedBytesPerSecond);
                                    }

                                    progress.Report(new DownloadProgress
                                    {
                                        BytesReceived = completedBytes,
                                        TotalBytes = totalBytes,
                                        ProgressPercentage = percentage,
                                        SpeedBytesPerSecond = speedBytesPerSecond,
                                        ElapsedTime = elapsedTime,
                                        EstimatedTimeRemaining = estimatedTimeRemaining
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

                    var bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
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

    private async Task DownloadChunkAsync(string url, long startByte, long endByte, int chunkIndex, 
        byte[][] chunks, Action<long> onProgress, CancellationToken cancellationToken)
    {
        const int maxRetries = 3;
        const int baseDelayMs = 1000;

        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(startByte, endByte);
                
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                
                // GitHub should return 206 (Partial Content) for range requests
                if (response.StatusCode != System.Net.HttpStatusCode.PartialContent)
                {
                    response.EnsureSuccessStatusCode();
                }

                using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var chunkStream = new MemoryStream();
                
                var buffer = new byte[8192];
                var lastProgressReport = 0L;
                
                while (true)
                {
                    var bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
                    if (bytesRead == 0) break;

                    await chunkStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                    
                    // Report progress every 64KB to avoid too frequent updates
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

                chunks[chunkIndex] = chunkStream.ToArray();
                _loggingService.LogInfo(Strings.ChunkCompleted, chunkIndex, $"{chunks[chunkIndex].Length:N0}");
                return; // Success
            }
            catch (Exception ex) when (attempt < maxRetries - 1)
            {
                var delay = baseDelayMs * (int)Math.Pow(2, attempt); // Exponential backoff
                _loggingService.LogWarning(Strings.ChunkFailedRetrying, chunkIndex, attempt + 1, maxRetries, delay, ex.Message);
                await Task.Delay(delay, cancellationToken);
            }
        }
        
        throw new Exception(string.Format(GHPC_Mod_Manager.Resources.Strings.ChunkDownloadMaxRetriesExceeded, chunkIndex, maxRetries));
    }

    public void ClearCache()
    {
        // Clear rate limit blocks
        _rateLimitBlocks.Clear();
        
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

    public void ClearRateLimitBlocks()
    {
        _rateLimitBlocks.Clear();
        _loggingService.LogInfo(Strings.RateLimitBlocksCleared);
    }

    private async Task<T?> GetFromPersistentCacheAsync<T>(string cacheKey, TimeSpan cacheExpiry, bool ignoreExpiry = false) where T : class
    {
        try
        {
            var cacheDir = Path.Combine(_settingsService.AppDataPath, "cache");
            var cacheFile = Path.Combine(cacheDir, $"{SanitizeFileName(cacheKey)}.json");
            
            if (!File.Exists(cacheFile))
                return null;
                
            var cacheData = await File.ReadAllTextAsync(cacheFile);
            var cacheItem = JsonConvert.DeserializeObject<CacheItem<T>>(cacheData);
            
            if (cacheItem == null)
                return null;
            
            // Check expiry unless explicitly ignoring it
            if (!ignoreExpiry && DateTime.Now - cacheItem.Timestamp > cacheExpiry)
            {
                // Cache expired, delete file
                File.Delete(cacheFile);
                return null;
            }
            
            return cacheItem.Data;
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning(Strings.FailedToReadCache, cacheKey, ex.Message);
            return null;
        }
    }

    private async Task SaveToPersistentCacheAsync<T>(string cacheKey, T data)
    {
        try
        {
            var cacheDir = Path.Combine(_settingsService.AppDataPath, "cache");
            Directory.CreateDirectory(cacheDir);
            
            var cacheFile = Path.Combine(cacheDir, $"{SanitizeFileName(cacheKey)}.json");
            var cacheItem = new CacheItem<T>
            {
                Data = data,
                Timestamp = DateTime.Now
            };
            
            var json = JsonConvert.SerializeObject(cacheItem, Formatting.Indented);
            await File.WriteAllTextAsync(cacheFile, json);
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning(Strings.FailedToSaveCache, cacheKey, ex.Message);
        }
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
    /// Get cache expiry time based on URL
    /// </summary>
    private TimeSpan GetCacheExpiryForUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            
            // Short cache for DMR.gg URLs
            if (uri.Host.Equals("ghpc.dmr.gg", StringComparison.OrdinalIgnoreCase))
            {
                return _dmrCacheExpiry;
            }
            
            // Longer cache for GitHub API and other external sources
            return _githubApiCacheExpiry;
        }
        catch
        {
            // Default to GitHub API cache time
            return _githubApiCacheExpiry;
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
    /// Load TranslationConfig from local file system path
    /// </summary>
    private async Task<TranslationConfig> LoadTranslationConfigFromLocalPath(string path)
    {
        try
        {
            string jsonPath;
            
            // If path is a directory, look for translationconfig.json inside
            if (Directory.Exists(path))
            {
                jsonPath = Path.Combine(path, "translationconfig.json");
                if (!File.Exists(jsonPath))
                {
                    _loggingService.LogError(Strings.TranslationConfigJsonNotFound, path);
                    return new TranslationConfig();
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
                return new TranslationConfig();
            }

            _loggingService.LogInfo(Strings.ReadingTranslationConfigFrom, jsonPath);
            var json = await File.ReadAllTextAsync(jsonPath);
            var config = JsonConvert.DeserializeObject<TranslationConfig>(json);
            return config ?? new TranslationConfig();
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, "Failed to load TranslationConfig from local path: {0}", path);
            return new TranslationConfig();
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
    private async Task ShowRateLimitWarningAsync()
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

        // Show the warning popup
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            MessageBox.Show(
                Strings.GitHubRateLimitMessage,
                Strings.GitHubRateLimitTitle,
                MessageBoxButton.OK,
                MessageBoxImage.Warning
            );
        });
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
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning(Strings.FailedToSaveGitHubReleasesToCache, repoOwner, repoName, ex.Message);
        }
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
    
    public string GetFormattedSpeed()
    {
        return FormatBytes(SpeedBytesPerSecond) + "/s";
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

// Helper class for chunk download work queue
internal class ChunkDownloadTask
{
    public int ChunkIndex { get; set; }
    public long StartByte { get; set; }
    public long EndByte { get; set; }
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