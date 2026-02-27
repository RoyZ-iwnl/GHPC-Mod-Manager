using System.ComponentModel;
using System.Runtime.CompilerServices;
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
    GhDmrGg,          // gh.dmr.gg (Cloudflare 1)
    Gh1DmrGg,         // gh1.dmr.gg (腾讯云 1)
    Gh2DmrGg,         // gh2.dmr.gg (腾讯云 2)
    EdgeOneGhProxyCom, // edgeone.gh-proxy.com (腾讯云 3)
    GhProxyCom,       // gh-proxy.com (Cloudflare 2)
    HkGhProxyCom,     // hk.gh-proxy.com (Hong Kong)
    CdnGhProxyCom     // cdn.gh-proxy.com (Fastly)
}

/// <summary>
/// 代理服务器显示项，用于UI下拉框绑定
/// </summary>
public class ProxyServerItem
{
    public GitHubProxyServer EnumValue { get; set; }
    public string DisplayName { get; set; } = string.Empty;

    public ProxyServerItem(GitHubProxyServer enumValue, string displayName)
    {
        EnumValue = enumValue;
        DisplayName = displayName;
    }

    // 本地兜底列表，远程未下发时使用
    public static List<ProxyServerItem> BuildFallback() => new()
    {
        new(GitHubProxyServer.GhDmrGg,           "gh.dmr.gg (Cloudflare 1)"),
        new(GitHubProxyServer.Gh1DmrGg,          "gh1.dmr.gg (腾讯云 1)"),
        new(GitHubProxyServer.Gh2DmrGg,          "gh2.dmr.gg (腾讯云 2)"),
        new(GitHubProxyServer.EdgeOneGhProxyCom, "edgeone.gh-proxy.com (腾讯云 3)"),
        new(GitHubProxyServer.GhProxyCom,        "gh-proxy.com (Cloudflare 2)"),
        new(GitHubProxyServer.HkGhProxyCom,      "hk.gh-proxy.com (Hong Kong)"),
        new(GitHubProxyServer.CdnGhProxyCom,     "cdn.gh-proxy.com (Fastly)"),
    };

    // 从远程下发列表构建，未知 Id 跳过，空时回退到本地兜底
    public static List<ProxyServerItem> BuildFromRemote(
        List<MainConfigProxyServer> remote, string lang)
    {
        var result = new List<ProxyServerItem>();
        foreach (var ps in remote)
        {
            if (!Enum.TryParse<GitHubProxyServer>(ps.Id, out var enumVal))
                continue;
            var name = ps.DisplayName.TryGetValue(lang, out var n) ? n : ps.Domain;
            result.Add(new ProxyServerItem(enumVal, $"{ps.Domain} ({name})"));
        }
        return result.Count > 0 ? result : BuildFallback();
    }
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
    public bool IsFirstRun { get; set; } = true;
    public AppTheme Theme { get; set; } = AppTheme.Light;
    public bool UseGitHubProxy { get; set; } = false;
    public GitHubProxyServer GitHubProxyServer { get; set; } = GitHubProxyServer.GhDmrGg;
    public UpdateChannel UpdateChannel { get; set; } = UpdateChannel.Stable;
    public string GitHubApiToken { get; set; } = string.Empty;
}

/// <summary>
/// 主配置文件模型，从远程下发
/// </summary>
public class MainConfig
{
    public string? ModConfigUrl { get; set; }
    public string? TranslationConfigUrl { get; set; }
    public string? ModI18nUrl { get; set; }
    public List<MainConfigProxyServer>? ProxyServers { get; set; }
}

/// <summary>
/// 主配置下发的代理服务器条目
/// </summary>
public class MainConfigProxyServer
{
    public string Id { get; set; } = string.Empty;   // 对应 GitHubProxyServer 枚举名
    public string Domain { get; set; } = string.Empty;
    public Dictionary<string, string> DisplayName { get; set; } = new(); // zh-CN / en-US
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

    // 可选扩展字段（向后兼容，缺失时为null/空集合）
    [JsonProperty("Description")]
    public Dictionary<string, string>? Description { get; set; }

    [JsonProperty("Tags")]
    public Dictionary<string, Dictionary<string, string>> Tags { get; set; } = new();

    [JsonProperty("SupportedGameVersions")]
    public List<string> SupportedGameVersions { get; set; } = new();
}

