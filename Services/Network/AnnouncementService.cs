using GHPC_Mod_Manager.Resources;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace GHPC_Mod_Manager.Services.Network;

public interface IAnnouncementService
{
    Task<(string? content, string md5)> GetAnnouncementAsync(string language, bool forceRefresh = false);
}

public class AnnouncementService : IAnnouncementService
{
    private readonly HttpClient _httpClient;
    private readonly ILoggingService _loggingService;
    private readonly ISettingsService _settingsService;

    public AnnouncementService(HttpClient httpClient, ILoggingService loggingService, ISettingsService settingsService)
    {
        _httpClient = httpClient;
        _loggingService = loggingService;
        _settingsService = settingsService;
    }

    public async Task<(string? content, string md5)> GetAnnouncementAsync(string language, bool forceRefresh = false)
    {
        try
        {
            // === 临时硬编码测试：读取本地公告文件（测试完删除此块） ===
            //var localPath = @"D:\Project\Github\GHPC\GHPC-Mod-Manager-Web\announce\zh-CN.md";
            //if (File.Exists(localPath))
            //{
            //    var content = await File.ReadAllTextAsync(localPath);
            //    if (!string.IsNullOrWhiteSpace(content))
            //        return (content, ComputeMd5(content));
            //}
            // === 临时测试结束 ===

            var languageFileMap = new Dictionary<string, string>
            {
                ["zh-CN"] = "zh-CN.md",
                ["en-US"] = "en-US.md"
            };

            var fileName = languageFileMap.GetValueOrDefault(language, "en-US.md");
            var url = $"https://GHPC.DMR.gg/announce/{fileName}";

            var response = await _httpClient.GetAsync(url);

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();

                if (string.IsNullOrWhiteSpace(content))
                    return (null, string.Empty);

                var md5 = ComputeMd5(content);
                return (content, md5);
            }

            return (null, string.Empty);
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.AnnouncementLoadFailed);
            return (null, string.Empty);
        }
    }

    private string ComputeMd5(string content)
    {
        using var md5 = MD5.Create();
        var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(content));
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }
}