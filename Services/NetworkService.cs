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
}

public class NetworkService : INetworkService
{
    private readonly HttpClient _httpClient;
    private readonly ILoggingService _loggingService;
    private readonly ISettingsService _settingsService;
    
    
    // Rate limit tracking
    private static readonly Dictionary<string, DateTime> _rateLimitBlocks = new();
    
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
            var request = new HttpRequestMessage(HttpMethod.Head, "https://api.github.com");
            request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/139.0.0.0 Safari/537.36 Edg/139.0.0.0");
            
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
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
                _loggingService.LogInfo("Skipping GitHub API request due to previous rate limit for {0}/{1}", repoOwner, repoName);
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
            
            // Save to persistent cache
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
        // GitHub-optimized parameters
        const int maxThreads = 4; // Conservative for GitHub's rate limiting
        const int minChunkSize = 1024 * 1024; // 1MB minimum chunk size
        
        // Calculate optimal chunk size and thread count
        var chunkSize = Math.Max(minChunkSize, totalBytes / maxThreads);
        var actualThreads = (int)Math.Min(maxThreads, (totalBytes + chunkSize - 1) / chunkSize);
        
        _loggingService.LogInfo(Strings.StartingMultiThreadedDownload, actualThreads, $"{chunkSize:N0}");

        var chunks = new byte[actualThreads][];
        var chunkSizes = new long[actualThreads]; // Track actual chunk sizes for validation
        var downloadTasks = new List<Task>();
        var progressLock = new object();
        var completedBytes = 0L;
        var startTime = DateTime.Now;
        var lastUpdateTime = startTime;
        var lastCompletedBytes = 0L;

        // Create download tasks for each chunk with exact byte ranges
        for (int i = 0; i < actualThreads; i++)
        {
            var chunkIndex = i;
            var startByte = (long)chunkIndex * chunkSize;
            var endByte = Math.Min(startByte + chunkSize - 1, totalBytes - 1);
            var expectedChunkSize = endByte - startByte + 1;
            
            // Special handling for the last chunk to ensure we get all bytes
            if (chunkIndex == actualThreads - 1)
            {
                endByte = totalBytes - 1; // Ensure last chunk goes to the very end
                expectedChunkSize = endByte - startByte + 1;
            }
            
            _loggingService.LogInfo(Strings.ChunkDownloadRange, chunkIndex, startByte, endByte, $"{expectedChunkSize:N0}");
            
            var task = DownloadChunkAsync(url, startByte, endByte, chunkIndex, chunks, 
                (bytesReceived) => 
                {
                    lock (progressLock)
                    {
                        completedBytes += bytesReceived;
                        if (progress != null)
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
            
            downloadTasks.Add(task);
        }

        // Wait for all downloads to complete
        await Task.WhenAll(downloadTasks);

        // Validate all chunks were downloaded correctly
        var totalDownloaded = 0L;
        for (int i = 0; i < actualThreads; i++)
        {
            if (chunks[i] == null)
            {
                throw new Exception(string.Format(GHPC_Mod_Manager.Resources.Strings.ChunkDownloadFailed, i));
            }
            totalDownloaded += chunks[i].Length;
            _loggingService.LogInfo(Strings.ChunkValidation, i, $"{chunks[i].Length:N0}");
        }

        if (totalDownloaded != totalBytes)
        {
            _loggingService.LogError(Strings.DownloadSizeMismatch, $"{totalBytes:N0}", $"{totalDownloaded:N0}");
            var errorMessage = string.Format(GHPC_Mod_Manager.Resources.Strings.DownloadSizeMismatchException, totalBytes, totalDownloaded);
            throw new Exception(errorMessage);
        }

        // Combine all chunks in correct order
        var result = new byte[totalBytes];
        var offset = 0L;
        
        for (int i = 0; i < actualThreads; i++)
        {
            var chunk = chunks[i];
            Array.Copy(chunk, 0, result, offset, chunk.Length);
            offset += chunk.Length;
            _loggingService.LogInfo(Strings.ChunkCopiedToOffset, i, $"{offset - chunk.Length:N0}");
        }

        _loggingService.LogInfo(Strings.MultiThreadedDownloadCompleted, $"{result.Length:N0}");
        return result;
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
            _loggingService.LogInfo("All caches cleared (session cache cleared, no persistent cache files found)");
        }
    }

    public void ClearRateLimitBlocks()
    {
        _rateLimitBlocks.Clear();
        _loggingService.LogInfo("Rate limit blocks cleared - GitHub API requests can now be retried");
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
    /// Show rate limit warning popup with localized message
    /// </summary>
    private async Task ShowRateLimitWarningAsync()
    {
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
                                _loggingService.LogInfo("Deleting expired GitHub releases cache for {0}/{1} (age: {2} days)", repoOwner, repoName, age.TotalDays);
                                File.Delete(cacheFile);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _loggingService.LogWarning("Failed to process cache file {0}: {1}", cacheFile, ex.Message);
                    // Try to delete corrupted cache file
                    try { File.Delete(cacheFile); } catch { }
                }
            }
            
            return null;
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning("Failed to read GitHub releases from persistent cache for {0}/{1}: {2}", repoOwner, repoName, ex.Message);
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
                        _loggingService.LogInfo("Deleted old GitHub releases cache file: {0}", Path.GetFileName(oldFile));
                    }
                    catch (Exception ex)
                    {
                        _loggingService.LogWarning("Failed to delete old cache file {0}: {1}", oldFile, ex.Message);
                    }
                }
            }
            
            // Save new cache
            var json = JsonConvert.SerializeObject(releases, Formatting.Indented);
            await File.WriteAllTextAsync(cacheFile, json);
            
            _loggingService.LogInfo("Saved GitHub releases to persistent cache: {0}", Path.GetFileName(cacheFile));
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning("Failed to save GitHub releases to persistent cache for {0}/{1}: {2}", repoOwner, repoName, ex.Message);
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