public enum InstallMethod
{
    DirectRelease,
    Scripted
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

// MOD依赖状态枚举
public enum DependencyStatus
{
    Unknown,    // 未检查
    Satisfied,  // 所有依赖已安装且启用
    Missing     // 存在缺失或禁用的依赖
}

public class ModViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

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
    public bool IsTranslationPlugin { get; set; }
    public bool IsSupportedManualMod { get; set; }
    public bool IsUnsupportedManualMod { get; set; }
    // 是否有卸载备份可快速重装
    public bool HasBackup { get; set; }
    private ModConfig _config = new();
    public ModConfig Config
    {
        get => _config;
        set
        {
            _config = value;
            // Config变更时通知WPF重新读取所有本地化属性
            OnPropertyChanged(nameof(LocalizedTags));
            OnPropertyChanged(nameof(LocalizedDescription));
            OnPropertyChanged(nameof(LocalizedTagsText));
            OnPropertyChanged(nameof(Tags));
            OnPropertyChanged(nameof(SupportedGameVersions));
            OnPropertyChanged(nameof(SupportedVersionsText));
        }
    }

    public ModViewModel()
    {
    }
    public DateTime? UpdateDate { get; set; }  // MOD更新日期

    // 新增：按需加载的历史版本列表（用于版本选择下拉）
    public List<GitHubRelease>? AvailableReleases { get; set; }

    // 新增：依赖状态（在InstalledModsView中内联显示图标）
    public DependencyStatus DependencyStatus { get; set; } = DependencyStatus.Unknown;

    // Tags原始字典
    public Dictionary<string, Dictionary<string, string>> Tags => _config?.Tags ?? new();

    // 缓存当前语言，由RefreshLocalization设置，避免依赖异步上下文中不可靠的CultureInfo
    private string _cachedLang = "en-US";

    // 本地化Tag显示名列表——使用缓存语言计算
    public List<string> LocalizedTags => ComputeLocalizedTags(_cachedLang);

    // 本地化简介——使用缓存语言计算
    public string? LocalizedDescription => ComputeLocalizedDescription(_cachedLang);

    // 支持的游戏版本列表
    public List<string> SupportedGameVersions => _config?.SupportedGameVersions ?? new();

    // 更新缓存语言并通知WPF重新读取本地化属性
    public void RefreshLocalization(string lang)
    {
        _cachedLang = lang;
        OnPropertyChanged(nameof(LocalizedTags));
        OnPropertyChanged(nameof(LocalizedDescription));
        OnPropertyChanged(nameof(LocalizedTagsText));
    }

    private string? ComputeLocalizedDescription(string lang)
    {
        if (_config?.Description == null || _config.Description.Count == 0)
            return null;

        // 精确匹配
        if (_config.Description.TryGetValue(lang, out var desc) && !string.IsNullOrWhiteSpace(desc))
            return desc;

        // 前缀匹配，处理 zh-Hans-CN / zh-CN 等变体（取语言代码前2段）
        var prefix = lang.Length >= 2 ? lang.Substring(0, 2) : lang;
        var prefixMatch = _config.Description
            .FirstOrDefault(kv => kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(kv.Value));
        if (prefixMatch.Value != null)
            return prefixMatch.Value;

        if (_config.Description.TryGetValue("en-US", out var enDesc) && !string.IsNullOrWhiteSpace(enDesc))
            return enDesc;
        return _config.Description.Values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
    }

    private List<string> ComputeLocalizedTags(string lang)
    {
        if (_config?.Tags == null || _config.Tags.Count == 0)
            return new();

        var prefix = lang.Length >= 2 ? lang.Substring(0, 2) : lang;
        return _config.Tags.Select(kv =>
        {
            var names = kv.Value;
            if (names == null || names.Count == 0) return kv.Key;

            // 精确匹配
            if (names.TryGetValue(lang, out var n) && !string.IsNullOrWhiteSpace(n)) return n;

            // 前缀匹配，处理语言变体
            var prefixMatch = names.FirstOrDefault(p => p.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(p.Value));
            if (prefixMatch.Value != null) return prefixMatch.Value;

            if (names.TryGetValue("en-US", out var en) && !string.IsNullOrWhiteSpace(en)) return en;
            return names.Values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? kv.Key;
        }).ToList();
    }

    // Tags逗号拼接字符串（用于列表页单行显示）
    public string LocalizedTagsText => LocalizedTags.Count > 0 ? string.Join(", ", LocalizedTags) : string.Empty;

    // 支持版本逗号拼接字符串（用于列表页单行显示）
    public string SupportedVersionsText => SupportedGameVersions.Count > 0 ? string.Join(", ", SupportedGameVersions) : string.Empty;

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
    
    // 未安装且有备份：可快速重装
    public bool CanQuickReinstall => !IsInstalled && HasBackup && !IsTranslationPlugin;

    // 未安装且无备份：正常安装
    public bool CanInstallFresh => !IsInstalled && !HasBackup && !IsTranslationPlugin;

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