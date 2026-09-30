using System.IO;
using System.Text.Json;

namespace StardewLauncher.Core.Mods;

/// <summary>
/// 从 Mod 自己的文件里认出中文名，供列表显示与搜索用。纯离线，不改 Mod 的任何文件。
///
/// <para>
/// 识别顺序：manifest.json 的 Name 自带中文 → 文件夹名自带中文（国内玩家常直接把文件夹改成中文）
/// → Mod 自带的 i18n 中文翻译文件里形如 Name / ModName 的键。都认不出来就返回 null，
/// 调用方继续用 manifest 里的原名。
/// </para>
/// </summary>
public static class ModNameLocalizer
{
    /// <summary>i18n 翻译文件里可能存放 Mod 名字的键名（小写比较）。</summary>
    private static readonly string[] NameKeys =
        ["name", "modname", "mod.name", "displayname", "title"];

    /// <summary>中文翻译文件名（不含扩展名）的候选，按优先级排列。</summary>
    private static readonly string[] ChineseLocales = ["zh", "zh-CN", "zh-Hans", "zh-Hant", "zh-TW"];

    /// <summary>解析 i18n 文件时的宽容选项，跟 manifest 的解析保持一致。</summary>
    private static readonly JsonDocumentOptions Options = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    /// <summary>认这个 Mod 的中文名；认不出来返回 null。</summary>
    public static string? TryResolve(ModEntry mod)
    {
        if (mod is null) return null;

        // 1. manifest 里的名字本身就是中文，不用再猜
        var manifestName = mod.Manifest?.Name;
        if (HasChinese(manifestName)) return manifestName!.Trim();

        // 2. 文件夹名就是中文
        if (HasChinese(mod.RawFolderName)) return mod.RawFolderName.Trim();

        // 3. Mod 自带的 i18n 中文翻译
        return TryFromI18n(mod.FolderPath);
    }

    /// <summary>文本里是否含中日韩表意文字。用于判断「这串字要不要当成中文名」。</summary>
    public static bool HasChinese(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        foreach (var ch in text)
        {
            if (ch is >= '\u4E00' and <= '\u9FFF') return true; // 基本区
            if (ch is >= '\u3400' and <= '\u4DBF') return true; // 扩展 A
            if (ch is >= '\uF900' and <= '\uFAFF') return true; // 兼容表意文字
        }

        return false;
    }

    /// <summary>
    /// 在 Mod 目录里找中文翻译文件。只看 &lt;Mod&gt;/i18n 与 &lt;Mod&gt;/*/i18n 两层：
    /// 再往下就进到资源目录了，翻一遍既慢又不可能有 Mod 名。
    /// </summary>
    private static string? TryFromI18n(string modFolder)
    {
        if (string.IsNullOrWhiteSpace(modFolder) || !Directory.Exists(modFolder)) return null;

        var candidates = new List<string>();

        var direct = Path.Combine(modFolder, "i18n");
        if (Directory.Exists(direct)) candidates.Add(direct);

        try
        {
            foreach (var child in Directory.GetDirectories(modFolder))
            {
                var nested = Path.Combine(child, "i18n");
                if (Directory.Exists(nested)) candidates.Add(nested);
            }
        }
        catch
        {
            // 目录读不了就当没有 i18n，不影响扫描
        }

        foreach (var directory in candidates)
        {
            foreach (var locale in ChineseLocales)
            {
                var file = Path.Combine(directory, locale + ".json");
                if (!File.Exists(file)) continue;

                var name = ReadNameFrom(file);
                if (name is not null) return name;
            }
        }

        return null;
    }

    private static string? ReadNameFrom(string file)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file), Options);

            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (Array.IndexOf(NameKeys, property.Name.ToLowerInvariant()) < 0) continue;
                if (property.Value.ValueKind != JsonValueKind.String) continue;

                var value = property.Value.GetString();
                if (HasChinese(value)) return value!.Trim();
            }
        }
        catch
        {
            // 翻译文件格式千奇百怪，读不动就跳过，不打扰用户
        }

        return null;
    }
}