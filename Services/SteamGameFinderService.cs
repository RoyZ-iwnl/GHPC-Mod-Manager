using Microsoft.Win32;
using System.IO;
using System.Text.RegularExpressions;
using GHPC_Mod_Manager.Resources;

namespace GHPC_Mod_Manager.Services;

public interface ISteamGameFinderService
{
    /// <summary>
    /// 自动查找GHPC游戏安装路径
    /// </summary>
    /// <returns>返回找到的游戏路径列表，按可能性排序</returns>
    Task<List<string>> FindGHPCGamePathsAsync();

    /// <summary>
    /// 获取所有Steam游戏库路径
    /// </summary>
    /// <returns>Steam游戏库路径列表</returns>
    Task<List<string>> GetSteamLibraryPathsAsync();

    /// <summary>
    /// 在指定路径中搜索GHPC游戏
    /// </summary>
    /// <param name="searchPath">搜索路径</param>
    /// <returns>找到的游戏路径列表</returns>
    Task<List<string>> SearchGHPCInPathAsync(string searchPath);
}

public class SteamGameFinderService : ISteamGameFinderService
{
    private readonly ILoggingService _loggingService;

    // GHPC在Steam中的AppID
    private const string GHPC_APP_ID = "665650";

    public SteamGameFinderService(ILoggingService loggingService)
    {
        _loggingService = loggingService;
    }

    public async Task<List<string>> FindGHPCGamePathsAsync()
    {
        var foundPaths = new List<string>();

        try
        {
            _loggingService.LogInfo(Strings.AutoSearchStarted);

            // 1. 获取所有Steam游戏库路径
            var steamLibraries = await GetSteamLibraryPathsAsync();

            foreach (var library in steamLibraries)
            {
                // 2. 在每个游戏库中搜索GHPC
                var gamesInLibrary = await SearchGHPCInPathAsync(library);
                foundPaths.AddRange(gamesInLibrary);
            }

            // 3. 去重并排序（按优先级）
            foundPaths = foundPaths.Distinct().ToList();

            _loggingService.LogInfo(string.Format(Strings.AutoSearchCompleted, foundPaths.Count));
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.AutoSearchError);
        }

