using System.Globalization;
using System.IO;
using StardewLauncher.Core.App;
using StardewLauncher.Core.IO;
using StardewLauncher.Core.Logging;

namespace StardewLauncher.Core.Saves;

/// <summary>一份存档备份。落在启动器的 Data\save-backups\&lt;存档 Id&gt;\&lt;时间&gt;\ 下。</summary>
public sealed class SaveBackup
{
    public string SaveId { get; init; } = string.Empty;

    /// <summary>备份目录绝对路径。</summary>
    public string Directory { get; init; } = string.Empty;

    /// <summary>备份目录名，形如 20260923-215530 或 20260923-215530-auto。</summary>
    public string FolderName { get; init; } = string.Empty;

    public DateTime CreatedAt { get; init; }

    public long SizeBytes { get; init; }

    public int FileCount { get; init; }

    /// <summary>恢复前自动打的快照（不是用户手动建的）。</summary>
    public bool IsAutoSnapshot { get; init; }

    public bool HasMainFile { get; init; }

    public bool HasInfoFile { get; init; }

    /// <summary>主存档与 SaveGameInfo 是否都在，缺一个就是坏备份。</summary>
    public bool IsComplete => HasMainFile && HasInfoFile;

    public string TimeText => CreatedAt.ToString("yyyy-MM-dd HH:mm:ss");

    public string KindText => IsAutoSnapshot ? "恢复前快照" : "手动备份";

    public string SizeText => SizeBytes >= 1024L * 1024
        ? $"{SizeBytes / 1024.0 / 1024.0:F1} MB"
        : $"{SizeBytes / 1024.0:F0} KB";

    public string DetailText => IsComplete
        ? $"{KindText} · {SizeText} · {FileCount} 个文件"
        : $"{KindText} · {SizeText} · 文件缺失，不可用于恢复";
}

/// <summary>
/// 存档备份与回滚。
/// 备份放在启动器自己的数据目录里（不往游戏存档目录里塞文件，避免被 Steam 云同步带走），
/// 恢复前会先把当前存档自动快照一次，所以恢复动作本身也能反悔。
/// </summary>
public static class SaveBackupService
{
    private const string AutoSuffix = "-auto";

    /// <summary>备份根目录。</summary>
    public static string Root => Path.Combine(Paths.Data, "save-backups");

    public static string DirectoryOf(string saveId) => Path.Combine(Root, saveId);

    /// <summary>某个存档已有多少份备份。</summary>
    public static int CountOf(string saveId)
    {
        var directory = DirectoryOf(saveId);
        if (!Directory.Exists(directory)) return 0;

        try { return Directory.GetDirectories(directory).Length; }
        catch (Exception ex)
        {
            Log.Warn($"统计备份数量失败：{ex.Message}");
            return 0;
        }
    }

    /// <summary>列出某个存档的全部备份，按时间倒序。</summary>
    public static IReadOnlyList<SaveBackup> List(string saveId)
    {
        var result = new List<SaveBackup>();

        var root = DirectoryOf(saveId);
        if (!Directory.Exists(root)) return result;

        string[] directories;
        try
        {
            directories = Directory.GetDirectories(root);
        }
        catch (Exception ex)
        {
            Log.Warn($"读取备份目录失败：{ex.Message}");
            return result;
        }

        foreach (var directory in directories)
        {
            var backup = Read(saveId, directory);
            if (backup is not null) result.Add(backup);
        }

        result.Sort((left, right) => right.CreatedAt.CompareTo(left.CreatedAt));
        return result;
    }

    private static SaveBackup? Read(string saveId, string directory)
    {
        var folderName = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(folderName)) return null;

        var createdAt = ParseStamp(directory, folderName);

