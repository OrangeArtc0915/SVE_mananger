using System.IO;
using StardewLauncher.Api;
using StardewLauncher.Core.App;
using StardewLauncher.Core.Games;
using StardewLauncher.Core.Instances;
using StardewLauncher.Core.Mods;
using StardewLauncher.Core.Saves;
using StardewLauncher.Core.Smapi;

namespace StardewLauncher.Core.Plugins;

/// <summary>
/// 交给扩展的上下文：只有只读数据、取数工具和扩展自己的缓存目录。
/// 插件拿不到启动器的任何写操作，也不引用界面框架。
/// </summary>
public sealed class WidgetContext(string storageDirectory) : IWidgetContext
{
    /// <summary>快照缓存时长。一次刷新里多个扩展都会来取，缓存一下就不会重复扫盘。</summary>
    private static readonly TimeSpan SnapshotTtl = TimeSpan.FromSeconds(3);

    private static readonly object SnapshotLock = new();
    private static LauncherSnapshot? _cached;
    private static DateTime _cachedAt;

    public string LauncherVersion => AppInfo.Version;

    public string StorageDirectory { get; } = EnsureStorage(storageDirectory);

    public void Log(string message) => StardewLauncher.Core.Logging.Log.Info($"[扩展] {message}");

    public Task<string?> HttpGetAsync(string url, CancellationToken cancellationToken = default)
        => HttpDownloader.GetStringAsync(url, "StardewLauncher-Widget", cancellationToken);

    public LauncherSnapshot GetSnapshot()
    {
        lock (SnapshotLock)
        {
            if (_cached is not null && DateTime.UtcNow - _cachedAt < SnapshotTtl) return _cached;

            _cached = Build();
            _cachedAt = DateTime.UtcNow;

            return _cached;
        }
    }

    private static string EnsureStorage(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex)
        {
            StardewLauncher.Core.Logging.Log.Warn($"创建扩展数据目录失败：{ex.Message}");
        }

        return directory;
    }

    private static LauncherSnapshot Build()
    {
        var current = InstanceStore.Current;

        return new LauncherSnapshot(
            AppInfo.Version,
            GameProcess.IsRunning(),
            InstanceStore.All.Select(ToSnapshot).ToList(),
            current is null ? null : ToSnapshot(current),
            current is null ? [] : ScanMods(current),
            SaveScanner.Scan().Select(ToSnapshot).ToList());
    }

    private static InstanceSnapshot ToSnapshot(Instance instance) => new(
        instance.Id,
        instance.Name,
        instance.IsVanilla,
        instance.GameDir,
        instance.ModsDirectory,
        instance.Install?.GameVersion,
        instance.Install?.SmapiVersion,
        instance.Install?.HasSmapi ?? false,
        instance.TotalPlaySeconds,
        instance.LastLaunchAt);

    private static IReadOnlyList<ModSnapshot> ScanMods(Instance instance)
    {
        var scan = ModScanner.Scan(instance.ModsDirectory);
        if (scan.Mods.Count == 0) return [];

        // 依赖问题在内存里算，顺手填上，插件就能直接用来判断"这个 Mod 会不会加载失败"
        DependencyResolver.Evaluate(scan.Mods);

        return scan.Mods.Select(mod => new ModSnapshot(
            mod.DisplayName,
            mod.UniqueId,
            mod.DisplayVersion,
            mod.DisplayAuthor,
            mod.IsEnabled,
            mod.Manifest?.ContentPackFor is not null,
            mod.State != ModState.Invalid,
            mod.HasIssues,
            mod.FolderPath)).ToList();
    }

    private static SaveSnapshot ToSnapshot(SaveSummary save) => new(
        save.Id,
        save.PlayerName,
        save.FarmName,
        save.Directory,
        save.Year,
        save.Season,
        save.DayOfMonth,
        save.MillisecondsPlayed / 1000,
        save.LastSavedAt,
        save.SizeBytes);
}
