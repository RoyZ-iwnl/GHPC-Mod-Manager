namespace GHPC_Mod_Manager.Models;

/// <summary>
/// 全局开发模式状态，通过 -dev 启动参数解锁
/// 支持以下格式：
///   -dev                        仅启用开发模式
///   -dev:"path"                 冒号分隔路径（推荐，快捷方式兼容）
///   -dev="path"                 等号分隔路径（快捷方式兼容）
///   -dev "path"                 空格分隔路径（命令行使用，需双引号）
/// </summary>
public static class DevMode
{
    public static bool IsEnabled { get; set; } = false;

    // dev 模式下的主配置 URL/路径 override（不持久化，仅内存）
    // 支持本地文件路径或 HTTP URL
    public static string? MainConfigUrlOverride { get; set; }
}
