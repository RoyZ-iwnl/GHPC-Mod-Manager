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
    private readonly ISecureStorageService _secureStorage;
    private AppSettings _settings = new();

    public AppSettings Settings => _settings;

    public string AppDataPath { get; }
    public string TempPath { get; }

    public SettingsService(ILoggingService loggingService, ISecureStorageService secureStorage)
    {
        _loggingService = loggingService;
        _secureStorage = secureStorage;

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
                    // 旧版本settings.json可能存有已删除的枚举值，回退到默认节点
                    if (!Enum.IsDefined(typeof(GitHubProxyServer), loadedSettings.GitHubProxyServer))
                        loadedSettings.GitHubProxyServer = GitHubProxyServer.GhDmrGg;

                    // 解密 Token（兼容明文旧版）
                    if (!string.IsNullOrEmpty(loadedSettings.GitHubApiToken))
                    {
                        var (decrypted, wasMigrated) = _secureStorage.UnprotectWithMigrationDetection(loadedSettings.GitHubApiToken);
                        loadedSettings.GitHubApiToken = decrypted;

                        // 如果从明文迁移，立即保存加密版本
                        if (wasMigrated)
                        {
                            _settings = loadedSettings;
                            await SaveSettingsAsync();
                            _loggingService.LogInfo(Strings.GitHubTokenMigratedToEncrypted);
                            return;
                        }
                    }

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

            // 克隆设置对象，加密 Token 后保存
            var settingsToSave = new AppSettings
            {
                Language = _settings.Language,
                GameRootPath = _settings.GameRootPath,
                IsFirstRun = _settings.IsFirstRun,
                Theme = _settings.Theme,
                UseGitHubProxy = _settings.UseGitHubProxy,
                GitHubProxyServer = _settings.GitHubProxyServer,
                UseDnsOverHttps = _settings.UseDnsOverHttps,
                UpdateChannel = _settings.UpdateChannel,
                GitHubApiToken = string.IsNullOrEmpty(_settings.GitHubApiToken)
                    ? string.Empty
                    : _secureStorage.Protect(_settings.GitHubApiToken),
                CleanupDoneForVersion = _settings.CleanupDoneForVersion,
                LastAnnouncementMd5 = _settings.LastAnnouncementMd5,
                DoNotShowAnnouncementBeforeUpdate = _settings.DoNotShowAnnouncementBeforeUpdate,
                ShowUninstalledOnly = _settings.ShowUninstalledOnly,
                SkipConflictCheck = _settings.SkipConflictCheck,
                SkipIntegrityCheck = _settings.SkipIntegrityCheck,
                IsEndfieldThemeUnlocked = _settings.IsEndfieldThemeUnlocked
            };

            var json = JsonConvert.SerializeObject(settingsToSave, Formatting.Indented);
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