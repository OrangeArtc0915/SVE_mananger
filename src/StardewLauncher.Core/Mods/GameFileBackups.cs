using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using StardewLauncher.Core.App;
using StardewLauncher.Core.IO;
using StardewLauncher.Core.Logging;

namespace StardewLauncher.Core.Mods;

/// <summary>索引里的一条：某个游戏原版文件被覆盖前留下的备份。</summary>
public sealed record GameFileBackupRecord(
    string GameDirectory,
    string RelativePath,
    string BackupPath,
    DateTime CreatedAt,
    long SizeBytes,
    string Origin);

/// <summary>索引文件的内容。</summary>
internal sealed class GameFileBackupIndexFile
{
    public int Version { get; set; } = 1;

    public List<GameFileBackupRecord> Entries { get; set; } = [];
}

/// <summary>待还原的一项。</summary>
public sealed record GameFileRestoreItem(
    string GameDirectory,
    string RelativePath,
    string OriginalPath,
    string BackupPath,
    DateTime CreatedAt,
    long SizeBytes,
    bool Registered,
    string Origin)
{
    public string FileName => Path.GetFileName(RelativePath);

    public string FolderText
        => Path.GetDirectoryName(RelativePath) is { Length: > 0 } folder ? folder : "游戏根目录";

    public string CreatedText => CreatedAt == default ? "时间未知" : CreatedAt.ToString("yyyy-MM-dd HH:mm");

    public string SizeText => SizeBytes switch
    {
        >= 1024 * 1024 => $"{SizeBytes / 1024d / 1024:0.#} MB",
        >= 1024 => $"{SizeBytes / 1024d:0.#} KB",
        _ => $"{SizeBytes} B"
    };

    /// <summary>这份备份是不是百分百能确定是原版。</summary>
    public string OriginText => Registered
        ? string.IsNullOrWhiteSpace(Origin) ? "已登记" : $"已登记 · {Origin}"
        : "未登记 · 可能是原版，也可能是上一个 Mod 的版本";

    public bool BackupMissing => !File.Exists(BackupPath);
}

/// <summary>还原的结果。</summary>
public sealed record GameFileRestoreResult(bool Ok, string Message);

/// <summary>
/// 游戏原版文件的覆盖前备份：索引、列表与还原。
///
/// <para>
/// 备份文件本身**仍就地放在原文件旁边**（<c>{文件}.bak-时间戳</c>，由
/// <see cref="PlanInstaller"/> 在覆盖前生成），这里只在数据目录里多记一份索引，
/// 记下「哪个原版文件的备份是谁在什么时候留下的」。有索引才敢说某份备份是原版。
/// </para>
///
/// <para>
/// 索引之前就存在的那些 <c>.bak-</c> 会在扫描时一并列出来，但标记为「未登记」——
/// 它们可能是原版，也可能是上一个 Mod 覆盖后的版本，工具不替用户下结论。
/// </para>
/// </summary>
public static class GameFileBackups
{
    /// <summary>覆盖前备份的命名标记，与 <see cref="PlanInstaller"/> 保持一致。</summary>
    public const string Marker = ".bak-";

    /// <summary>还原前另存当前文件用的标记。</summary>
    private const string SnapshotMarker = ".before-restore-";

    /// <summary>索引最多留多少条，超出后丢最旧的，避免无限增长。</summary>
    private const int MaxEntries = 800;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>索引所在目录。</summary>
    public static string Root => Path.Combine(Paths.Data, "game-backups");

    public static string IndexPath => Path.Combine(Root, "index.json");

