using GHPC_Mod_Manager.Resources;
using Newtonsoft.Json;
using System.IO;
using System.Net.Http;

namespace GHPC_Mod_Manager.Services.Network;

public interface IFundingService
{
    /// <summary>
    /// 获取赞助者ID列表，三级回退 + 24h持久化缓存，forceRefresh跳过缓存
    /// </summary>
    Task<List<string>> GetFundingIdsAsync(bool forceRefresh = false);
}

/// <summary>
/// 赞助者ID列表服务：从 main.json 下发的 FundingIdsUrl 拉取纯文本名单（每行一个ID）
/// 支持三级回退（primary/fallback/fallback2）+ 24h持久化缓存 + 失败走过期缓存兜底
/// </summary>
public class FundingService : IFundingService
{
    private const string CacheFileName = "funding_ids.json";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan CacheExpiry = TimeSpan.FromHours(24);

    private readonly HttpClient _httpClient;
    private readonly IMainConfigService _mainConfigService;
    private readonly ISettingsService _settingsService;
    private readonly ILoggingService _loggingService;

    private string CacheDir => Path.Combine(_settingsService.AppDataPath, "cache");
    private string CachePath => Path.Combine(CacheDir, CacheFileName);

    public FundingService(
        HttpClient httpClient,
        IMainConfigService mainConfigService,
        ISettingsService settingsService,
        ILoggingService loggingService)
    {
        _httpClient = httpClient;
        _mainConfigService = mainConfigService;
        _settingsService = settingsService;
        _loggingService = loggingService;
    }

    public async Task<List<string>> GetFundingIdsAsync(bool forceRefresh = false)
    {
        // TODO: 离线测试 - 读取本地文件，测试完注释掉整块 if
        // ============================================================
        // const string localTestPath = @"D:\Project\Github\GHPC\GHPC-Mod-Manager-Web\funding\IDs.txt";
        // if (File.Exists(localTestPath))
        // {
        //     try
        //     {
        //         var text = await File.ReadAllTextAsync(localTestPath);
        //         var ids = ParseIds(text);
        //         _loggingService.LogInfo(Strings.FundingFetchSuccess, ids.Count);
        //         return ids;
        //     }
        //     catch (Exception ex)
        //     {
        //         _loggingService.LogInfo(Strings.FundingFetchUrlFailed, localTestPath, ex.Message);
        //     }
        // }
        // ============================================================

        // 非强制时优先读未过期缓存
        if (!forceRefresh)
        {
            var cached = await TryLoadCacheAsync();
            if (cached != null)
            {
                _loggingService.LogInfo(Strings.FundingUsingCache, cached.Count);
                return cached;
            }
        }

        // 远程拉取（三级回退，首个成功即返回）
        var fresh = await FetchRemoteAsync();
        if (fresh != null)
        {
            await SaveCacheAsync(fresh);
            _loggingService.LogInfo(Strings.FundingFetchSuccess, fresh.Count);
            return fresh;
        }

        // 全部失败：返回过期缓存兜底，无缓存则空列表
        var stale = await TryLoadCacheAsync(ignoreExpiry: true);
        if (stale != null)
        {
            _loggingService.LogInfo(Strings.FundingUsingStale, stale.Count);
            return stale;
        }

        _loggingService.LogInfo(Strings.FundingFetchFailedEmpty);
        return new List<string>();
    }

    /// <summary>
    /// 遍历候选URL逐个尝试，首个成功即返回
    /// </summary>
    private async Task<List<string>?> FetchRemoteAsync()
    {
        var urls = _mainConfigService.GetFundingIdsUrlCandidates();
        for (var i = 0; i < urls.Count; i++)
        {
            var url = urls[i];
            try
            {
                _loggingService.LogInfo(Strings.FundingFetchingFrom, url);
                using var cts = new CancellationTokenSource(RequestTimeout);
                var text = await _httpClient.GetStringAsync(url, cts.Token);
                var ids = ParseIds(text);
                _loggingService.LogInfo(Strings.FundingFetchSuccessUrl, url, ids.Count);
                return ids;
            }
            catch (Exception ex)
            {
                var hasNext = i < urls.Count - 1;
                _loggingService.LogInfo(Strings.FundingFetchUrlFailed, url, ex.Message);
                if (hasNext)
                    _loggingService.LogInfo(Strings.FundingTriggerFallback);
            }
        }
        return null;
    }

    /// <summary>
    /// 按行解析纯文本名单：去空白、去空行、去重
    /// </summary>
    private static List<string> ParseIds(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new List<string>();

        return text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                   .Select(line => line.Trim())
                   .Where(line => line.Length > 0)
                   .Distinct()
                   .ToList();
    }

    private async Task<List<string>?> TryLoadCacheAsync(bool ignoreExpiry = false)
    {
        try
        {
            if (!File.Exists(CachePath)) return null;
            var info = new FileInfo(CachePath);
            if (!ignoreExpiry && DateTime.UtcNow - info.LastWriteTimeUtc > CacheExpiry) return null;
            var json = await File.ReadAllTextAsync(CachePath);
            var cache = JsonConvert.DeserializeObject<FundingCache>(json);
            return cache?.Ids;
        }
        catch { return null; }
    }

    private async Task SaveCacheAsync(List<string> ids)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            var cache = new FundingCache { Timestamp = DateTime.UtcNow, Ids = ids };
            var json = JsonConvert.SerializeObject(cache, Formatting.Indented);
            await File.WriteAllTextAsync(CachePath, json);
        }
        catch { }
    }

    private class FundingCache
    {
        public DateTime Timestamp { get; set; }
        public List<string> Ids { get; set; } = new();
    }
}
