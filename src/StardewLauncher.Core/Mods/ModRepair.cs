using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;
using StardewLauncher.Core.Logging;

namespace StardewLauncher.Core.Mods;

/// <summary>能自动修的问题种类。</summary>
public enum ModRepairKind
{
    /// <summary>manifest.json 读不出来，Mod 卡片上是红字。</summary>
    ManifestSyntax,

    /// <summary>Mod 外面多套了一层文件夹，SMAPI 那一层看不到。</summary>
    FolderNesting,

    /// <summary>多个已启用的 Mod 抢同一个 UniqueID。</summary>
    DuplicateUniqueId
}

/// <summary>
/// 一条待修复的问题。<see cref="Path"/> 是要动手的目录；<see cref="Targets"/> 是连带要处理的目录
/// （重复 ID 用它记「该禁用的那几份」）。
/// </summary>
public sealed record ModRepairFinding(ModRepairKind Kind, string Title, string Path, string Detail,
    IReadOnlyList<string> Targets)
{
    public string KindText => Kind switch
    {
        ModRepairKind.ManifestSyntax => "manifest 语法",
        ModRepairKind.FolderNesting => "目录层级",
        _ => "重复 ID"
    };
}

/// <summary>一次自动修复的结果。Lines 是逐条的明细，直接拿去给用户看。</summary>
public sealed record ModRepairReport(int Fixed, int Skipped, int Failed, IReadOnlyList<string> Lines)
{
    public bool Changed => Fixed > 0;
}

