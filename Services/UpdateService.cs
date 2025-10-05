using GHPC_Mod_Manager.Models;
using GHPC_Mod_Manager.Resources;
using System.Diagnostics;
using System.IO;
using System.Reflection;

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
        try
        {
            // Get releases from GitHub
            var releases = await _networkService.GetGitHubReleasesAsync("RoyZ-iwnl", "GHPC-Mod-Manager", forceRefresh: true);

            if (releases == null || !releases.Any())
            {
                _loggingService.LogWarning(Strings.NoReleasesFound);
                return (false, null, null);
            }

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
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.Error);
            return (false, null, null);
        }
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
