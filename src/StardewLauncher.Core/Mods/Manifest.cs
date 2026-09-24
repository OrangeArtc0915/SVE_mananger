using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StardewLauncher.Core.Mods;

/// <summary>manifest.json 里的一条依赖声明。</summary>
public sealed class ManifestDependency
{
    /// <summary>依赖目标的唯一标识。对应 JSON 中的 "UniqueID"（真实文件里也有写成 "UniqueId" 的）。</summary>
    [JsonPropertyName("UniqueID")]
    public string UniqueId { get; set; } = "";

    [JsonPropertyName("MinimumVersion")]
    public string? MinimumVersion { get; set; }

    /// <summary>是否必需。字段缺省时按 true 处理。</summary>
    [JsonPropertyName("IsRequired")]
    public bool IsRequired { get; set; } = true;
}

/// <summary>内容包指向的目标框架 Mod。</summary>
public sealed class ManifestContentPackFor
{
    [JsonPropertyName("UniqueID")]
    public string UniqueId { get; set; } = "";

    [JsonPropertyName("MinimumVersion")]
    public string? MinimumVersion { get; set; }
}

/// <summary>
/// SMAPI 的 manifest.json 模型。
/// 注意：真实文件里的唯一标识键名是全大写 ID（"UniqueID"），解析时对大小写不敏感。
/// </summary>
public sealed class Manifest
{
    [JsonPropertyName("Name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("Author")]
    public string Author { get; set; } = "";

    [JsonPropertyName("Version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("Description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("UniqueID")]
    public string UniqueId { get; set; } = "";

    [JsonPropertyName("EntryDll")]
    public string? EntryDll { get; set; }

    /// <summary>更新键。真实文件里元素格式很杂，统一用柔性转换器转成字符串。</summary>
    [JsonPropertyName("UpdateKeys")]
    [JsonConverter(typeof(FlexibleStringArrayConverter))]
    public string[] UpdateKeys { get; set; } = [];

    [JsonPropertyName("Dependencies")]
    public List<ManifestDependency> Dependencies { get; set; } = [];

    [JsonPropertyName("ContentPackFor")]
    public ManifestContentPackFor? ContentPackFor { get; set; }

    [JsonPropertyName("MinimumApiVersion")]
    public string? MinimumApiVersion { get; set; }

    [JsonPropertyName("MinimumGameVersion")]
    public string? MinimumGameVersion { get; set; }
}

/// <summary>
/// UpdateKeys 的柔性解析：既接受字符串数组，也接受单个字符串、单个数字、数组里混着数字与裸名字。
/// 统一转成去除首尾空白的字符串数组，并丢弃空项。
/// </summary>
internal sealed class FlexibleStringArrayConverter : JsonConverter<string[]>
{
    public override string[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return [];

            case JsonTokenType.String:
                return Clean(reader.GetString());

            case JsonTokenType.Number:
                return Clean(NumberText(ref reader));

            case JsonTokenType.StartArray:
                var items = new List<string>();
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndArray) break;

                    switch (reader.TokenType)
                    {
                        case JsonTokenType.String:
                            AddIfNotBlank(items, reader.GetString());
                            break;
                        case JsonTokenType.Number:
                            AddIfNotBlank(items, NumberText(ref reader));
                            break;
                        case JsonTokenType.True:
                            items.Add("true");
                            break;
                        case JsonTokenType.False:
                            items.Add("false");
                            break;
                        case JsonTokenType.Null:
                            break;
                        default:
                            // 元素是对象/嵌套数组这种畸形写法，直接跳过
                            reader.Skip();
                            break;
                    }
                }
                return [.. items];

            default:
                reader.Skip();
                return [];
        }
    }

    public override void Write(Utf8JsonWriter writer, string[] value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var item in value ?? []) writer.WriteStringValue(item);
        writer.WriteEndArray();
    }

    private static string[] Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? [] : [value.Trim()];

    /// <summary>把数字 token 转成不依赖区域设置的文本。</summary>
    private static string NumberText(ref Utf8JsonReader reader)
        => reader.TryGetInt64(out var integer)
            ? integer.ToString(CultureInfo.InvariantCulture)
            : reader.GetDouble().ToString("R", CultureInfo.InvariantCulture);

    private static void AddIfNotBlank(List<string> items, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) items.Add(value.Trim());
    }
}