/// <summary>
/// 一键修复 Mod 上那些「不用问作者、自己就能改好」的毛病：
///
/// <para>
/// 1. manifest.json 语法 —— 去 BOM、去注释、去尾逗号、去多余控制字符，缺 UniqueID 就补一个；
/// 动文件之前先把原文另存成 manifest.json.bak-&lt;时间戳&gt;。<br/>
/// 2. 多套一层文件夹 —— 外层没有 manifest.json、只含一个内含 manifest.json 的子目录时，把子目录提上来。<br/>
/// 3. 重复 UniqueID —— 同一 ID 只留一个，其余的禁用（改名加前缀点，不删文件）。
/// </para>
///
/// <para>
/// 只做确定能改好的事情：改不动的记进 Skipped / Failed，绝不动 Mod 的内容文件。
/// </para>
/// </summary>
public static class ModRepair
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    /// <summary>扫描出当前所有可以自动修复的问题。</summary>
    public static IReadOnlyList<ModRepairFinding> Analyze(IReadOnlyList<ModEntry> mods, string? modsDirectory)
    {
        var findings = new List<ModRepairFinding>();

        AddBrokenManifests(findings, mods);
        AddNestedFolders(findings, modsDirectory);
        AddDuplicateIds(findings, mods);

        return findings;
    }

    /// <summary>按 findings 逐条修复。传入的 findings 就是 Analyze 的结果，用户确认过再调这里。</summary>
    public static ModRepairReport Fix(IReadOnlyList<ModRepairFinding> findings, string? modsDirectory,
        IProgress<string>? progress = null, CancellationToken token = default)
    {
        var lines = new List<string>();
        int ok = 0, skipped = 0, failed = 0;

        foreach (var finding in findings)
        {
            token.ThrowIfCancellationRequested();
            progress?.Report($"正在修复：{finding.Title}");

            switch (finding.Kind)
            {
                case ModRepairKind.ManifestSyntax:
                    FixManifest(finding, lines, ref ok, ref failed);
                    break;

                case ModRepairKind.FolderNesting:
                    FixNesting(finding, modsDirectory, lines, ref ok, ref skipped, ref failed);
                    break;

                case ModRepairKind.DuplicateUniqueId:
                    FixDuplicates(finding, lines, ref ok, ref skipped, ref failed);
                    break;
            }
        }

        progress?.Report(string.Empty);
        Log.Info($"Mod 自动修复结束：修好 {ok} 项，未处理 {skipped} 项，失败 {failed} 项");

        return new ModRepairReport(ok, skipped, failed, lines);
    }

    // ————— 分析 —————

    /// <summary>解析失败的 Mod。目录都读不了（连 manifest.json 文件都找不到）的没法就地修，跳过。</summary>
    private static void AddBrokenManifests(List<ModRepairFinding> findings, IReadOnlyList<ModEntry> mods)
    {
        foreach (var mod in mods.Where(item => item.State == ModState.Invalid))
        {
            if (!File.Exists(Path.Combine(mod.FolderPath, "manifest.json"))) continue;

            findings.Add(new ModRepairFinding(ModRepairKind.ManifestSyntax,
                $"manifest.json 读不出来：{mod.RawFolderName}",
                mod.FolderPath,
                mod.ParseError ?? "原因未知",
                []));
        }
    }

    /// <summary>
    /// 外层目录没有 manifest.json、只装着一个内含 manifest.json 的子目录 —— 典型的「多套了一层」。
    /// 空壳目录、只有一个子目录但子目录里也没有 manifest 的，都算不出确定结论，不动。
    /// </summary>
    private static void AddNestedFolders(List<ModRepairFinding> findings, string? modsDirectory)
    {
        if (string.IsNullOrWhiteSpace(modsDirectory) || !Directory.Exists(modsDirectory)) return;

        string[] directories;
        try
        {
            directories = Directory.GetDirectories(modsDirectory);
        }
        catch (Exception ex)
        {
            Log.Warn($"列 Mods 目录失败：{modsDirectory}（{ex.Message}）");
            return;
        }

        foreach (var directory in directories)
        {
            if (File.Exists(Path.Combine(directory, "manifest.json"))) continue;

            string[] children;
            string[] files;
            try
            {
                children = Directory.GetDirectories(directory);
                files = Directory.GetFiles(directory);
            }
            catch
            {
                continue;
            }

            if (children.Length != 1 || files.Length != 0) continue;
            if (!File.Exists(Path.Combine(children[0], "manifest.json"))) continue;

            findings.Add(new ModRepairFinding(ModRepairKind.FolderNesting,
                $"多套了一层文件夹：{Path.GetFileName(directory)}",
                directory,
                $"真正的 Mod 在里面的「{Path.GetFileName(children[0])}」，SMAPI 看不到外层这一格",
                []));
        }
    }

    /// <summary>
    /// 重复 UniqueID。只算已启用的（一份启用一份禁用是有意留的旧版本），
    /// 按文件夹名排序留第一个，其余的建议禁用。
    /// </summary>
    private static void AddDuplicateIds(List<ModRepairFinding> findings, IReadOnlyList<ModEntry> mods)
    {
        var groups = mods
            .Where(mod => mod.IsEnabled && !string.IsNullOrWhiteSpace(mod.UniqueId))
            .GroupBy(mod => mod.UniqueId.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1);

        foreach (var group in groups)
        {
            var ordered = group.OrderBy(mod => mod.RawFolderName, StringComparer.OrdinalIgnoreCase).ToList();
            var keep = ordered[0];
            var rest = ordered.Skip(1).ToList();

            findings.Add(new ModRepairFinding(ModRepairKind.DuplicateUniqueId,
                $"{ordered.Count} 个 Mod 共用 ID：{group.Key}",
                keep.FolderPath,
                $"保留「{keep.RawFolderName}」，禁用「{string.Join("」「", rest.Select(mod => mod.RawFolderName))}」",
                [.. rest.Select(mod => mod.FolderPath)]));
        }
    }

    // ————— 修复 —————

    private static void FixManifest(ModRepairFinding finding, List<string> lines, ref int ok, ref int failed)
    {
        var name = Path.GetFileName(finding.Path);
        var file = Path.Combine(finding.Path, "manifest.json");

        string original;
        try
        {
            original = File.ReadAllText(file);
        }
        catch (Exception ex)
        {
            failed++;
            lines.Add($"✗ {name}：manifest.json 读取失败（{ex.Message}）");
            return;
        }

        var repaired = TryRepairJson(original);
        if (repaired is null)
        {
            failed++;
            lines.Add($"✗ {name}：语法问题超出自动修复范围，需要手动改 manifest.json");
            return;
        }

        try
        {
            var backup = $"{file}.bak-{DateTime.Now:yyyyMMddHHmmss}";
            File.WriteAllText(backup, original, new UTF8Encoding(false));
            File.WriteAllText(file, repaired, new UTF8Encoding(false));

            ok++;
            lines.Add($"✓ {name}：已修好 manifest.json（原件备份为 {Path.GetFileName(backup)}）");
        }
        catch (Exception ex)
        {
            failed++;
            lines.Add($"✗ {name}：写入失败（{ex.Message}）");
        }
    }

    private static void FixNesting(ModRepairFinding finding, string? modsDirectory, List<string> lines,
        ref int ok, ref int skipped, ref int failed)
    {
        var name = Path.GetFileName(finding.Path);

        if (string.IsNullOrWhiteSpace(modsDirectory))
        {
            skipped++;
            lines.Add($"· {name}：没有可用的 Mods 目录，跳过");
            return;
        }

        string[] children;
        try
        {
            children = Directory.GetDirectories(finding.Path);
        }
        catch (Exception ex)
        {
            failed++;
            lines.Add($"✗ {name}：读目录失败（{ex.Message}）");
            return;
        }

        if (children.Length != 1)
        {
            skipped++;
            lines.Add($"· {name}：结构变了，跳过");
            return;
        }

        var inner = children[0];

        // 外层是禁用的（名字带点），提上来的目录也要带上点，别把用户的启停状态改掉
        var targetName = Path.GetFileName(inner);
        if (name.StartsWith('.')) targetName = "." + targetName.TrimStart('.');

        var target = Path.Combine(modsDirectory, targetName);

        if (Directory.Exists(target) || File.Exists(target))
        {
            skipped++;
            lines.Add($"· {name}：Mods 下已经有「{targetName}」了，跳过（需要手动确认留哪一份）");
            return;
        }

        try
        {
            Directory.Move(inner, target);

            // 外层这时应该是空壳，顺手删掉；万一还有残留就留着，不硬删
            if (Directory.Exists(finding.Path) && Directory.GetFileSystemEntries(finding.Path).Length == 0)
                Directory.Delete(finding.Path);

            ok++;
            lines.Add($"✓ {name}：已把「{Path.GetFileName(inner)}」提到 Mods 下");
        }
        catch (Exception ex)
        {
            failed++;
            lines.Add($"✗ {name}：上移失败（{ex.Message}）");
        }
    }

    private static void FixDuplicates(ModRepairFinding finding, List<string> lines, ref int ok, ref int skipped,
        ref int failed)
    {
        var keepName = Path.GetFileName(finding.Path);
        var disabled = new List<string>();

        foreach (var path in finding.Targets)
        {
            var name = Path.GetFileName(path);

            if (name.StartsWith('.'))
            {
                skipped++;
                lines.Add($"· {name}：已经是禁用状态，跳过");
                continue;
            }

            if (!Directory.Exists(path))
            {
                skipped++;
                lines.Add($"· {name}：目录不在了，跳过");
                continue;
            }

            try
            {
                Directory.Move(path, Path.Combine(Path.GetDirectoryName(path)!, "." + name));
                disabled.Add(name);
                ok++;
            }
            catch (Exception ex)
            {
                failed++;
                lines.Add($"✗ {name}：禁用失败（{ex.Message}）");
            }
        }

        if (disabled.Count > 0)
            lines.Add($"✓ {finding.Title}：保留「{keepName}」，已禁用「{string.Join("」「", disabled)}」");
    }

    // ————— manifest 语法修复 —————

    /// <summary>
    /// 把一段写坏的 manifest JSON 尽量修回合法 JSON。修不回来返回 null（调用方记为失败，不写文件）。
    /// </summary>
    private static string? TryRepairJson(string raw)
    {
        // 只看 JSON 本身合不合法：缺 UniqueID 也是「要修的问题」之一，
        // 所以不能拿 ManifestParser 的结果来判断，它会把「缺 UniqueID」和「语法坏了」混成同一个 null
        var cleaned = Clean(raw);

        JsonObject? obj;
        try
        {
            obj = JsonNode.Parse(cleaned) as JsonObject;
        }
        catch (Exception ex)
        {
            Log.Warn($"manifest.json 清理后仍不是合法 JSON：{ex.Message}");
            return null;
        }

        if (obj is null) return null;

        if (NeedsUniqueId(obj)) obj["UniqueID"] = "Launcher.AutoFix." + Guid.NewGuid().ToString("N")[..8];

        var repaired = obj.ToJsonString(WriteOptions);

        // 收尾自检：补完还是读不出来，说明改坏了，宁可不写
        return ManifestParser.ParseJson(repaired, out _) is null ? null : repaired;
    }

    /// <summary>没有 UniqueID 键，或者键在但值是空的。空键会被删掉，好补一个规范的。</summary>
    private static bool NeedsUniqueId(JsonObject obj)
    {
        var key = obj
            .Select(pair => pair.Key)
            .FirstOrDefault(name => string.Equals(name, "UniqueID", StringComparison.OrdinalIgnoreCase));

        if (key is null) return true;
        if (!string.IsNullOrWhiteSpace(obj[key]?.ToString())) return false;

        obj.Remove(key);
        return true;
    }

    /// <summary>
    /// 去掉 BOM、注释、尾逗号，以及字符串外多余的控制字符。全部按字符扫，字符串里的内容一律不动。
    /// </summary>
    private static string Clean(string raw)
    {
        var builder = new StringBuilder(raw.Length);
        var inString = false;
        var escaped = false;

        for (var i = 0; i < raw.Length; i++)
        {
            var ch = raw[i];

            if (ch == '\uFEFF') continue;

            if (inString)
            {
                builder.Append(ch);

                if (escaped) escaped = false;
                else if (ch == '\\') escaped = true;
                else if (ch == '"') inString = false;

                continue;
            }

            // 行注释：整行丢掉，留一个换行维持行号感
            if (ch == '/' && i + 1 < raw.Length && raw[i + 1] == '/')
            {
                while (i < raw.Length && raw[i] != '\n') i++;
                builder.Append('\n');
                continue;
            }

            // 块注释
            if (ch == '/' && i + 1 < raw.Length && raw[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < raw.Length && !(raw[i] == '*' && raw[i + 1] == '/')) i++;
                i++;
                continue;
            }

            if (ch == '"')
            {
                inString = true;
                builder.Append(ch);
                continue;
            }

            // 字符串外的控制字符：换行与制表符留着，其余（垂直制表符之类）去掉
            if (ch < 0x20 && ch is not ('\n' or '\r' or '\t')) continue;

            builder.Append(ch);
        }

        return RemoveTrailingCommas(builder.ToString());
    }

    /// <summary>删掉 "}" / "]" 前面多出来的那个逗号。</summary>
    private static string RemoveTrailingCommas(string json)
    {
        var chars = new List<char>(json.Length);
        var inString = false;
        var escaped = false;

        foreach (var ch in json)
        {
            if (inString)
            {
                chars.Add(ch);

                if (escaped) escaped = false;
                else if (ch == '\\') escaped = true;
                else if (ch == '"') inString = false;

                continue;
            }

            if (ch == '"')
            {
                inString = true;
                chars.Add(ch);
                continue;
            }

            if (ch is '}' or ']')
            {
                // 往回跳过空白，落在逗号上就连同它后面的空白一起删掉
                var end = chars.Count;
                while (end > 0 && char.IsWhiteSpace(chars[end - 1])) end--;

                if (end > 0 && chars[end - 1] == ',') chars.RemoveRange(end - 1, chars.Count - (end - 1));
            }

            chars.Add(ch);
        }

        return new string([.. chars]);
    }
}