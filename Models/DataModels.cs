using Newtonsoft.Json;

namespace GHPC_Mod_Manager.Models;

// 主题枚举
public enum AppTheme
{
    Light,  // 浅色主题
    Dark    // 深色主题
}

// GitHub代理服务器枚举
public enum GitHubProxyServer
{
    EdgeOneGhProxyCom, // edgeone.gh-proxy.com (Tencent Cloud)
    GhDmrGg,          // gh.dmr.gg (Cloudflare 1)
    GhProxyCom,       // gh-proxy.com (Cloudflare 2)
    HkGhProxyCom,     // hk.gh-proxy.com (Hong Kong)
    CdnGhProxyCom     // cdn.gh-proxy.com (Fastly)
}

// 更新通道枚举
public enum UpdateChannel
{
    Stable,  // 稳定版：只包含正式版本（不含任何字母）
    Beta     // 测试版：包含所有版本（正式版+预发布版）
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
    public GitHubProxyServer GitHubProxyServer { get; set; } = GitHubProxyServer.EdgeOneGhProxyCom; // 代理服务器选择
    public UpdateChannel UpdateChannel { get; set; } = UpdateChannel.Stable; // 更新通道选择
    public string GitHubApiToken { get; set; } = string.Empty; // GitHub API Token
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
    public List<string> Conflicts { get; set; } = new();
    public List<string> Requirements { get; set; } = new();
}

public enum InstallMethod
{
    DirectRelease,
    Scripted
}

public class TranslationConfig
{
    public string RepoUrl { get; set; } = string.Empty;
    public string Owner { get; set; } = string.Empty;
    public string RepoName { get; set; } = string.Empty;
    public string TargetAssetName { get; set; } = ".zip";
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
    public string XUnityVersion { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
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

    [JsonProperty("published_at")]
    public DateTime PublishedAt { get; set; }

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

    private string? _latestVersion;
    public string? LatestVersion
    {
        get => _latestVersion;
        set => _latestVersion = value;
    }

    public bool IsInstalled { get; set; }
    public bool IsEnabled { get; set; }
    public bool IsManuallyInstalled { get; set; }
    public bool IsTranslationPlugin { get; set; }  // Special flag for translation plugin
    public bool IsSupportedManualMod { get; set; }  // Manual mod that matches a supported mod
    public bool IsUnsupportedManualMod { get; set; }  // Manual mod with no matching config
    public ModConfig Config { get; set; } = new();
    public DateTime? UpdateDate { get; set; }  // MOD更新日期

    // 格式化更新日期为"XX天前"格式
    private string GetUpdateDateDisplay()
    {
        if (!UpdateDate.HasValue)
            return "";

        var days = (DateTime.Now - UpdateDate.Value).Days;
        if (days == 0)
            return GHPC_Mod_Manager.Resources.Strings.Today;
        if (days == 1)
            return GHPC_Mod_Manager.Resources.Strings.Yesterday;
        return string.Format(GHPC_Mod_Manager.Resources.Strings.DaysAgo, days);
    }

    // 显示版本号和更新日期: "1.2.3 (XX天前)"
    public string LatestVersionWithDate
    {
        get
        {
            if (string.IsNullOrEmpty(LatestVersion))
                return GHPC_Mod_Manager.Resources.Strings.Unknown;

            var dateDisplay = GetUpdateDateDisplay();
            if (string.IsNullOrEmpty(dateDisplay))
                return LatestVersion;

            return $"{LatestVersion} ({dateDisplay})";
        }
    }
    
    // Property to determine if this mod has configuration options
    public bool HasConfiguration => IsInstalled && 
                                    !IsTranslationPlugin && 
                                    (IsSupportedManualMod || !IsManuallyInstalled) &&
                                    !string.IsNullOrEmpty(Config.ConfigSectionName);

    // Property to determine if this mod can be updated
    public bool CanUpdate => IsInstalled && 
                            !IsTranslationPlugin && 
                            !IsManuallyInstalled && 
                            !string.IsNullOrEmpty(LatestVersion) && 
                            LatestVersion != GHPC_Mod_Manager.Resources.Strings.Unknown &&
                            InstalledVersion != LatestVersion &&
                            InstalledVersion != GHPC_Mod_Manager.Resources.Strings.Manual;
    
    // Property to determine if this mod can be reinstalled (for supported manual mods)
    public bool CanReinstall => IsInstalled &&
                                IsManuallyInstalled &&
                                IsSupportedManualMod &&
                                !IsTranslationPlugin;

    // Property to determine if this mod can be uninstalled
    // Only allow uninstall when mod is installed AND enabled (since disabled mods are moved out of directory)
    public bool CanUninstall => IsInstalled && IsEnabled && !IsTranslationPlugin;

    // Property to extract GitHub repository URL from ReleaseUrl
    public string? GitHubRepositoryUrl => ExtractGitHubRepositoryUrl();
    
    private string? ExtractGitHubRepositoryUrl()
    {
        if (string.IsNullOrEmpty(Config.ReleaseUrl))
            return null;

        try
        {
            var uri = new Uri(Config.ReleaseUrl);
            
            // Only handle GitHub URLs
            if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
                !uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase))
                return null;

            // Parse different GitHub URL formats:
            // - https://github.com/owner/repo/releases/tag/v1.0.0
            // - https://api.github.com/repos/owner/repo/releases/latest
            // - https://api.github.com/repos/owner/repo/releases
            var pathParts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            
            if (uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase))
            {
                // API format: /repos/owner/repo/releases[/latest]
                if (pathParts.Length >= 3 && pathParts[0] == "repos")
                {
                    var owner = pathParts[1];
                    var repo = pathParts[2];
                    return $"https://github.com/{owner}/{repo}";
                }
            }
            else
            {
                // Regular GitHub format: /owner/repo/releases/...
                if (pathParts.Length >= 2)
                {
                    var owner = pathParts[0];
                    var repo = pathParts[1];
                    return $"https://github.com/{owner}/{repo}";
                }
            }
        }
        catch
        {
            // Ignore URL parsing errors
        }
        
        return null;
    }
}