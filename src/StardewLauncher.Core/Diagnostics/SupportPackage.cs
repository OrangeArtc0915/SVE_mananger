using System.IO;
using System.IO.Compression;
using System.Text;
using StardewLauncher.Core.App;
using StardewLauncher.Core.Instances;
using StardewLauncher.Core.Logging;
using StardewLauncher.Core.Mods;

namespace StardewLauncher.Core.Diagnostics;

/// <summary>反馈包的导出结果。</summary>
public sealed record SupportPackageResult(bool Ok, string Message, string? Path);

/// <summary>
/// 把「求助时要来回问一圈」的东西打成一个 zip：体检报告、Mod 清单、设置摘要、最近的运行日志。
///
/// <para>
/// 包里**不含** Nexus 密钥与樱花密钥：设置摘要按白名单逐项写入，另外所有文本在落包前
/// 再用实际密钥值做一次替换兜底 —— 万一日志里打印过密钥，也会被打成 ***。
/// </para>
/// </summary>
public static class SupportPackage
{
    /// <summary>包里最多放几份日志。</summary>
    private const int MaxLogFiles = 5;

    private const string Redacted = "***";

    /// <summary>
    /// 生成反馈包。report 为空时现场做一次体检。
    /// 先写成临时文件再改名，中途失败不会留下一个半截的 zip。
    /// </summary>
    public static SupportPackageResult Create(string destinationPath, HealthReport? report = null,
        IProgress<string>? progress = null)
    {
        var temp = destinationPath + ".part";

        try
        {
            var directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            int logCount;

            using (var archive = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                progress?.Report("正在写体检报告…");
                Write(archive, "体检报告.txt", (report ?? HealthInspector.Inspect()).ToText());

                progress?.Report("正在整理 Mod 清单…");
                Write(archive, "Mod 清单.txt", DescribeMods());

                progress?.Report("正在写设置摘要…");
                Write(archive, "设置摘要.txt", DescribeSettings());

                progress?.Report("正在打包运行日志…");
                logCount = WriteLogs(archive);
            }

            File.Move(temp, destinationPath, overwrite: true);

            Log.Info($"已导出反馈包：{destinationPath}（日志 {logCount} 份）");

            return new SupportPackageResult(true,
                $"已导出到 {destinationPath}\n含体检报告、Mod 清单、设置摘要与 {logCount} 份运行日志；" +
                "Nexus 与樱花密钥不会写进包里。",
                destinationPath);
        }
        catch (Exception ex)
        {
            Log.Error($"导出反馈包失败：{destinationPath}", ex);
            TryDelete(temp);

            return new SupportPackageResult(false, $"导出失败：{ex.Message}", null);
        }
    }

    // ————— 包内各文件 —————

    /// <summary>Mod 清单：启用、禁用、异常分开列，附上 ID、版本与依赖问题。</summary>
    private static string DescribeMods()
    {
        if (InstanceStore.Current is not { } instance)
            return "没有选中任何实例，读不到 Mod 列表。";

        var scan = ModScanner.Scan(instance.ModsDirectory);

        if (!scan.ModsDirectoryExists)
            return $"Mods 目录不存在：{scan.ModsDirectory}";

        DependencyResolver.Evaluate(scan.Mods);

        var text = new StringBuilder();
        text.AppendLine($"实例：{instance.Name}（{instance.KindText}）");
        text.AppendLine($"目录：{scan.ModsDirectory}");
        text.AppendLine($"统计：{scan.SummaryText}");
        text.AppendLine();

        Section(text, "启用", scan.Mods.Where(mod => mod.State == ModState.Enabled));
        Section(text, "禁用", scan.Mods.Where(mod => mod.State == ModState.Disabled));
        Section(text, "异常", scan.Mods.Where(mod => mod.State == ModState.Invalid));

        return text.ToString();
    }

    private static void Section(StringBuilder text, string title, IEnumerable<ModEntry> mods)
    {
        var list = mods.ToList();
        if (list.Count == 0) return;

        text.AppendLine($"———— {title}（{list.Count}）————");

        foreach (var mod in list)
        {
            text.AppendLine($"{mod.DisplayName}  {mod.DisplayVersion}  {mod.DisplayAuthor}  [{mod.TypeText}]");
            text.AppendLine($"    ID：{mod.UniqueId}");

            if (!string.IsNullOrWhiteSpace(mod.DisplayDescription))
                text.AppendLine($"    说明：{Shorten(mod.DisplayDescription)}");

            if (mod.ParseError is { } error)
                text.AppendLine($"    解析问题：{error}");

            foreach (var issue in mod.Issues)
                text.AppendLine($"    依赖问题：{Describe(issue)}");
        }

        text.AppendLine();
    }

