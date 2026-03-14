using System.Security.Cryptography;
using System.Text;

namespace GHPC_Mod_Manager.Services;

/// <summary>
/// 使用 Windows DPAPI 加密存储敏感数据
/// </summary>
public interface ISecureStorageService
{
    string Protect(string plainText);
    (string decrypted, bool wasMigrated) UnprotectWithMigrationDetection(string encryptedText);
}

public class SecureStorageService : ISecureStorageService
{
    // 加密明文，返回 Base64 字符串
    public string Protect(string plainText)
    {
        if (string.IsNullOrEmpty(plainText))
            return string.Empty;

        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var encryptedBytes = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encryptedBytes);
    }

    // 解密并检测是否从明文迁移
    public (string decrypted, bool wasMigrated) UnprotectWithMigrationDetection(string encryptedText)
    {
        if (string.IsNullOrEmpty(encryptedText))
            return (string.Empty, false);

        try
        {
            var encryptedBytes = Convert.FromBase64String(encryptedText);
            var plainBytes = ProtectedData.Unprotect(encryptedBytes, null, DataProtectionScope.CurrentUser);
            return (Encoding.UTF8.GetString(plainBytes), false);
        }
        catch
        {
            // 解密失败，是明文（旧版兼容）
            return (encryptedText, true);
        }
    }
}
