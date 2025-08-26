using GHPC_Mod_Manager.Models;
using Newtonsoft.Json;
using System.Globalization;
using System.IO;
using GHPC_Mod_Manager.Resources;

namespace GHPC_Mod_Manager.Services;

public interface ISettingsService
{
    AppSettings Settings { get; }
    Task LoadSettingsAsync();
    Task SaveSettingsAsync();
    void ApplyLanguageSetting();
    string AppDataPath { get; }
    string TempPath { get; }
}

public class SettingsService : ISettingsService
{
    private readonly ILoggingService _loggingService;
    private AppSettings _settings = new();

    public AppSettings Settings => _settings;

    public string AppDataPath { get; }
    public string TempPath { get; }

    public SettingsService(ILoggingService loggingService)
    {
        _loggingService = loggingService;
        
        var appPath = AppDomain.CurrentDomain.BaseDirectory;
        AppDataPath = Path.Combine(appPath, "app_data");
        TempPath = Path.Combine(appPath, "temp");
        
        Directory.CreateDirectory(AppDataPath);
        Directory.CreateDirectory(TempPath);
    }

    public async Task LoadSettingsAsync()
    {
        try
        {
            var settingsPath = Path.Combine(AppDataPath, "settings.json");
            
            if (File.Exists(settingsPath))
            {
                var json = await File.ReadAllTextAsync(settingsPath);
                var loadedSettings = JsonConvert.DeserializeObject<AppSettings>(json);
                if (loadedSettings != null)
                {
                    _settings = loadedSettings;
                    ApplyLanguageSetting();
                    _loggingService.LogInfo(Strings.SettingsLoaded);
                }
            }
            else
            {
                _loggingService.LogInfo(Strings.SettingsNotFound);
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.SettingsLoadError);
        }
    }

    public async Task SaveSettingsAsync()
    {
        try
        {
            var settingsPath = Path.Combine(AppDataPath, "settings.json");
            var json = JsonConvert.SerializeObject(_settings, Formatting.Indented);
            await File.WriteAllTextAsync(settingsPath, json);
            ApplyLanguageSetting();
            _loggingService.LogInfo(Strings.SettingsSaved);
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.SettingsSaveError);
        }
    }

    public void ApplyLanguageSetting()
    {
        try
        {
            var culture = new CultureInfo(_settings.Language);
            Thread.CurrentThread.CurrentUICulture = culture;
            Thread.CurrentThread.CurrentCulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            CultureInfo.DefaultThreadCurrentCulture = culture;
            
            // Also set the resource culture
            GHPC_Mod_Manager.Resources.Strings.Culture = culture;
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.LanguageSettingError);
            // Fallback to English culture if there's an error
            var fallbackCulture = new CultureInfo("en-US");
            Thread.CurrentThread.CurrentUICulture = fallbackCulture;
            Thread.CurrentThread.CurrentCulture = fallbackCulture;
            CultureInfo.DefaultThreadCurrentUICulture = fallbackCulture;
            CultureInfo.DefaultThreadCurrentCulture = fallbackCulture;
            GHPC_Mod_Manager.Resources.Strings.Culture = fallbackCulture;
        }
    }
}