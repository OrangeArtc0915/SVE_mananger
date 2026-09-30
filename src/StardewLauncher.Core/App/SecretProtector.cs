using System.Security.Cryptography;
using System.Text;
using StardewLauncher.Core.Logging;

namespace StardewLauncher.Core.App;

/// <summary>
/// 密钥类配置的加解密。用 Windows DPAPI（CurrentUser 作用域）：密文只能被同一台机器上的
/// 同一个 Windows 用户解开，用户不用额外记一个主密码，也不引入第三方加密库。
/// 密文带前缀，读到不带前缀的值就说明是旧版本留下的明文，由调用方负责回写密文。
/// </summary>
public static class SecretProtector
{
    /// <summary>密文前缀。也用于把「已加密」与「旧版明文」区分开。</summary>
    private const string Prefix = "dpapi:v1:";

    /// <summary>值是否已经是加过密的密文。</summary>
    public static bool IsProtected(string? value)
        => !string.IsNullOrEmpty(value) && value.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>把明文加密成可落盘的文本。空值原样返回空串。</summary>
    public static string Protect(string? plain)
        => string.IsNullOrEmpty(plain)
            ? string.Empty
            : Prefix + Convert.ToBase64String(
                ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser));

    /// <summary>
    /// 把落盘文本还原成明文。旧版明文原样返回（交给迁移逻辑回写）；
    /// 解不开（换了机器或换了 Windows 用户）时返回空串并记日志，不让启动崩掉。
    /// </summary>
    public static string Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return string.Empty;

        if (!IsProtected(stored)) return stored;

        try
        {
            var bytes = Convert.FromBase64String(stored[Prefix.Length..]);
            var plain = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex)
        {
            Log.Error("密钥解密失败，按未配置处理（换了机器或 Windows 用户后需要重新填写）", ex);
            return string.Empty;
        }
    }
}