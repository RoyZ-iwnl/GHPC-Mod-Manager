using GHPC_Mod_Manager.Models;
using GHPC_Mod_Manager.Resources;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Net.Http;
using Newtonsoft.Json;

namespace GHPC_Mod_Manager.Services;

public interface IUpdateService
{
    Task<(bool hasUpdate, string? latestVersion, string? downloadUrl)> CheckForUpdatesAsync(bool includePrerelease = false);
    Task<bool> DownloadAndInstallUpdateAsync(string downloadUrl, string version, IProgress<DownloadProgress>? progress = null);
    string GetCurrentVersion();
}

public class UpdateService : IUpdateService
{
    private readonly INetworkService _networkService;
    private readonly ILoggingService _loggingService;
    private readonly ISettingsService _settingsService;

    public UpdateService(
        INetworkService networkService,
        ILoggingService loggingService,
        ISettingsService settingsService)
    {
        _networkService = networkService;
        _loggingService = loggingService;
        _settingsService = settingsService;
    }

    public string GetCurrentVersion()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                         ?? assembly.GetName().Version?.ToString()
                         ?? "Unknown";

            // Remove build metadata if present (e.g., "1.0.0+abc" -> "1.0.0")
            var plusIndex = version.IndexOf('+');
            if (plusIndex > 0)
            {
                version = version.Substring(0, plusIndex);
            }