    /// <summary>Mod 的简介常常是一整段宣传语，包里只留一句。</summary>
    private static string Shorten(string text)
    {
        var single = text.ReplaceLineEndings(" ").Trim();
        return single.Length <= 80 ? single : single[..80] + "…";
    }

    private static string Describe(DependencyIssue issue) => issue.Kind switch
    {
        DependencyIssueKind.Missing => $"缺少前置 {issue.UniqueId}",
        DependencyIssueKind.VersionTooLow => $"需要 {issue.UniqueId} ≥ {issue.RequiredVersion}，实际 {issue.FoundVersion}",
        _ => $"与 {issue.UniqueId} 互为循环依赖"
    };

    /// <summary>
    /// 设置摘要按白名单逐项写，密钥只写「有没有配置」，不写值。
    /// 这样即使以后设置里新增了敏感字段，也不会被无意带进包里。
    /// </summary>
    private static string DescribeSettings()
    {
        var settings = SettingsStore.Current;

        var text = new StringBuilder();
        text.AppendLine($"启动器版本：{AppInfo.VersionDisplay}");
        text.AppendLine($"界面语言：{settings.Language}");
        text.AppendLine($"主题：{settings.Theme} / {settings.AccentTheme}");
        text.AppendLine($"下载源：{settings.DownloadSource}，启动器更新线路：{settings.LauncherUpdateSource}");
        text.AppendLine($"游戏目录：{settings.GameDir}");
        text.AppendLine($"Mod 库目录：{settings.ModLibraryDirectory}");
        text.AppendLine($"下载监控目录：{settings.DownloadFolder}");
        text.AppendLine($"启动时检查更新：{settings.CheckUpdateOnStartup}");
        text.AppendLine($"导入前备份：{settings.BackupBeforeImport}");
        text.AppendLine($"删除到回收站：{settings.DeleteToRecycleBin}");
        text.AppendLine($"日志上限：{settings.MaxLogFileCount} 份 / {settings.MaxLogFileSize / 1024 / 1024} MB");
        text.AppendLine($"存档备份保留：{settings.SaveBackupKeepCount} 份，恢复前快照：{settings.SnapshotBeforeRestore}");
        text.AppendLine($"已配置 Nexus 密钥：{(string.IsNullOrWhiteSpace(settings.NexusApiKey) ? "否" : "是")}");
        text.AppendLine($"已配置樱花密钥：{(string.IsNullOrWhiteSpace(settings.SakuraAccessKey) ? "否" : "是")}");

        return text.ToString();
    }

    private static int WriteLogs(ZipArchive archive)
    {
        if (!Directory.Exists(Paths.Log)) return 0;

        List<FileInfo> files;
        try
        {
            files = Directory.EnumerateFiles(Paths.Log, "*.log")
                .Select(path => new FileInfo(path))
                .OrderByDescending(info => info.LastWriteTime)
                .Take(MaxLogFiles)
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Warn($"列日志文件失败：{ex.Message}");
            return 0;
        }

        var count = 0;

        foreach (var file in files)
        {
            try
            {
                Write(archive, $"运行日志/{file.Name}", File.ReadAllText(file.FullName));
                count++;
            }
            catch (Exception ex)
            {
                Log.Warn($"读取日志失败：{file.Name}（{ex.Message}）");
            }
        }

        return count;
    }

    private static void Write(ZipArchive archive, string entryName, string content)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);

        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(Redact(content));
    }

    /// <summary>
    /// 兜底脱敏：把两个密钥的实际值从文本里抹掉。日志里理论上不会打印密钥，
    /// 但「理论上不会」不能当作保证，多这一道几乎零成本。
    /// </summary>
    private static string Redact(string text)
    {
        var settings = SettingsStore.Current;

        foreach (var secret in new[] { settings.NexusApiKey, settings.SakuraAccessKey })
        {
            var value = secret?.Trim();

            // 太短的值（例如用户只填了两个字符）替换起来会误伤正常文本
            if (!string.IsNullOrWhiteSpace(value) && value.Length >= 6)
                text = text.Replace(value, Redacted, StringComparison.Ordinal);
        }

        return text;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Warn($"删除临时文件失败 {path}：{ex.Message}");
        }
    }
}
