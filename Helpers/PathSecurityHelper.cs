using System.IO;

namespace GHPC_Mod_Manager.Helpers;

/// <summary>
/// 路径安全校验工具,防止 zip-slip 等路径遍历攻击
/// </summary>
internal static class PathSecurityHelper
{
    /// <summary>
    /// 校验 fullPath 解析后位于 baseDirectory 内部,返回规范化后的绝对路径。
    /// 解析 .. 和 . 段,基目录末尾补分隔符避免前缀误匹配(C:\Game 不应匹配 C:\GameFoo)。
    /// 越界时抛 IOException,由调用方 catch 统一处理。
    /// </summary>
    public static string EnsurePathInsideDirectory(string fullPath, string baseDirectory)
    {
        var normalizedFull = Path.GetFullPath(fullPath);
        var normalizedBase = Path.GetFullPath(baseDirectory);

        if (!normalizedBase.EndsWith(Path.DirectorySeparatorChar))
            normalizedBase += Path.DirectorySeparatorChar;

        if (!normalizedFull.StartsWith(normalizedBase, StringComparison.OrdinalIgnoreCase))
            throw new IOException(
                $"Path traversal detected: '{fullPath}' escapes target directory '{baseDirectory}'");

        return normalizedFull;
    }
}
