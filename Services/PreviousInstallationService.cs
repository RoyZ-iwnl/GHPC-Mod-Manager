using Microsoft.Win32;
using System.Diagnostics;
using System.IO;

namespace GHPC_Mod_Manager.Services;

public class PreviousInstallationService : IPreviousInstallationService
{
    private const string RegistrySubKey = @"Software\GHPCModManager";
    private const string RegistryValueName = "LastAppPath";

    public void SaveCurrentAppPath()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistrySubKey);
            key.SetValue(RegistryValueName, AppDomain.CurrentDomain.BaseDirectory);
        }
        catch
        {
            // 忽略注册表写入失败，不影响程序运行
        }
    }

    public string? GetPreviousAppPath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistrySubKey);
            return key?.GetValue(RegistryValueName)?.ToString();
        }
        catch
        {
            return null;
        }
    }

    public bool HasPreviousInstallation()
    {
        var previousPath = GetPreviousAppPath();
        if (string.IsNullOrWhiteSpace(previousPath)) return false;
        if (!Directory.Exists(previousPath)) return false; // 旧目录已不存在则忽略

        var currentPath = AppDomain.CurrentDomain.BaseDirectory
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var storedPath = previousPath
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // 忽略大小写比较（Windows 路径不区分大小写）
        return !string.Equals(currentPath, storedPath, StringComparison.OrdinalIgnoreCase);
    }

    public string? GetPreviousAppExePath()
    {
        var previousPath = GetPreviousAppPath();
        if (string.IsNullOrWhiteSpace(previousPath)) return null;
        if (!Directory.Exists(previousPath)) return null;

        // 查找与当前进程同名的exe
        var currentExeName = Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrEmpty(currentExeName)) return null;

        var exeName = Path.GetFileName(currentExeName);
        var previousExePath = Path.Combine(previousPath, exeName);

        return File.Exists(previousExePath) ? previousExePath : null;
    }

    public string? GetPreviousAppVersion()
    {
        var exePath = GetPreviousAppExePath();
        if (string.IsNullOrEmpty(exePath)) return null;

        try
        {
            var versionInfo = FileVersionInfo.GetVersionInfo(exePath);
            return versionInfo.ProductVersion;
        }
        catch
        {
            return null;
        }
    }
}