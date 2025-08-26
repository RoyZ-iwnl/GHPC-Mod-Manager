using GHPC_Mod_Manager.Models;
using Newtonsoft.Json;
using System.IO;
using System.Net.Http;
using System.Text;
using GHPC_Mod_Manager.Resources;

namespace GHPC_Mod_Manager.Services;

public interface INetworkService
{
    Task<bool> CheckNetworkConnectionAsync();
    Task<List<ModConfig>> GetModConfigAsync(string url);
    Task<TranslationConfig> GetTranslationConfigAsync(string url);
    Task<ModI18nManager> GetModI18nConfigAsync(string url);
    Task<List<GitHubRelease>> GetGitHubReleasesAsync(string repoOwner, string repoName);
    Task<byte[]> DownloadFileAsync(string url, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default);
    void ClearCache(); // Clear all cached data
}

public class NetworkService : INetworkService
{
    private readonly HttpClient _httpClient;
    private readonly ILoggingService _loggingService;
    private readonly ISettingsService _settingsService;
    
    // In-memory cache for API responses during this session
    private static readonly Dictionary<string, object> _sessionCache = new();
    private static readonly Dictionary<string, DateTime> _cacheTimestamps = new();
    
    // Cache expiry time (24 hours for persistent cache, session-based for GitHub API)
    private readonly TimeSpan _persistentCacheExpiry = TimeSpan.FromHours(24);
    
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

            // Apply proxy prefix for supported URL patterns
            var path = uri.PathAndQuery;
            var supportedPatterns = new[]
            {
                "/archive/", // Branch/tag source code archives
                "/releases/download/", // Release files
                "/blob/", // File content
                "/raw/" // Raw file content (gist support)
            };

            bool isSupported = supportedPatterns.Any(pattern => path.Contains(pattern, StringComparison.OrdinalIgnoreCase));
            
            if (isSupported)
            {
                var proxyUrl = $"https://gh.dmr.gg/{originalUrl}";
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
            // Try to get from persistent cache first
            var cacheKey = $"modconfig_{url}";
            var cached = await GetFromPersistentCacheAsync<List<ModConfig>>(cacheKey);
            if (cached != null)
            {
                _loggingService.LogInfo(Strings.ModConfigLoadedFromCache);
                return cached;
            }
            
            _loggingService.LogInfo(Strings.FetchingModConfig, url);
            var json = await _httpClient.GetStringAsync(url);
            var configs = JsonConvert.DeserializeObject<List<ModConfig>>(json);
            var result = configs ?? new List<ModConfig>();
            
            // Save to persistent cache
            await SaveToPersistentCacheAsync(cacheKey, result);
            
            return result;
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.ModConfigFetchError, url);
            
            // Try to return stale cached data as fallback
            var cacheKey = $"modconfig_{url}";
            var staleCache = await GetFromPersistentCacheAsync<List<ModConfig>>(cacheKey, ignoreExpiry: true);
            return staleCache ?? new List<ModConfig>();
        }
    }

    public async Task<TranslationConfig> GetTranslationConfigAsync(string url)
    {
        try
        {
            // Try to get from persistent cache first
            var cacheKey = $"translationconfig_{url}";
            var cached = await GetFromPersistentCacheAsync<TranslationConfig>(cacheKey);
            if (cached != null)
            {
                _loggingService.LogInfo(Strings.TranslationConfigLoadedFromCache);
                return cached;
            }
            
            _loggingService.LogInfo(Strings.FetchingTranslationConfig, url);
            var json = await _httpClient.GetStringAsync(url);
            var config = JsonConvert.DeserializeObject<TranslationConfig>(json);
            var result = config ?? new TranslationConfig();
            
            // Save to persistent cache
            await SaveToPersistentCacheAsync(cacheKey, result);
            
            return result;
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.TranslationConfigFetchError, url);
            
            // Try to return stale cached data as fallback
            var cacheKey = $"translationconfig_{url}";
            var staleCache = await GetFromPersistentCacheAsync<TranslationConfig>(cacheKey, ignoreExpiry: true);
            return staleCache ?? new TranslationConfig();
        }
    }

    public async Task<ModI18nManager> GetModI18nConfigAsync(string url)
    {
        try
        {
            // Try to get from persistent cache first
            var cacheKey = $"modi18nconfig_{url}";
            var cached = await GetFromPersistentCacheAsync<ModI18nManager>(cacheKey);
            if (cached != null)
            {
                _loggingService.LogInfo(Strings.ModI18nConfigLoadedFromCache);
                return cached;
            }
            
            _loggingService.LogInfo(Strings.FetchingModI18nConfig, url);
            var json = await _httpClient.GetStringAsync(url);
            var config = JsonConvert.DeserializeObject<ModI18nManager>(json);
            var result = config ?? new ModI18nManager();
            
            // Save to persistent cache
            await SaveToPersistentCacheAsync(cacheKey, result);
            _loggingService.LogInfo(Strings.ModI18nConfigRefreshed);
            
            return result;
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.ModI18nConfigFetchError, url);
            
            // Try to return stale cached data as fallback
            var cacheKey = $"modi18nconfig_{url}";
            var staleCache = await GetFromPersistentCacheAsync<ModI18nManager>(cacheKey, ignoreExpiry: true);
            return staleCache ?? new ModI18nManager();
        }
    }

    public async Task<List<GitHubRelease>> GetGitHubReleasesAsync(string repoOwner, string repoName)
    {
        try
        {
            // For GitHub API, use session cache (only fetch once per program startup)
            var cacheKey = $"github_releases_{repoOwner}_{repoName}";
            
            if (_sessionCache.ContainsKey(cacheKey))
            {
                _loggingService.LogInfo(Strings.GitHubReleasesLoadedFromSessionCache, repoOwner, repoName);
                return (List<GitHubRelease>)_sessionCache[cacheKey];
            }
            
            var url = $"https://api.github.com/repos/{repoOwner}/{repoName}/releases";
            _loggingService.LogInfo(Strings.FetchingGitHubReleases, url);
            
            var json = await _httpClient.GetStringAsync(url);
            var releases = JsonConvert.DeserializeObject<List<GitHubRelease>>(json);
            var result = releases ?? new List<GitHubRelease>();
            
            // Cache in session cache (memory only, cleared on restart)
            _sessionCache[cacheKey] = result;
            _cacheTimestamps[cacheKey] = DateTime.Now;
            
            return result;
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.GitHubReleasesFetchError, repoOwner, repoName);
            
            // Try to return from session cache if available
            var cacheKey = $"github_releases_{repoOwner}_{repoName}";
            if (_sessionCache.ContainsKey(cacheKey))
            {
                return (List<GitHubRelease>)_sessionCache[cacheKey];
            }
            
            return new List<GitHubRelease>();
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
                    await TestProxyConnectivityAsync("gh.dmr.gg", cancellationToken);
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
        _sessionCache.Clear();
        _cacheTimestamps.Clear();
        
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
    }

    private async Task<T?> GetFromPersistentCacheAsync<T>(string cacheKey, bool ignoreExpiry = false) where T : class
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
            if (!ignoreExpiry && DateTime.Now - cacheItem.Timestamp > _persistentCacheExpiry)
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