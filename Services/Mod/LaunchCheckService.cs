using GHPC_Mod_Manager.Models;
using GHPC_Mod_Manager.Resources;
using GHPC_Mod_Manager.ViewModels;
using System.IO;

namespace GHPC_Mod_Manager.Services.Mod;

public record LaunchCheckEvaluation
{
    public LaunchCheckStepStatus Status { get; init; } = LaunchCheckStepStatus.Passed;
    public List<string> Details { get; init; } = new();
}

public interface ILaunchCheckService
{
    Task<LaunchCheckEvaluation> CheckGamePathAsync(string? gameRootPath);
    Task<LaunchCheckEvaluation> CheckModUpdatesAsync(IEnumerable<ModViewModel> mods, bool onlineMode);
    Task<LaunchCheckEvaluation> CheckConflictsAsync(IEnumerable<ModViewModel> mods);
    Task<LaunchCheckEvaluation> CheckDependenciesAsync(IEnumerable<ModViewModel> mods);
    Task<LaunchCheckEvaluation> CheckDelistedModsAsync(IEnumerable<ModViewModel> mods);
    Task<LaunchCheckEvaluation> CheckIntegrityAsync();
    Task<LaunchCheckEvaluation> CheckGameVersionCompatibilityAsync(string? gameRootPath, IEnumerable<ModViewModel> mods);
    Task<LaunchCheckEvaluation> CheckManagerVersionAsync(bool onlineMode);
}

public class LaunchCheckService : ILaunchCheckService
{
    private readonly IModManagerService _modManagerService;
    private readonly INetworkService _networkService;
    private readonly IUpdateService _updateService;
    private readonly IMelonLoaderService _melonLoaderService;
    private readonly ILoggingService _loggingService;

    public LaunchCheckService(
        IModManagerService modManagerService,
        INetworkService networkService,
        IUpdateService updateService,
        IMelonLoaderService melonLoaderService,
        ILoggingService loggingService)
    {
        _modManagerService = modManagerService;
        _networkService = networkService;
        _updateService = updateService;
        _melonLoaderService = melonLoaderService;
        _loggingService = loggingService;
    }

    public Task<LaunchCheckEvaluation> CheckGamePathAsync(string? gameRootPath)
    {
        if (string.IsNullOrWhiteSpace(gameRootPath))
        {
            return Task.FromResult(new LaunchCheckEvaluation
            {
                Status = LaunchCheckStepStatus.Failed,
                Details = new List<string> { Strings.GamePathNotSet }
            });
        }

        var exePath = Path.Combine(gameRootPath, "GHPC.exe");
        if (!File.Exists(exePath))
        {
            return Task.FromResult(new LaunchCheckEvaluation
            {
                Status = LaunchCheckStepStatus.Failed,
                Details = new List<string> { Strings.GameExeNotFound }
            });
        }

        return Task.FromResult(new LaunchCheckEvaluation
        {
            Status = LaunchCheckStepStatus.Passed,
            Details = new List<string> { gameRootPath }
        });
    }

    public async Task<LaunchCheckEvaluation> CheckModUpdatesAsync(IEnumerable<ModViewModel> mods, bool onlineMode)
    {
        var installedMods = mods.Where(m => m.IsInstalled && !m.IsManuallyInstalled).ToList();
        var updates = new List<string>();

        if (onlineMode)
        {
            var tasks = installedMods
                .Where(m => m.Config != null && !string.IsNullOrEmpty(m.Config.ReleaseUrl))
                .Select(async mod =>
                {
                    try
                    {
                        var owner = GetRepoOwnerFromApiUrl(mod.Config.ReleaseUrl);
                        var repo = GetRepoNameFromApiUrl(mod.Config.ReleaseUrl);
                        var releases = await _networkService.GetGitHubReleasesAsync(owner, repo, forceRefresh: true);
                        var latestVersion = releases.FirstOrDefault()?.TagName;

                        var normalizedLatestVersion = NormalizeVersion(latestVersion);
                        var normalizedInstalledVersion = NormalizeVersion(mod.InstalledVersion);

                        if (!string.IsNullOrEmpty(normalizedLatestVersion) &&
                            !string.IsNullOrEmpty(normalizedInstalledVersion) &&
                            !string.Equals(normalizedInstalledVersion, normalizedLatestVersion, StringComparison.OrdinalIgnoreCase) &&
                            mod.InstalledVersion != Strings.Manual)
                        {
                            return ($"{mod.DisplayName}: {mod.InstalledVersion} → {latestVersion}", true);
                        }
                    }
                    catch (Exception ex)
                    {
                        _loggingService.LogError(ex, "Launch mod update check failed: {0}", mod.DisplayName);
                    }

                    return (string.Empty, false);
                });

            var results = await Task.WhenAll(tasks);
            updates.AddRange(results.Where(r => r.Item2).Select(r => r.Item1));
        }
        else
        {
            foreach (var mod in installedMods)
            {
                var normalizedLatestVersion = NormalizeVersion(mod.LatestVersion);
                var normalizedInstalledVersion = NormalizeVersion(mod.InstalledVersion);

                if (!string.IsNullOrEmpty(normalizedLatestVersion) &&
                    !string.IsNullOrEmpty(normalizedInstalledVersion) &&
                    !string.Equals(normalizedInstalledVersion, normalizedLatestVersion, StringComparison.OrdinalIgnoreCase) &&
                    mod.InstalledVersion != Strings.Manual)
                {
                    updates.Add($"{mod.DisplayName}: {mod.InstalledVersion} → {mod.LatestVersion}");
                }
            }
        }

        return new LaunchCheckEvaluation
        {
            Status = updates.Count > 0 ? LaunchCheckStepStatus.Warning : LaunchCheckStepStatus.Passed,
            Details = updates.Count > 0 ? updates : new List<string> { Strings.NoModUpdatesAvailable }
        };
    }

