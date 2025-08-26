using Newtonsoft.Json;

namespace GHPC_Mod_Manager.Models;

// 主题枚举
public enum AppTheme
{
    Light,  // 浅色主题
    Dark    // 深色主题
}

public class AppSettings
{
    public string Language { get; set; } = "zh-CN";
    public string GameRootPath { get; set; } = string.Empty;
    public string ModConfigUrl { get; set; } = "https://GHPC.DMR.gg/config/modconfig.json";
    public string TranslationConfigUrl { get; set; } = "https://GHPC.DMR.gg/config/translationconfig.json";
    public string ModI18nUrl { get; set; } = "https://GHPC.DMR.gg/config/mod_i18n.json";
    public bool IsFirstRun { get; set; } = true;
    public AppTheme Theme { get; set; } = AppTheme.Light; // 主题设置
    public bool UseGitHubProxy { get; set; } = false; // GitHub代理加速设置
}

public class ModConfig
{
    public string Id { get; set; } = string.Empty;
    public Dictionary<string, string> Name { get; set; } = new();
    public string ReleaseUrl { get; set; } = string.Empty;
    public string TargetFileNameKeyword { get; set; } = ".zip";
    public string MainBinaryFileName { get; set; } = string.Empty;
    public string ConfigSectionName { get; set; } = string.Empty;
    public InstallMethod InstallMethod { get; set; } = InstallMethod.DirectRelease;
    public string? InstallScript_Base64 { get; set; }
}

public enum InstallMethod
{
    DirectRelease,
    Scripted
}

public class TranslationConfig
{
    public string RepoUrl { get; set; } = string.Empty;
    public DateTime LastUpdated { get; set; } = DateTime.MinValue;
}

public class ModInstallManifest
{
    public Dictionary<string, ModInstallInfo> InstalledMods { get; set; } = new();
}

public class ModInstallInfo
{
    public string ModId { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public List<string> InstalledFiles { get; set; } = new();
    public DateTime InstallDate { get; set; } = DateTime.Now;
}

public class TranslationInstallManifest
{
    public List<string> XUnityAutoTranslatorFiles { get; set; } = new();
    public List<string> TranslationRepoFiles { get; set; } = new();
    public DateTime InstallDate { get; set; } = DateTime.Now;
}

public class GitHubRelease
{
    [JsonProperty("tag_name")]
    public string TagName { get; set; } = string.Empty;
    
    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;
    
    [JsonProperty("assets")]
    public List<GitHubAsset> Assets { get; set; } = new();
    
    [JsonProperty("prerelease")]
    public bool PreRelease { get; set; }

    public override string ToString()
    {
        return TagName;
    }
}

public class GitHubAsset
{
    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;
    
    [JsonProperty("browser_download_url")]
    public string DownloadUrl { get; set; } = string.Empty;
    
    [JsonProperty("size")]
    public long Size { get; set; }
}

public class ModI18nConfig
{
    public string ModId { get; set; } = string.Empty;
    public Dictionary<string, Dictionary<string, string>> ConfigLabels { get; set; } = new();
    public Dictionary<string, Dictionary<string, string>> ConfigComments { get; set; } = new();
}

public class ModI18nManager
{
    public Dictionary<string, ModI18nConfig> ModConfigs { get; set; } = new();
}

public class ModViewModel
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? InstalledVersion { get; set; }
    public string? LatestVersion { get; set; }
    public bool IsInstalled { get; set; }
    public bool IsEnabled { get; set; }
    public bool IsManuallyInstalled { get; set; }
    public bool IsTranslationPlugin { get; set; }  // Special flag for translation plugin
    public ModConfig Config { get; set; } = new();
    
    // Property to determine if this mod has configuration options
    public bool HasConfiguration => IsInstalled && 
                                    !IsTranslationPlugin && 
                                    !string.IsNullOrEmpty(Config.ConfigSectionName);
}