        return foundPaths;
    }

    public async Task<List<string>> GetSteamLibraryPathsAsync()
    {
        var libraryPaths = new List<string>();

        await Task.Run(() =>
        {
            try
            {
                // 1. 从注册表获取Steam安装路径
                var steamPath = GetSteamInstallationPath();
                if (string.IsNullOrEmpty(steamPath))
                {
                    _loggingService.LogWarning(Strings.SteamPathNotFound);
                    return;
                }

                _loggingService.LogInfo(string.Format(Strings.SteamInstallationPath, steamPath));

                // 2. 添加默认的Steam库路径
                var defaultLibraryPath = Path.Combine(steamPath, "steamapps");
                if (Directory.Exists(defaultLibraryPath))
                {
                    libraryPaths.Add(steamPath);
                    _loggingService.LogInfo(string.Format(Strings.DefaultSteamLibrary, steamPath));
                }

                // 3. 解析libraryfolders.vdf获取其他游戏库
                var libraryFoldersConfig = Path.Combine(defaultLibraryPath, "libraryfolders.vdf");
                if (File.Exists(libraryFoldersConfig))
                {
                    var additionalPaths = ParseLibraryFoldersVdf(libraryFoldersConfig);
                    foreach (var path in additionalPaths)
                    {
                        if (!libraryPaths.Contains(path) && Directory.Exists(path))
                        {
                            libraryPaths.Add(path);
                            _loggingService.LogInfo(string.Format(Strings.AdditionalSteamLibrary, path));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _loggingService.LogError(ex, Strings.AutoSearchError);
            }
        });

        return libraryPaths;
    }

    public async Task<List<string>> SearchGHPCInPathAsync(string searchPath)
    {
        var foundGames = new List<string>();

        await Task.Run(() =>
        {
            try
            {
                // 方法1: 直接搜索GHPC.exe
                var gameExePath = Path.Combine(searchPath, "steamapps", "common", "Gunner, HEAT, PC!", "Bin", "GHPC.exe");
                if (File.Exists(gameExePath))
                {
                    foundGames.Add(Path.GetDirectoryName(gameExePath)!);
                    _loggingService.LogInfo(string.Format(Strings.GHPCFoundInLibrary, searchPath, gameExePath));
                }

                // 方法2: 通过AppID查找（如果manifest文件存在）
                var manifestPath = Path.Combine(searchPath, "steamapps", $"appmanifest_{GHPC_APP_ID}.acf");
                if (File.Exists(manifestPath))
                {
                    var installDir = ParseInstallDirFromManifest(manifestPath);
                    if (!string.IsNullOrEmpty(installDir))
                    {
                        var gamePath = Path.Combine(searchPath, "steamapps", "common", installDir, "Bin");
                        if (Directory.Exists(gamePath) && File.Exists(Path.Combine(gamePath, "GHPC.exe")))
                        {
                            if (!foundGames.Contains(gamePath))
                            {
                                foundGames.Add(gamePath);
                                _loggingService.LogInfo(string.Format(Strings.GHPCFoundViaManifest, gamePath));
                            }
                        }
                    }
                }

                // 方法3: 通用搜索（如果上面的方法都失败）
                if (foundGames.Count == 0)
                {
                    var commonPath = Path.Combine(searchPath, "steamapps", "common");
                    if (Directory.Exists(commonPath))
                    {
                        var gunnerDirs = Directory.GetDirectories(commonPath, "*Gunner*");
                        foreach (var dir in gunnerDirs)
                        {
                            var binPath = Path.Combine(dir, "Bin");
                            if (Directory.Exists(binPath) && File.Exists(Path.Combine(binPath, "GHPC.exe")))
                            {
                                if (!foundGames.Contains(binPath))
                                {
                                    foundGames.Add(binPath);
                                    _loggingService.LogInfo(string.Format(Strings.GHPCFoundViaGeneralSearch, binPath));
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _loggingService.LogError(ex, string.Format(Strings.SearchingGHPCInLibraryError, searchPath));
            }
        });

        return foundGames;
    }

    private string? GetSteamInstallationPath()
    {
        try
        {
            // 查询注册表获取Steam安装路径
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            var steamPath = key?.GetValue("SteamPath")?.ToString();

            if (!string.IsNullOrEmpty(steamPath))
            {
                return steamPath;
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.ReadingSteamPathFromRegistryFailed);
        }

        return null;
    }

    private List<string> ParseLibraryFoldersVdf(string vdfPath)
    {
        var paths = new List<string>();

        try
        {
            var content = File.ReadAllText(vdfPath);

            // 使用正则表达式解析VDF文件中的路径
            // 格式: "path"        "D:\\Games\\Steam"
            var pathPattern = "\"path\"\\s*\"([^\"]+)\"";
            var matches = Regex.Matches(content, pathPattern);

            foreach (Match match in matches)
            {
                var path = match.Groups[1].Value;
                if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                {
                    paths.Add(path);
                }
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.ParsingLibraryFoldersConfigFailed);
        }

        return paths;
    }

    private string? ParseInstallDirFromManifest(string manifestPath)
    {
        try
        {
            var content = File.ReadAllText(manifestPath);

            // 解析manifest中的安装目录
            // 格式: "installdir"        "Gunner, HEAT, PC!"
            var installDirPattern = "\"installdir\"\\s*\"([^\"]+)\"";
            var match = Regex.Match(content, installDirPattern);

            if (match.Success)
            {
                return match.Groups[1].Value;
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError(ex, Strings.ParsingSteamManifestFailed);
        }

        return null;
    }
}