    public async Task<LaunchCheckEvaluation> CheckConflictsAsync(IEnumerable<ModViewModel> mods)
    {
        var modLookup = mods.ToDictionary(mod => mod.Id, mod => mod.DisplayName, StringComparer.OrdinalIgnoreCase);
        var (hasConflicts, conflicts) = await _modManagerService.CheckEnabledModsConflictsAsync();
        if (!hasConflicts)
        {
            return new LaunchCheckEvaluation { Status = LaunchCheckStepStatus.Passed };
        }

        var details = conflicts
            .Select(conflict =>
            {
                var mod1Name = GetModDisplayName(modLookup, conflict.ModId);
                var mod2Name = GetModDisplayName(modLookup, conflict.ConflictsWith);
                return string.Format(Strings.ModConflictsWith, mod1Name, mod2Name);
            })
            .ToList();

        return new LaunchCheckEvaluation
        {
            Status = LaunchCheckStepStatus.Failed,
            Details = details
        };
    }

    public async Task<LaunchCheckEvaluation> CheckDependenciesAsync(IEnumerable<ModViewModel> mods)
    {
        var modLookup = mods.ToDictionary(mod => mod.Id, mod => mod.DisplayName, StringComparer.OrdinalIgnoreCase);
        var (hasMissingDeps, missingDeps) = await _modManagerService.CheckEnabledModsDependenciesAsync();
        if (!hasMissingDeps)
        {
            return new LaunchCheckEvaluation { Status = LaunchCheckStepStatus.Passed };
        }

        var details = missingDeps
            .Select(dep =>
            {
                var modName = GetModDisplayName(modLookup, dep.ModId);
                var reqName = GetModDisplayName(modLookup, dep.RequiredMod);
                return string.Format(Strings.ModRequiresDependency, modName, reqName);
            })
            .ToList();

        return new LaunchCheckEvaluation
        {
            Status = LaunchCheckStepStatus.Failed,
            Details = details
        };
    }

    public Task<LaunchCheckEvaluation> CheckDelistedModsAsync(IEnumerable<ModViewModel> mods)
    {
        var delistedMods = mods.Where(m => m.IsInstalled && m.IsDelisted).Select(m => m.DisplayName).ToList();
        return Task.FromResult(new LaunchCheckEvaluation
        {
            Status = delistedMods.Count > 0 ? LaunchCheckStepStatus.Warning : LaunchCheckStepStatus.Passed,
            Details = delistedMods.Count > 0 ? delistedMods : new List<string>()
        });
    }

    public async Task<LaunchCheckEvaluation> CheckIntegrityAsync()
    {
        var issues = await _modManagerService.CheckManagedModsIntegrityAsync();
        if (!issues.Any())
        {
            return new LaunchCheckEvaluation { Status = LaunchCheckStepStatus.Passed };
        }

        var details = issues
            .Select(issue =>
            {
                var status = issue.IssueType == ModIntegrityIssueType.Missing
                    ? (Strings.ResourceManager.GetString("ManagedModIntegrityIssueMissing", Strings.Culture) ?? (Strings.Culture?.TwoLetterISOLanguageName == "zh" ? "缺失" : "Missing"))
                    : (Strings.ResourceManager.GetString("ManagedModIntegrityIssueModified", Strings.Culture) ?? (Strings.Culture?.TwoLetterISOLanguageName == "zh" ? "已修改" : "Modified"));
                return $"{issue.ModDisplayName} — {issue.RelativePath} [{status}]";
            })
            .ToList();

        return new LaunchCheckEvaluation
        {
            Status = LaunchCheckStepStatus.Warning,
            Details = details
        };
    }

