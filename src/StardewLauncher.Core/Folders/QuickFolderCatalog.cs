using System.IO;
using StardewLauncher.Core.App;
using StardewLauncher.Core.Diagnostics;
using StardewLauncher.Core.Instances;
using StardewLauncher.Core.Saves;

namespace StardewLauncher.Core.Folders;

/// <summary>一个常用文件夹条目。<see cref="Path"/> 是实际要打开的目录。</summary>
public sealed record QuickFolder(string Name, string Path, string Note, string Icon, bool BuiltIn);

/// <summary>
/// 常用文件夹：内置几个随当前实例与设置动态算出来的目录，外加用户自己收藏的目录。
/// 自定义项只存路径不存名字，名字和说明每次现算 —— 用户给目录改名后列表里不会留着旧名字。
/// </summary>
public static class QuickFolderCatalog
{
    /// <summary>内置目录。全部按当前状态现算，没装游戏 / 没有实例时对应条目直接不出现。</summary>
    public static IReadOnlyList<QuickFolder> BuiltIn()
    {
        var settings = SettingsStore.Current;
        var list = new List<QuickFolder>();

        if (InstanceStore.Current is { } instance)
        {
            Add(list, "当前实例的 Mods 目录", instance.ModsDirectory,
                $"「{instance.Name}」加载 Mod 的地方", "lucide/package");

            Add(list, "游戏目录", instance.GameDir,
                $"「{instance.Name}」的游戏本体", "lucide/box");
        }

        Add(list, "游戏存档目录", SaveScanner.SavesDirectory,
            "游戏自己写存档的地方，备份也读这里", "lucide/save");

        // 日志目录只有真要读日志时才去扫，扫不到就不显示这一条
        var logFile = SmapiLogReader.FindLogFile();
        if (!string.IsNullOrWhiteSpace(logFile))
        {
            Add(list, "SMAPI 日志目录", Path.GetDirectoryName(logFile) ?? string.Empty,
                $"最近一份日志：{Path.GetFileName(logFile)}", "lucide/scroll-text");
        }

        var download = string.IsNullOrWhiteSpace(settings.DownloadFolder)
            ? Paths.Downloads
            : settings.DownloadFolder;

        Add(list, "下载 Mod 的目录", download,
            "浏览器下载 Mod 后，监视器从这里入库", "lucide/download");

        Add(list, "Mod 库目录", settings.ModLibraryDirectory,
            "解压好、待安装的 Mod 放这里", "lucide/boxes");

        Add(list, "实例目录", Paths.Instances,
            "每个实例一个 JSON，都在这里", "lucide/layers");

        Add(list, "启动器数据目录", Paths.Data,
            "设置、缓存与日志的总目录", "lucide/server");

        Add(list, "启动器日志目录", Paths.Log,
            "启动器自己写的运行日志", "lucide/scroll");

        return list;
    }

    /// <summary>用户收藏的目录。路径失效的也会返回，由界面标出来，不偷偷吞掉。</summary>
    public static IReadOnlyList<QuickFolder> Custom()
    {
        var list = new List<QuickFolder>();

        foreach (var path in SettingsStore.Current.QuickFolders)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;

            var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(name)) name = path;

            list.Add(new QuickFolder(name, path, Describe(path), "lucide/heart", false));
        }

        return list;
    }

    /// <summary>收藏一个目录。已收藏过或路径不可用时返回 false 并给出原因。</summary>
    public static bool Add(string path, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(path))
        {
            error = "没有选择目录。";
            return false;
        }

        var normalized = path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (!Directory.Exists(normalized))
        {
            error = $"目录不存在：{normalized}";
            return false;
        }

        var folders = SettingsStore.Current.QuickFolders;
        if (folders.Any(existing => string.Equals(existing, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            error = "这个目录已经在收藏里了。";
            return false;
        }

        folders.Add(normalized);
        SettingsStore.Save();
        return true;
    }

    public static void Remove(string path)
    {
        var folders = SettingsStore.Current.QuickFolders;
        folders.RemoveAll(existing => string.Equals(existing, path, StringComparison.OrdinalIgnoreCase));
        SettingsStore.Save();
    }

    /// <summary>目录还在不在，用来在界面上标出失效的收藏。</summary>
    public static bool Exists(QuickFolder folder) =>
        !string.IsNullOrWhiteSpace(folder.Path) && Directory.Exists(folder.Path);

    private static void Add(List<QuickFolder> list, string name, string path, string note, string icon)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        list.Add(new QuickFolder(name, path, note, icon, true));
    }

    private static string Describe(string path) =>
        Directory.Exists(path) ? "自己收藏的目录" : "目录已不存在，可以在右侧移除";
}
