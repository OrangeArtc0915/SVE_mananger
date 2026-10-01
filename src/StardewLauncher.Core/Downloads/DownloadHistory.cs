using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using StardewLauncher.Core.App;
using StardewLauncher.Core.Logging;

namespace StardewLauncher.Core.Downloads;

/// <summary>一条下载历史。</summary>
public sealed class DownloadHistoryEntry
{
    /// <summary>任务标题。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>结束状态的中文文字（完成 / 已取消 / 失败）。</summary>
    public string StateText { get; set; } = string.Empty;

    /// <summary>结束时的说明文字，可能为空。</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>结束时间（本地时间）。</summary>
    public DateTime FinishedAt { get; set; }

    /// <summary>从开始到结束的耗时（秒）。</summary>
    public double DurationSeconds { get; set; }
}

/// <summary>
/// 下载历史：把已经结束的任务落盘，供下载监视器回看。
/// 只记「结果」不记目标目录 —— 任务本身不保证携带可靠的目标路径，记了反而误导用户。
/// </summary>
public static class DownloadHistory
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        // 不转义中文，保证用户手工查看历史文件时看得懂
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    /// <summary>最新的排在最前。</summary>
    private static readonly List<DownloadHistoryEntry> Items = [];

    public static IReadOnlyList<DownloadHistoryEntry> Entries => Items;

    /// <summary>历史发生变化（新增 / 清空 / 载入完成）时触发。</summary>
    public static event Action? Changed;

    public static string FilePath => Path.Combine(Paths.Data, "download-history.json");

    public static void Load()
    {
        Paths.Init();
        Items.Clear();

        try
        {
            var file = FilePath;

            if (File.Exists(file))
            {
                var model = JsonSerializer.Deserialize<List<DownloadHistoryEntry>>(File.ReadAllText(file), Options);

                if (model is not null)
                {
                    foreach (var entry in model)
                    {
                        if (entry is null) continue;

                        // 手工编辑过的文件可能缺字段，这里统一兜底成空串，界面才不会显示空白行
                        entry.Title ??= string.Empty;
                        entry.StateText ??= string.Empty;
                        entry.Message ??= string.Empty;

                        Items.Add(entry);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("读取下载历史失败", ex);
        }

        Trim();
        Log.Info($"已载入 {Items.Count} 条下载历史");
        Changed?.Invoke();
    }

    /// <summary>记录一条刚结束的任务。startedAt 由调用方在任务开始时记下，用于算耗时。</summary>
    public static void Record(string title, string stateText, string message, DateTime startedAt)
    {
        var finishedAt = DateTime.Now;
        var seconds = (finishedAt - startedAt).TotalSeconds;

        Items.Insert(0, new DownloadHistoryEntry
        {
            Title = title ?? string.Empty,
            StateText = stateText ?? string.Empty,
            Message = message ?? string.Empty,
            FinishedAt = finishedAt,
            // 系统时间被往回调时会出现负数，落盘前收敛掉
            DurationSeconds = seconds < 0 ? 0 : seconds
        });

        Trim();
        Save();

        Log.Info($"下载历史新增：{title}（{stateText}）");
    }

    public static void Clear()
    {
        Items.Clear();
        Save();

        Log.Info("已清空下载历史");
    }

    private static void Trim()
    {
        var keep = Math.Max(1, SettingsStore.Current.DownloadHistoryKeepCount);

        if (Items.Count > keep) Items.RemoveRange(keep, Items.Count - keep);
    }

    private static void Save()
    {
        try
        {
            Paths.Init();
            Directory.CreateDirectory(Paths.Data);

            IO.AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(Items, Options));
        }
        catch (Exception ex)
        {
            Log.Error("下载历史保存失败", ex);
        }

        Changed?.Invoke();
    }
}
