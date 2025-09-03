using GHPC_Mod_Manager.Resources;
using System.Net.Http;

namespace GHPC_Mod_Manager.Services;

public interface IAnnouncementService
{
    Task<string?> GetAnnouncementAsync(string language);
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

    public async Task<string?> GetAnnouncementAsync(string language)
    {
        try
        {
            _loggingService.LogInfo(Strings.LoadingAnnouncement);

            // Map language codes to file names
            var languageFileMap = new Dictionary<string, string>
            {
                ["zh-CN"] = "zh-CN.md",
                ["en-US"] = "en-US.md"
            };

            var fileName = languageFileMap.GetValueOrDefault(language, "en-US.md");
            var url = $"https://GHPC.DMR.gg/announce/{fileName}";

            _loggingService.LogInfo(Strings.FetchingAnnouncementFrom, url);

            var response = await _httpClient.GetAsync(url);
            
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                
                // Return null if content is empty or just whitespace
                if (string.IsNullOrWhiteSpace(content))
                {
                    _loggingService.LogInfo(Strings.AnnouncementContentEmpty, language);
                    return null;
                }

                _loggingService.LogInfo(Strings.AnnouncementLoadedSuccessfully, language);
                return content;
            }
            else if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _loggingService.LogInfo(Strings.NoAnnouncementAvailable, language);
                return null;
            }
            else
            {
                _loggingService.LogWarning(Strings.AnnouncementFetchFailed, response.StatusCode.ToString(), response.ReasonPhrase ?? "Unknown");
                return null;
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.AnnouncementLoadFailed);
            return null;
        }
    }
}