namespace GHPC_Mod_Manager.Services;

public interface IPreviousInstallationService
{
    /// <summary>
    /// 保存当前程序路径到注册表
    /// </summary>
    void SaveCurrentAppPath();

    /// <summary>
    /// 获取注册表中存储的旧程序路径
    /// </summary>
    /// <returns>旧路径，不存在则返回 null</returns>
    string? GetPreviousAppPath();

    /// <summary>
    /// 检查是否存在旧安装（路径与当前不同）
    /// </summary>
    /// <returns>true 表示存在旧安装</returns>
    bool HasPreviousInstallation();

    /// <summary>
    /// 获取旧安装的exe文件完整路径
    /// </summary>
    /// <returns>exe路径，不存在则返回 null</returns>
    string? GetPreviousAppExePath();

    /// <summary>
    /// 获取旧安装的ProductVersion
    /// </summary>
    /// <returns>版本字符串，无法获取则返回 null</returns>
    string? GetPreviousAppVersion();
}