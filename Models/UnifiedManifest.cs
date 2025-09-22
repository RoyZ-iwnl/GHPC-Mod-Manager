using System.Security.Cryptography;

namespace GHPC_Mod_Manager.Models;

// 统一的文件信息模型
public class FileInfoModel
{
    public string RelativePath { get; set; } = string.Empty;
    public string Sha256Hash { get; set; } = string.Empty;
    public long Size { get; set; }
    public DateTime CreatedDate { get; set; }
    public FileCategory Category { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = new();
}

public enum FileCategory
{
    ModBinary,          // Mod DLL文件
    ModConfig,          // Mod配置文件  
    ModResource,        // Mod资源文件
    TranslationCore,    // XUnity核心文件
    TranslationData,    // 翻译数据文件
    TranslationConfig,  // 翻译配置文件
    Other
}

// 统一的安装清单
public class UnifiedInstallManifest
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty; // "mod" 或 "translation"
    public string Version { get; set; } = string.Empty;
    public DateTime InstallDate { get; set; }
    public DateTime LastModified { get; set; }
    public List<FileInfoModel> Files { get; set; } = new();
    public Dictionary<string, string> Properties { get; set; } = new();
    public InstallSource Source { get; set; } = new();
}

public class InstallSource
{
    public string Method { get; set; } = string.Empty; // "github", "local", "script"
    public string Url { get; set; } = string.Empty;
    public string CommitHash { get; set; } = string.Empty;
    public Dictionary<string, string> Parameters { get; set; } = new();
}

// 统一的备份清单
public class UnifiedBackupManifest
{
    public string SourceId { get; set; } = string.Empty;
    public string BackupId { get; set; } = string.Empty;
    public DateTime BackupDate { get; set; }
    public BackupReason Reason { get; set; }
    public List<BackupFileInfo> Files { get; set; } = new();
}

public class BackupFileInfo
{
    public string OriginalPath { get; set; } = string.Empty;
    public string BackupPath { get; set; } = string.Empty;
    public string Sha256Hash { get; set; } = string.Empty;
    public long Size { get; set; }
}

public enum BackupReason
{
    Disable,        // 临时禁用
    Update,         // 版本更新
    Uninstall,      // 卸载前备份
    Manual          // 手动备份
}