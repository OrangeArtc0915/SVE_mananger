using System.Text.Json;
using System.Text.Json.Serialization;

namespace StardewLauncher.Core.App;

/// <summary>
/// 给字符串属性加一道加解密：读出来是明文，写回去是密文。
/// 用法是在密钥属性上标 <c>[JsonConverter(typeof(SecretJsonConverter))]</c>，
/// 这样加密发生在 JSON 边界上，任何调用 <see cref="SettingsStore.Save"/> 的入口都自动生效。
/// </summary>
public sealed class SecretJsonConverter : JsonConverter<string>
{
    /// <summary>本次读取过程中是否读到过旧版明文，供 <see cref="SettingsStore"/> 决定要不要回写迁移。</summary>
    public static bool SawLegacyPlaintext { get; private set; }

    /// <summary>开始一轮读取前清掉上一次的标记。</summary>
    public static void ResetTracking() => SawLegacyPlaintext = false;

    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var stored = reader.GetString() ?? string.Empty;

        if (!string.IsNullOrEmpty(stored) && !SecretProtector.IsProtected(stored)) SawLegacyPlaintext = true;

        return SecretProtector.Unprotect(stored);
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        => writer.WriteStringValue(SecretProtector.Protect(value));
}