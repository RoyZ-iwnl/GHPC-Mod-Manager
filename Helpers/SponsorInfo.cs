using System.Reflection;

namespace GHPC_Mod_Manager.Helpers;

/// <summary>
/// 赞助者冠名信息（硬编码常量）。
/// SponsorTitleSuffix：冠名版本名，追加到窗口标题（如 "某赞助者 Version"）。
/// SponsorName：赞助者名，显示在设置页关于区 "v1.3.9 XXX Sponsor by XXXX"。
/// 二者均为空时，标题不追加后缀、关于区不显示冠名元素。
/// </summary>
public static class SponsorInfo
{
    /// <summary>
    /// 冠名版本名（追加到窗口标题后缀，与关于区 XXX 同源）
    /// </summary>
    public const string SponsorTitleSuffix = "Nailoong";

    /// <summary>
    /// 赞助者名（显示在关于区 Sponsor by 后）
    /// </summary>
    public const string SponsorName = "";

    /// <summary>
    /// 是否有冠名（任一非空即为 true）
    /// </summary>
    public static bool HasSponsor =>
        !string.IsNullOrWhiteSpace(SponsorTitleSuffix) || !string.IsNullOrWhiteSpace(SponsorName);

    /// <summary>
    /// 短版本号（不含 git hash），如 "1.3.9"
    /// </summary>
    public static string ShortVersion
    {
        get
        {
            try
            {
                var info = Assembly.GetExecutingAssembly()
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                if (string.IsNullOrEmpty(info)) return string.Empty;
                // 格式 "1.3.9+16534f4..."，取 + 前
                var idx = info.IndexOf('+');
                return idx > 0 ? info[..idx] : info;
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
