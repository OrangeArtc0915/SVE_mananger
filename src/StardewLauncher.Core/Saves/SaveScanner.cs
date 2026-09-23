using System.Globalization;
using System.IO;
using System.Xml.Linq;
using StardewLauncher.Core.Logging;

namespace StardewLauncher.Core.Saves;

/// <summary>
/// 扫描游戏存档目录。游戏只认 %APPDATA%\StardewValley\Saves（Roaming），
/// 启动器不复制、不搬移存档，只读取摘要与做备份。
/// </summary>
public static class SaveScanner
{
    /// <summary>存档摘要文件名（游戏自身写入）。</summary>
    public const string InfoFileName = "SaveGameInfo";

    /// <summary>游戏存档目录。</summary>
    public static string SavesDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "StardewValley", "Saves");

    public static bool Exists => !string.IsNullOrWhiteSpace(SavesDirectory) && Directory.Exists(SavesDirectory);

    /// <summary>枚举全部存档，按最后保存时间倒序。解析失败或结构不完整的目录会被跳过。</summary>
    public static IReadOnlyList<SaveSummary> Scan()
    {
        var result = new List<SaveSummary>();

        if (!Exists) return result;

        string[] directories;
        try
        {
            directories = Directory.GetDirectories(SavesDirectory);
        }
        catch (Exception ex)
        {
            Log.Warn($"读取存档目录失败：{ex.Message}");
            return result;
        }

        foreach (var directory in directories)
        {
            var summary = Read(directory);
            if (summary is not null) result.Add(summary);
        }

        result.Sort((left, right) =>
        {
            var a = left.LastSavedAt ?? DateTime.MinValue;
            var b = right.LastSavedAt ?? DateTime.MinValue;
            if (a != b) return b.CompareTo(a);
            return string.Compare(left.DisplayName, right.DisplayName, StringComparison.OrdinalIgnoreCase);
        });

        Log.Info($"扫描到 {result.Count} 个存档");
        return result;
    }

    /// <summary>读取单个存档目录的摘要。不是有效存档时返回 null。</summary>
    public static SaveSummary? Read(string saveDirectory)
    {
        if (string.IsNullOrWhiteSpace(saveDirectory) || !Directory.Exists(saveDirectory)) return null;

        var id = Path.GetFileName(saveDirectory.TrimEnd(Path.DirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(id)) return null;

        var infoFile = Path.Combine(saveDirectory, InfoFileName);
        if (!File.Exists(infoFile)) return null;

        var mainFile = Path.Combine(saveDirectory, id);
        var hasMain = File.Exists(mainFile);

        var root = LoadInfo(infoFile);
        if (root is null) return null;

        return new SaveSummary
        {
            Id = id,
            Directory = saveDirectory,
            PlayerName = ReadText(root, "name"),
            FarmName = ReadText(root, "farmName"),
            Money = ReadInt(root, "money"),
            TotalMoneyEarned = ReadInt(root, "totalMoneyEarned"),
            DayOfMonth = ReadInt(root, "dayOfMonthForSaveGame"),
            Season = ReadInt(root, "seasonForSaveGame"),
            Year = ReadInt(root, "yearForSaveGame"),
            MillisecondsPlayed = ReadLong(root, "millisecondsPlayed"),
            DeepestMineLevel = ReadInt(root, "deepestMineLevel"),
            FarmingLevel = ReadInt(root, "farmingLevel"),
            MiningLevel = ReadInt(root, "miningLevel"),
            ForagingLevel = ReadInt(root, "foragingLevel"),
            FishingLevel = ReadInt(root, "fishingLevel"),
            CombatLevel = ReadInt(root, "combatLevel"),
            LastSavedAt = hasMain ? SafeLastWrite(mainFile) : SafeLastWrite(infoFile),
            SizeBytes = DirectorySize(saveDirectory),
            HasMainFile = hasMain
        };
    }

    /// <summary>读取 SaveGameInfo 根节点。异常一律吞掉并记日志，坏存档不应让整个列表失败。</summary>
    private static XElement? LoadInfo(string infoFile)
    {
        try
        {
            using var stream = File.Open(infoFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return XDocument.Load(stream, LoadOptions.None).Root;
        }
        catch (Exception ex)
        {
            Log.Warn($"解析 {infoFile} 失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>按节点名直接子节点取值（忽略命名空间，只看本地名）。</summary>
    private static string ReadText(XElement root, string name)
        => FindChild(root, name)?.Value.Trim() ?? string.Empty;

    private static int ReadInt(XElement root, string name)
        => int.TryParse(ReadText(root, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;

    private static long ReadLong(XElement root, string name)
        => long.TryParse(ReadText(root, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0L;

    private static XElement? FindChild(XElement root, string name)
    {
        foreach (var child in root.Elements())
        {
            if (string.Equals(child.Name.LocalName, name, StringComparison.Ordinal)) return child;
        }

        return null;
    }

    private static DateTime? SafeLastWrite(string path)
    {
        try { return File.GetLastWriteTime(path); }
        catch (Exception ex)
        {
            Log.Warn($"读取文件时间失败 {path}：{ex.Message}");
            return null;
        }
    }

    private static long DirectorySize(string directory)
    {
        long total = 0;

        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(file).Length; }
                catch { /* 单个文件读不到就跳过 */ }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"统计存档大小失败 {directory}：{ex.Message}");
        }

        return total;
    }
}
