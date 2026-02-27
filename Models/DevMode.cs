namespace GHPC_Mod_Manager.Models;

/// <summary>
/// 全局开发模式状态，通过 -dev 启动参数解锁
/// </summary>
public static class DevMode
{
    public static bool IsEnabled { get; set; } = false;

    // dev 模式下的 URL override（不持久化，仅内存）
    public static string? MainConfigUrlOverride { get; set; }
}