        long size = 0;
        var count = 0;

        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                count++;
                try { size += new FileInfo(file).Length; }
                catch { /* 单个文件读不到就跳过 */ }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"统计备份大小失败 {directory}：{ex.Message}");
        }

        return new SaveBackup
        {
            SaveId = saveId,
            Directory = directory,
            FolderName = folderName,
            CreatedAt = createdAt,
            SizeBytes = size,
            FileCount = count,
            IsAutoSnapshot = folderName.EndsWith(AutoSuffix, StringComparison.OrdinalIgnoreCase),
            HasMainFile = File.Exists(Path.Combine(directory, saveId)),
            HasInfoFile = File.Exists(Path.Combine(directory, SaveScanner.InfoFileName))
        };
    }

    private static DateTime ParseStamp(string directory, string folderName)
    {
        var stamp = folderName.EndsWith(AutoSuffix, StringComparison.OrdinalIgnoreCase)
            ? folderName[..^AutoSuffix.Length]
            : folderName;

        if (DateTime.TryParseExact(stamp, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsed))
            return parsed;

        try { return Directory.GetCreationTime(directory); }
        catch { return DateTime.MinValue; }
    }

    /// <summary>把一份存档整目录备份下来。autoSnapshot 用于"恢复前自动快照"。</summary>
    public static SaveBackup? Create(
        SaveSummary save,
        out string? error,
        bool autoSnapshot = false,
        IProgress<double>? progress = null)
    {
        error = null;

        if (save is null || string.IsNullOrWhiteSpace(save.Directory) || !Directory.Exists(save.Directory))
        {
            error = "存档目录不存在，无法备份";
            return null;
        }

        var target = MakeTargetDirectory(save.Id, autoSnapshot);
        if (target is null)
        {
            error = "创建备份目录失败";
            return null;
        }

        try
        {
            CopyInto(save.Directory, target, save.Id, progress);
        }
        catch (Exception ex)
        {
            error = $"备份失败：{ex.Message}";
            Log.Error($"备份存档失败：{save.Directory} → {target}", ex);

            // 半截备份没有意义，直接清掉
            try { Directory.Delete(target, true); } catch { /* 清理失败不覆盖原始错误 */ }

            return null;
        }

        var backup = Read(save.Id, target);
        Log.Info($"已备份存档「{save.DisplayName}」→ {target}");
        ActivityLog.Write(LogSource.App, $"已备份存档「{save.DisplayName}」");
        return backup;
    }

    /// <summary>
    /// 把备份覆盖回存档目录。恢复前会先给当前存档打一份自动快照；
    /// 快照失败就中止，避免"恢复后无法反悔"。
    /// </summary>
    public static bool Restore(
        SaveBackup backup,
        SaveSummary target,
        out string? error,
        IProgress<double>? progress = null)
    {
        error = null;

        if (backup is null || !Directory.Exists(backup.Directory))
        {
            error = "备份目录不存在";
            return false;
        }

        if (!backup.IsComplete)
        {
            error = "这份备份缺少主存档或 SaveGameInfo，不能用它恢复";
            return false;
        }

        if (target is null || string.IsNullOrWhiteSpace(target.Directory))
        {
            error = "目标存档目录无效";
            return false;
        }

        // 先给当前存档拍一张，恢复动作本身也要能反悔（可在设置里关掉）
        if (SettingsStore.Current.SnapshotBeforeRestore)
        {
            var snapshot = Create(target, out var snapshotError, autoSnapshot: true);
            if (snapshot is null)
            {
                error = $"恢复前的自动快照失败，已中止：{snapshotError}";
                return false;
            }

            Log.Info($"恢复前已自动快照：{snapshot.FolderName}");
        }

        try
        {
            CopyInto(backup.Directory, target.Directory, target.Id, progress);
        }
        catch (Exception ex)
        {
            error = $"恢复失败：{ex.Message}";
            Log.Error($"恢复存档失败：{backup.Directory} → {target.Directory}", ex);
            return false;
        }

        Log.Info($"已恢复存档「{target.DisplayName}」");
        ActivityLog.Write(LogSource.App, $"已恢复存档「{target.DisplayName}」");
        return true;
    }

    /// <summary>删除一份备份。</summary>
    public static bool Delete(SaveBackup backup, bool toRecycleBin, out string? error)
    {
        error = null;

        if (backup is null || !Directory.Exists(backup.Directory))
        {
            error = "备份目录不存在";
            return false;
        }

        if (!ShellHelper.TryDeleteDirectory(backup.Directory, toRecycleBin, out var shellError))
        {
            error = shellError;
            return false;
        }

        Log.Info($"已删除备份：{backup.Directory}");
        return true;
    }

    /// <summary>只保留最近的 keep 份备份，多出来的直接删掉。返回删除数量。</summary>
    public static int Prune(string saveId, int keep)
    {
        if (keep <= 0) return 0;

        var all = List(saveId);
        if (all.Count <= keep) return 0;

        var removed = 0;

        foreach (var backup in all.Skip(keep))
        {
            try
            {
                Directory.Delete(backup.Directory, true);
                removed++;
            }
            catch (Exception ex)
            {
                Log.Warn($"清理旧备份失败 {backup.Directory}：{ex.Message}");
            }
        }

        if (removed > 0) Log.Info($"已清理 {removed} 份旧备份（存档 {saveId}，保留 {keep} 份）");
        return removed;
    }

    private static string? MakeTargetDirectory(string saveId, bool autoSnapshot)
    {
        try
        {
            Directory.CreateDirectory(DirectoryOf(saveId));

            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
                        + (autoSnapshot ? AutoSuffix : string.Empty);

            var target = Path.Combine(DirectoryOf(saveId), stamp);
            var index = 2;

            // 同一秒内连点两次时补序号，避免覆盖
            while (Directory.Exists(target))
            {
                target = Path.Combine(DirectoryOf(saveId), $"{stamp}-{index}");
                index++;
            }

            Directory.CreateDirectory(target);
            return target;
        }
        catch (Exception ex)
        {
            Log.Error($"创建备份目录失败（存档 {saveId}）", ex);
            return null;
        }
    }

    /// <summary>把 source 下的全部内容复制进 target（覆盖同名文件，不删除 target 里多出来的文件）。</summary>
    private static void CopyInto(string source, string target, string saveId, IProgress<double>? progress)
    {
        Directory.CreateDirectory(target);

        var files = Directory.GetFiles(source, "*", SearchOption.AllDirectories);
        var done = 0;
        var total = Math.Max(1, files.Length);

        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(source, file);
            var destination = Path.Combine(target, relative);

            var parent = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

            File.Copy(file, destination, overwrite: true);

            done++;
            progress?.Report((double)done / total);
        }

        // 主存档与摘要一定在；缺了说明源目录本来就不完整，交给上层判断
        if (!File.Exists(Path.Combine(target, SaveScanner.InfoFileName)))
            Log.Warn($"备份里没有 {SaveScanner.InfoFileName}（存档 {saveId}）");
    }
}