    public async Task<LaunchCheckEvaluation> CheckGameVersionCompatibilityAsync(string? gameRootPath, IEnumerable<ModViewModel> mods)
    {
        if (string.IsNullOrWhiteSpace(gameRootPath))
        {
            return new LaunchCheckEvaluation { Status = LaunchCheckStepStatus.Passed };
        }

        var currentGameVersion = await _melonLoaderService.GetCurrentGameVersionAsync(gameRootPath);
        if (string.IsNullOrWhiteSpace(currentGameVersion))
        {
            return new LaunchCheckEvaluation { Status = LaunchCheckStepStatus.Passed };
        }

        var normalizedCurrentVersion = NormalizeVersion(currentGameVersion);
        if (string.IsNullOrWhiteSpace(normalizedCurrentVersion))
        {
            return new LaunchCheckEvaluation { Status = LaunchCheckStepStatus.Passed };
        }

        var incompatibleMods = new List<string>();
        foreach (var mod in mods.Where(m => m.IsInstalled))
        {
            var supportedVersions = mod.SupportedGameVersions
                .Select(NormalizeVersion)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Where(v => !string.Equals(v, Strings.Unknown, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (supportedVersions.Count == 0)
            {
                continue;
            }

            if (!supportedVersions.Contains(normalizedCurrentVersion, StringComparer.OrdinalIgnoreCase))
            {
                incompatibleMods.Add($"{mod.DisplayName} ({mod.SupportedVersionsText})");
            }
        }

        if (incompatibleMods.Count == 0)
        {
            return new LaunchCheckEvaluation
            {
                Status = LaunchCheckStepStatus.Passed,
                Details = new List<string> { $"{Strings.LaunchCheckGameVersion}: {currentGameVersion}" }
            };
        }

        return new LaunchCheckEvaluation
        {
            Status = LaunchCheckStepStatus.Warning,
            Details = new List<string>
            {
                $"{Strings.LaunchCheckGameVersion}: {currentGameVersion}"
            }
            .Concat(incompatibleMods.Select(mod => $"  - {mod}"))
            .ToList()
        };
    }

    public async Task<LaunchCheckEvaluation> CheckManagerVersionAsync(bool onlineMode)
    {
        var currentVersion = _updateService.GetCurrentVersion();
        if (!onlineMode)
        {
            return new LaunchCheckEvaluation
            {
                Status = LaunchCheckStepStatus.Passed,
                Details = new List<string> { $"{Strings.LaunchCheckCurrentVersion}: {currentVersion}" }
            };
        }

        var (hasUpdate, latestVersion, _, _, _) = await _updateService.CheckForUpdatesAsync();
        if (hasUpdate && !string.IsNullOrEmpty(latestVersion))
        {
            return new LaunchCheckEvaluation
            {
                Status = LaunchCheckStepStatus.Warning,
                Details = new List<string>
                {
                    $"{Strings.LaunchCheckCurrentVersion}: {currentVersion}",
                    $"{Strings.LatestVersion}: {latestVersion}"
                }
            };
        }

        return new LaunchCheckEvaluation
        {
            Status = LaunchCheckStepStatus.Passed,
            Details = new List<string> { $"{Strings.LaunchCheckCurrentVersion}: {currentVersion}" }
        };
    }

    private static string GetModDisplayName(IReadOnlyDictionary<string, string> modLookup, string modId)
        => modLookup.TryGetValue(modId, out var displayName) ? displayName : modId;

    private static string NormalizeVersion(string? version)
        => version?.Trim().TrimStart('v', 'V') ?? string.Empty;

    private static string GetRepoOwnerFromApiUrl(string? apiUrl)
    {
        if (string.IsNullOrEmpty(apiUrl)) return string.Empty;
        var segments = new Uri(apiUrl).AbsolutePath.Trim('/').Split('/');
        return segments.Length >= 2 ? segments[1] : string.Empty;
    }

    private static string GetRepoNameFromApiUrl(string? apiUrl)
    {
        if (string.IsNullOrEmpty(apiUrl)) return string.Empty;
        var segments = new Uri(apiUrl).AbsolutePath.Trim('/').Split('/');
        return segments.Length >= 3 ? segments[2] : string.Empty;
    }
}