            return version;
        }
        catch
        {
            return "Unknown";
        }
    }

    public async Task<(bool hasUpdate, string? latestVersion, string? downloadUrl)> CheckForUpdatesAsync(bool includePrerelease = false)
    {
        return await CheckForUpdatesWithFallbackAsync(includePrerelease);
    }

    /// <summary>
    /// 智能回退的更新检查机制
    /// 1. 根据设置优先使用代理或直接访问
    /// 2. 失败时尝试所有代理服务器
    /// 3. 最后尝试直接访问GitHub API
    /// </summary>
    private async Task<(bool hasUpdate, string? latestVersion, string? downloadUrl)> CheckForUpdatesWithFallbackAsync(bool includePrerelease)
    {
        var checkMethods = new List<(string name, Func<Task<List<GitHubRelease>?>> checker)>();

        // 根据用户设置构建检查方法列表
        if (_settingsService.Settings.UseGitHubProxy)
        {
            // 如果启用了代理，优先使用选中的代理
            var primaryProxy = GetProxyDomain(_settingsService.Settings.GitHubProxyServer);
            checkMethods.Add(($"Proxy: {primaryProxy}", () => CheckWithProxy(_settingsService.Settings.GitHubProxyServer)));

            // 添加其他代理作为备选
            var allProxies = GetAllProxyServers().Where(p => p != _settingsService.Settings.GitHubProxyServer);
            foreach (var proxy in allProxies)
            {
                var proxyName = GetProxyDomain(proxy);
                checkMethods.Add(($"Proxy: {proxyName}", () => CheckWithProxy(proxy)));
            }

            // 最后尝试直接访问
            checkMethods.Add(("Direct GitHub API", () => CheckDirectAsync()));
        }
        else
        {
            // 如果未启用代理，优先尝试直接访问
            checkMethods.Add(("Direct GitHub API", () => CheckDirectAsync()));

            // 失败后尝试所有代理服务器
            var allProxies = GetAllProxyServers();
            foreach (var proxy in allProxies)
            {
                var proxyName = GetProxyDomain(proxy);
                checkMethods.Add(($"Proxy: {proxyName}", () => CheckWithProxy(proxy)));
            }
        }

        // 逐个尝试检查方法
        foreach (var (methodName, checker) in checkMethods)
        {
            try
            {
                _loggingService.LogInfo(Strings.UpdateCheckUsingProxy, methodName);

                var releases = await checker();

                if (releases != null && releases.Any())
                {
                    _loggingService.LogInfo(Strings.UpdateCheckSuccessWithMethod, methodName);
                    return ProcessReleases(releases, includePrerelease);
                }
                else
                {
                    _loggingService.LogWarning(Strings.NoReleasesFound);
                }
            }
            catch (Exception ex)
            {
                _loggingService.LogWarning(Strings.UpdateCheckProxyFailed, methodName, ex.Message);

                // 如果是最后一次尝试，记录错误日志
                if (checkMethods.Last().name == methodName)
                {
                    _loggingService.LogError(ex, Strings.UpdateCheckAllMethodsFailed);
                }
            }
        }

        _loggingService.LogError(new Exception("All update check methods failed"), Strings.UpdateCheckAllMethodsFailed);
        return (false, null, null);
    }

    private async Task<List<GitHubRelease>?> CheckWithProxy(GitHubProxyServer proxyServer)
    {
        var proxyDomain = GetProxyDomain(proxyServer);
        var proxyUrl = $"https://{proxyDomain}/https://api.github.com/repos/RoyZ-iwnl/GHPC-Mod-Manager/releases";

        // 使用NetworkService的GetGitHubReleasesAsync方法，但需要修改URL为代理URL
        // 由于现有的NetworkService不支持自定义URL格式，我们需要直接实现代理请求
        return await GetGitHubReleasesViaProxyAsync(proxyUrl);
    }

    private async Task<List<GitHubRelease>?> CheckDirectAsync()
    {
        return await _networkService.GetGitHubReleasesAsync("RoyZ-iwnl", "GHPC-Mod-Manager", forceRefresh: true);
    }

    private async Task<List<GitHubRelease>?> GetGitHubReleasesViaProxyAsync(string proxyUrl)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, proxyUrl);
            request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

            using var response = await _networkService.HttpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var releases = JsonConvert.DeserializeObject<List<GitHubRelease>>(json);

            return releases;
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.FailedToFetchReleasesViaProxy, proxyUrl);
            throw;
        }
    }

    private (bool hasUpdate, string? latestVersion, string? downloadUrl) ProcessReleases(List<GitHubRelease> releases, bool includePrerelease)
    {
        // Filter releases based on update channel setting
        var updateChannel = _settingsService.Settings.UpdateChannel;
        List<GitHubRelease> availableReleases;

        if (updateChannel == Models.UpdateChannel.Beta)
        {
            // Beta channel: include all releases (stable + prerelease)
            availableReleases = releases.ToList();
        }
        else
        {
            // Stable channel: only include releases without any letters in version number
            availableReleases = releases.Where(r =>
            {
                var version = r.TagName.TrimStart('v');
                // Check if version contains only digits, dots, and hyphens followed by digits
                // Pure stable versions: 1.0.0, 1.0.1, etc. (no letters)
                // Exclude: 1.0.0-beta.1, 1.1.0-rc.1, etc. (contains letters)
                return !System.Text.RegularExpressions.Regex.IsMatch(version, @"[a-zA-Z]");
            }).ToList();
        }

        if (!availableReleases.Any())
        {
            _loggingService.LogWarning(Strings.NoStableReleasesFound);
            return (false, null, null);
        }

        var latestRelease = availableReleases.First();
        var latestVersion = latestRelease.TagName.TrimStart('v');
        var currentVersion = GetCurrentVersion();

        // Compare versions
        if (!IsNewerVersion(currentVersion, latestVersion))
        {
            // No log needed - this is normal operation
            return (false, latestVersion, null);
        }

        // Find download URL for the update package
        var asset = latestRelease.Assets.FirstOrDefault(a =>
            a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

        if (asset == null)
        {
            _loggingService.LogWarning(Strings.NoDownloadableAsset);
            return (false, latestVersion, null);
        }

        _loggingService.LogInfo(Strings.UpdateAvailable, latestVersion, currentVersion);
        return (true, latestVersion, asset.DownloadUrl);
    }

    private List<GitHubProxyServer> GetAllProxyServers()
    {
        return new List<GitHubProxyServer>
        {
            GitHubProxyServer.GhDmrGg,
            GitHubProxyServer.GhProxyCom,
            GitHubProxyServer.HkGhProxyCom,
            GitHubProxyServer.CdnGhProxyCom,
            GitHubProxyServer.EdgeOneGhProxyCom
        };
    }

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

    public async Task<bool> DownloadAndInstallUpdateAsync(string downloadUrl, string version, IProgress<DownloadProgress>? progress = null)
    {
        try
        {
            _loggingService.LogInfo(Strings.DownloadingUpdate, version);

            // Download update package to temp directory
            var tempDir = Path.Combine(_settingsService.TempPath, "updates");
            Directory.CreateDirectory(tempDir);

            var fileName = Path.GetFileName(new Uri(downloadUrl).LocalPath);
            var downloadPath = Path.Combine(tempDir, fileName);

            var fileData = await _networkService.DownloadFileAsync(downloadUrl, progress);
            await File.WriteAllBytesAsync(downloadPath, fileData);

            _loggingService.LogInfo(Strings.UpdateDownloaded, downloadPath);

            // Create update PowerShell script
            var psScript = CreateUpdatePowerShellScript(downloadPath, fileName);
            var psPath = Path.Combine(tempDir, "update.ps1");
            // Write with UTF-8 BOM for PowerShell
            await File.WriteAllTextAsync(psPath, psScript, new System.Text.UTF8Encoding(true));

            _loggingService.LogInfo(Strings.LaunchingUpdateScript);

            // Launch update script and exit application
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-ExecutionPolicy Bypass -NoProfile -File \"{psPath}\"",
                UseShellExecute = true,
                CreateNoWindow = false,
                WindowStyle = ProcessWindowStyle.Normal
            };

            Process.Start(startInfo);

            // Give the batch script time to start
            await Task.Delay(500);

            // Exit application
            Environment.Exit(0);

            return true;
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, "Update install failed");
            return false;
        }
    }

    private string CreateUpdatePowerShellScript(string updateFilePath, string fileName)
    {
        var currentExe = Process.GetCurrentProcess().MainModule?.FileName ?? "";
        var currentDir = Path.GetDirectoryName(currentExe) ?? "";
        var isZip = fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

        // Get localized strings
        var title = Strings.BatchUpdateTitle;
        var waiting = Strings.BatchWaitingForClose;
        var extracting = Strings.BatchExtractingUpdate;
        var extractFailed = Strings.BatchExtractFailed;
        var installing = Strings.BatchInstallingUpdate;
        var copyFailed = Strings.BatchCopyFailed;
        var completed = Strings.BatchUpdateCompleted;
        var restarting = Strings.BatchRestartingApp;
        var cleanup = Strings.BatchCleaningUp;

        var script = $@"# PowerShell Update Script
# Set console encoding and clear screen
$OutputEncoding = [System.Text.Encoding]::UTF8
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
chcp 65001 > $null
Clear-Host

# Set window title
$Host.UI.RawUI.WindowTitle = '{title}'

# Display header
Write-Host '========================================' -ForegroundColor Cyan
Write-Host '{title}' -ForegroundColor Cyan
Write-Host '========================================' -ForegroundColor Cyan
Write-Host ''
Write-Host '{waiting}' -ForegroundColor Yellow
Start-Sleep -Seconds 2
Write-Host ''

";

        if (isZip)
        {
            script += $@"
Write-Host '{extracting}' -ForegroundColor Yellow
try {{
    Expand-Archive -Path '{updateFilePath}' -DestinationPath '{currentDir}' -Force -ErrorAction Stop
    Clear-Host
    Write-Host '========================================' -ForegroundColor Cyan
    Write-Host '{title}' -ForegroundColor Cyan
    Write-Host '========================================' -ForegroundColor Cyan
    Write-Host ''
    Write-Host '{completed}' -ForegroundColor Green
}} catch {{
    Clear-Host
    Write-Host '========================================' -ForegroundColor Red
    Write-Host '{title}' -ForegroundColor Red
    Write-Host '========================================' -ForegroundColor Red
    Write-Host ''
    Write-Host '{extractFailed}' -ForegroundColor Red
    Write-Host ('Error: ' + $_.Exception.Message) -ForegroundColor Red
    Write-Host ''
    Write-Host 'Press any key to exit...' -ForegroundColor Yellow
    $null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')
    exit 1
}}
";
        }
        else
        {
            script += $@"
Write-Host '{installing}' -ForegroundColor Yellow
try {{
    Copy-Item -Path '{updateFilePath}' -Destination '{currentExe}' -Force -ErrorAction Stop
    Clear-Host
    Write-Host '========================================' -ForegroundColor Cyan
    Write-Host '{title}' -ForegroundColor Cyan
    Write-Host '========================================' -ForegroundColor Cyan
    Write-Host ''
    Write-Host '{completed}' -ForegroundColor Green
}} catch {{
    Clear-Host
    Write-Host '========================================' -ForegroundColor Red
    Write-Host '{title}' -ForegroundColor Red
    Write-Host '========================================' -ForegroundColor Red
    Write-Host ''
    Write-Host '{copyFailed}' -ForegroundColor Red
    Write-Host ('Error: ' + $_.Exception.Message) -ForegroundColor Red
    Write-Host ''
    Write-Host 'Press any key to exit...' -ForegroundColor Yellow
    $null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')
    exit 1
}}
";
        }

        script += $@"
Write-Host ''
Write-Host '{restarting}' -ForegroundColor Green
Start-Sleep -Seconds 2

# Start the application
Start-Process -FilePath '{currentExe}'

# Cleanup
Write-Host '{cleanup}' -ForegroundColor Gray
Start-Sleep -Seconds 1
Remove-Item -Path '{updateFilePath}' -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500
Remove-Item -Path $PSCommandPath -Force -ErrorAction SilentlyContinue
";

        return script;
    }

    private bool IsNewerVersion(string currentVersion, string latestVersion)
    {
        try
        {
            // Remove 'v' prefix if present
            currentVersion = currentVersion.TrimStart('v');
            latestVersion = latestVersion.TrimStart('v');

            var current = ParseSemanticVersion(currentVersion);
            var latest = ParseSemanticVersion(latestVersion);

            return CompareSemanticVersions(current, latest) > 0;
        }
        catch
        {
            return false;
        }
    }

    private (int[] version, string? prerelease) ParseSemanticVersion(string version)
    {
        // Split version and prerelease parts
        // Example: "1.0.0-beta.1" -> version=[1,0,0], prerelease="beta.1"
        var dashIndex = version.IndexOf('-');
        string? prerelease = null;
        string versionPart = version;

        if (dashIndex > 0)
        {
            versionPart = version.Substring(0, dashIndex);
            prerelease = version.Substring(dashIndex + 1);
        }

        // Parse version numbers
        var versionNumbers = versionPart.Split('.')
            .Select(part => int.TryParse(part, out var num) ? num : 0)
            .ToArray();

        return (versionNumbers, prerelease);
    }

    private int CompareSemanticVersions((int[] version, string? prerelease) current, (int[] version, string? prerelease) latest)
    {
        // First, compare version numbers
        for (int i = 0; i < Math.Max(current.version.Length, latest.version.Length); i++)
        {
            var currentPart = i < current.version.Length ? current.version[i] : 0;
            var latestPart = i < latest.version.Length ? latest.version[i] : 0;

            if (latestPart > currentPart)
                return 1;  // latest is newer
            if (latestPart < currentPart)
                return -1; // current is newer
        }

        // Version numbers are equal, compare prerelease
        // No prerelease (stable) > has prerelease (alpha/beta/rc)
        if (current.prerelease == null && latest.prerelease == null)
            return 0; // Both are stable and equal
        if (current.prerelease == null)
            return -1; // Current is stable, latest is prerelease
        if (latest.prerelease == null)
            return 1; // Latest is stable, current is prerelease

        // Both have prerelease, compare them
        return ComparePrereleaseVersions(current.prerelease, latest.prerelease);
    }

    private int ComparePrereleaseVersions(string current, string latest)
    {
        // Prerelease priority: alpha < beta < rc
        var priorityMap = new Dictionary<string, int>
        {
            { "alpha", 1 },
            { "beta", 2 },
            { "rc", 3 }
        };

        var currentType = GetPrereleaseType(current);
        var latestType = GetPrereleaseType(latest);

        var currentPriority = priorityMap.ContainsKey(currentType) ? priorityMap[currentType] : 0;
        var latestPriority = priorityMap.ContainsKey(latestType) ? priorityMap[latestType] : 0;

        if (latestPriority > currentPriority)
            return 1;
        if (latestPriority < currentPriority)
            return -1;

        // Same prerelease type, compare version numbers
        // e.g., "beta.1" vs "beta.2"
        var currentNum = GetPrereleaseNumber(current);
        var latestNum = GetPrereleaseNumber(latest);

        if (latestNum > currentNum)
            return 1;
        if (latestNum < currentNum)
            return -1;

        return 0;
    }

    private string GetPrereleaseType(string prerelease)
    {
        // Extract prerelease type from strings like "beta.1", "rc.2", "alpha"
        var dotIndex = prerelease.IndexOf('.');
        if (dotIndex > 0)
            return prerelease.Substring(0, dotIndex).ToLowerInvariant();
        return prerelease.ToLowerInvariant();
    }

    private int GetPrereleaseNumber(string prerelease)
    {
        // Extract number from strings like "beta.1", "rc.2"
        var dotIndex = prerelease.IndexOf('.');
        if (dotIndex > 0 && dotIndex < prerelease.Length - 1)
        {
            var numberPart = prerelease.Substring(dotIndex + 1);
            if (int.TryParse(numberPart, out var num))
                return num;
        }
        return 0;
    }
}
