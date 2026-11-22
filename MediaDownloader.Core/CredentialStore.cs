using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MediaDownloader.Core;

/// <summary>
/// 凭证存储。用 Windows DPAPI（绑定当前用户账户）加密，不做明文落盘。
/// </summary>
public static class CredentialStore
{
    public static void Save(IReadOnlyDictionary<string, string> cookies)
    {
        AppPaths.Ensure();
        var json = JsonSerializer.Serialize(cookies);
        var plain = Encoding.UTF8.GetBytes(json);
        var secret = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(AppPaths.CredentialFile, secret);
    }

    public static Dictionary<string, string>? Load()
    {
        try
        {
            if (!File.Exists(AppPaths.CredentialFile)) return null;
            var secret = File.ReadAllBytes(AppPaths.CredentialFile);
            var plain = ProtectedData.Unprotect(secret, null, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(Encoding.UTF8.GetString(plain));
        }
        catch
        {
            return null;
        }
    }

    public static void Clear()
    {
        try { if (File.Exists(AppPaths.CredentialFile)) File.Delete(AppPaths.CredentialFile); } catch { /* 忽略 */ }
    }
}