    /// <summary>登记一次覆盖前备份。由 <see cref="PlanInstaller"/> 在执行覆盖时调用，失败不影响安装。</summary>
    internal static void Register(string gameDirectory, string originalFile, string backupPath, string origin)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory) || string.IsNullOrWhiteSpace(originalFile)) return;

        try
        {
            var root = Normalize(gameDirectory);

            var file = ReadIndex();
            file.Entries.Add(new GameFileBackupRecord(
                root,
                Path.GetRelativePath(root, originalFile),
                backupPath,
                DateTime.Now,
                LengthOf(backupPath),
                origin ?? string.Empty));

            if (file.Entries.Count > MaxEntries)
                file.Entries = file.Entries.OrderByDescending(entry => entry.CreatedAt).Take(MaxEntries).ToList();

            WriteIndex(file);
            Log.Info($"已登记覆盖前备份：{Path.GetRelativePath(root, originalFile)}（来源：{origin}）");
        }
        catch (Exception ex)
        {
            Log.Warn($"登记覆盖前备份失败（不影响安装）：{ex.Message}");
        }
    }

    /// <summary>
    /// 列出这个游戏目录下可以还原的原版文件：先取索引里登记过的，
    /// 再补上磁盘上存在、但索引里没有的 <c>.bak-</c>（Mods 目录下的不算，那些是旧版 Mod）。
    /// </summary>
    public static IReadOnlyList<GameFileRestoreItem> List(string gameDirectory, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory) || !Directory.Exists(gameDirectory)) return [];

        var root = Normalize(gameDirectory);
        var items = new Dictionary<string, GameFileRestoreItem>(StringComparer.OrdinalIgnoreCase);

        foreach (var record in ReadIndex().Entries)
        {
            token.ThrowIfCancellationRequested();

            if (!SameDirectory(record.GameDirectory, root)) continue;

            // 备份文件被删掉的条目不再列出来 —— 没东西可还原了
            if (!File.Exists(record.BackupPath)) continue;

            if (items.TryGetValue(record.RelativePath, out var existing) && existing.CreatedAt >= record.CreatedAt)
                continue;

            items[record.RelativePath] = new GameFileRestoreItem(
                root,
                record.RelativePath,
                Path.Combine(root, record.RelativePath),
                record.BackupPath,
                record.CreatedAt,
                record.SizeBytes,
                Registered: true,
                record.Origin);
        }

        foreach (var item in ScanUnregistered(root, token))
        {
            if (!items.ContainsKey(item.RelativePath)) items[item.RelativePath] = item;
        }

        return items.Values
            .OrderByDescending(item => item.CreatedAt)
            .ThenBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// 把备份复制回原位。原文件存在就先另存为 <c>.before-restore-时间戳</c>，
    /// 所以还原这一步本身也能反悔；备份文件保留不动。
    /// </summary>
    public static GameFileRestoreResult Restore(GameFileRestoreItem item)
    {
        if (!File.Exists(item.BackupPath))
            return new GameFileRestoreResult(false, "这份备份已经不在了，还原不了。");

        try
        {
            var snapshot = SnapshotExisting(item.OriginalPath);

            var directory = Path.GetDirectoryName(item.OriginalPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            File.Copy(item.BackupPath, item.OriginalPath, overwrite: true);

            Log.Info($"已还原原版文件：{item.OriginalPath}（来源备份 {Path.GetFileName(item.BackupPath)}）");

            return new GameFileRestoreResult(true, snapshot is null
                ? $"已还原 {item.RelativePath}。原来没有这个文件，等于重新放了一份回去。"
                : $"已还原 {item.RelativePath}。\n\n还原前的文件另存为 {Path.GetFileName(snapshot)}，" +
                  "备份本身也保留着，随时可以再来一遍。");
        }
        catch (Exception ex)
        {
            Log.Error($"还原原版文件失败：{item.OriginalPath}", ex);
            return new GameFileRestoreResult(false, $"还原失败：{ex.Message}");
        }
    }

    /// <summary>还原前把当前文件另存一份，返回另存后的路径；原文件不存在时返回 null。</summary>
    private static string? SnapshotExisting(string path)
    {
        if (!File.Exists(path)) return null;

        var stamp = DateTime.Now.ToString("yyyyMMddHHmmss");
        var snapshot = $"{path}{SnapshotMarker}{stamp}";

        var suffix = 1;
        while (File.Exists(snapshot)) snapshot = $"{path}{SnapshotMarker}{stamp}-{suffix++}";

        File.Copy(path, snapshot, overwrite: false);
        return snapshot;
    }

    /// <summary>扫磁盘上没登记过的备份。Mods 目录整棵跳过。</summary>
    private static IEnumerable<GameFileRestoreItem> ScanUnregistered(string root, CancellationToken token)
    {
        var modsRoot = Path.Combine(root, "Mods") + Path.DirectorySeparatorChar;

        string[] files;
        try
        {
            files = Directory.GetFiles(root, "*" + Marker + "*", SearchOption.AllDirectories);
        }
        catch (Exception ex)
        {
            Log.Warn($"扫描覆盖前备份失败：{ex.Message}");
            yield break;
        }

        foreach (var path in files)
        {
            token.ThrowIfCancellationRequested();

            if (path.StartsWith(modsRoot, StringComparison.OrdinalIgnoreCase)) continue;

            var name = Path.GetFileName(path);
            var index = name.LastIndexOf(Marker, StringComparison.OrdinalIgnoreCase);
            if (index <= 0) continue;

            var originalPath = Path.Combine(Path.GetDirectoryName(path) ?? root, name[..index]);

            yield return new GameFileRestoreItem(
                root,
                Path.GetRelativePath(root, originalPath),
                originalPath,
                path,
                ParseStamp(name[(index + Marker.Length)..]),
                LengthOf(path),
                Registered: false,
                string.Empty);
        }
    }

    /// <summary>从备份名的后缀里取回时间戳，取不到就留空。</summary>
    private static DateTime ParseStamp(string text)
    {
        if (text.Length < 14) return default;

        return DateTime.TryParseExact(text[..14], "yyyyMMddHHmmss", null,
            System.Globalization.DateTimeStyles.None, out var stamp)
            ? stamp
            : default;
    }

    private static long LengthOf(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static GameFileBackupIndexFile ReadIndex()
    {
        var path = IndexPath;
        if (!File.Exists(path)) return new GameFileBackupIndexFile();

        try
        {
            return JsonSerializer.Deserialize<GameFileBackupIndexFile>(File.ReadAllText(path), Options)
                   ?? new GameFileBackupIndexFile();
        }
        catch (Exception ex)
        {
            // 坏掉的索引留一份现场再重来，不然连"哪些文件被登记过"都说不清了
            Log.Warn($"备份索引解析失败，已重新开始：{ex.Message}");

            try { File.Move(path, $"{path}.broken-{DateTime.Now:yyyyMMddHHmmss}", overwrite: true); }
            catch (Exception moveError) { Log.Warn($"保留坏索引失败：{moveError.Message}"); }

            return new GameFileBackupIndexFile();
        }
    }

    private static void WriteIndex(GameFileBackupIndexFile file)
        => AtomicFile.WriteAllText(IndexPath, JsonSerializer.Serialize(file, Options));

    private static string Normalize(string path)
        => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool SameDirectory(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left)) return false;

        try
        {
            return string.Equals(Normalize(left), right, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Log.Warn($"索引里的游戏目录无法识别：{left}（{ex.Message}）");
            return false;
        }
    }
}
