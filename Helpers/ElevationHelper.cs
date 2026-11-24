using System.IO;
using System.Security.Principal;

namespace GHPC_Mod_Manager.Helpers;

/// <summary>
/// 管理员权限按需申请：平时普通权限运行，只有目标文件夹写不进去时才提示以管理员身份重启
/// </summary>
public static class ElevationHelper
{
    /// <summary>
    /// 当前进程是否以管理员身份运行
    /// </summary>
    public static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 实际写一个临时文件判断能否写入（比读ACL可靠；清单声明了执行级别，不会被UAC虚拟化骗过）
    /// 文件夹不存在或路径为空时返回true（没有可检查的东西）
    /// </summary>
    public static bool CanWriteTo(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return true;

        var probe = Path.Combine(directory, $".ghpcmm_write_test_{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            // 文件被占用等其他IO问题不代表没有权限
            return true;
        }
    }

    /// <summary>
    /// 游戏目录里需要写入的文件夹中，当前权限写不进去的那些（游戏根目录、Mods）
    /// </summary>
    public static List<string> GetUnwritableGameFolders(string? gameRootPath)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(gameRootPath) || !Directory.Exists(gameRootPath))
            return result;

        foreach (var dir in new[] { gameRootPath, Path.Combine(gameRootPath, "Mods") })
        {
            if (!CanWriteTo(dir))
                result.Add(dir);
        }
        return result;
    }
}
