using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using StardewLauncher.Api;
using StardewLauncher.Core.App;
using StardewLauncher.Core.Logging;

namespace StardewLauncher.Core.Plugins;

/// <summary>扩展目录里扫到的一个扩展。</summary>
public sealed class WidgetPluginEntry
{
    /// <summary>稳定键：dll 文件名或子文件夹名（都不带扩展名）。设置里存的就是它。</summary>
    public required string Key { get; init; }

    /// <summary>扩展所在目录，扩展自带的依赖 dll 也从这里找。</summary>
    public required string Folder { get; init; }

    /// <summary>主 dll 路径。</summary>
    public required string AssemblyPath { get; init; }

    /// <summary>是否已在「扩展管理」里启用。</summary>
    public bool Enabled { get; set; }

    /// <summary>加载失败的原因；为空表示没试过或已加载成功。</summary>
    public string? Error { get; set; }

    /// <summary>加载成功后的插件实例；未加载为 null。</summary>
    public IHomepageWidgetPlugin? Instance { get; private set; }

    private PluginLoadContext? _context;

    /// <summary>界面上的名字：加载成功后用插件自己声明的标题。</summary>
    public string DisplayName => Instance?.Title ?? Key;

    /// <summary>插件在主页上的小组件 id：用插件声明的 Id，未加载时退回键。</summary>
    public string WidgetId => Instance?.Id ?? Key;

    internal void Attach(PluginLoadContext context, IHomepageWidgetPlugin plugin)
    {
        _context = context;
        Instance = plugin;
        Error = null;
    }

    /// <summary>卸载并回收加载上下文，随后 dll 文件可以被替换或删除。</summary>
    internal void Detach()
    {
        Instance = null;

        try
        {
            _context?.Unload();
        }
        catch (Exception ex)
        {
            Log.Warn($"扩展卸载失败 {Key}：{ex.Message}");
        }

        _context = null;
    }

    internal bool IsLoaded => Instance is not null;

    internal void Restore(WidgetPluginEntry previous)
    {
        _context = previous._context;
        Instance = previous.Instance;
        Error = previous.Error;
    }
}

/// <summary>
/// 主页扩展的发现与加载。
///
/// <para>
/// 扩展放在数据目录的 <c>Widgets</c> 下：顶层的每个 dll 算一个扩展，每个子文件夹也算一个
/// （一个文件夹里放主 dll 与它自带的依赖）。只加载用户在「扩展管理」里明确启用的，
/// 加载或运行失败只记错误，不影响启动器本身。
/// </para>
/// </summary>
public static class WidgetPluginCatalog
{
    /// <summary>扩展目录。</summary>
    public static string WidgetsDirectory => Path.Combine(Paths.Data, "Widgets");

    /// <summary>扩展专属的可写目录（放缓存用）。刻意不与 dll 混在一起，方便用户直接删 dll。</summary>
    public static string StorageDirectoryOf(string key) => Path.Combine(Paths.Data, "WidgetStorage", key);

    private static readonly List<WidgetPluginEntry> Items = [];

    public static IReadOnlyList<WidgetPluginEntry> All => Items;

    /// <summary>当前已启用的扩展。</summary>
    public static IReadOnlyList<WidgetPluginEntry> Enabled =>
        Items.Where(item => item.Enabled && item.IsLoaded).ToList();

    public static void EnsureDirectory()
    {
        try
        {
            Directory.CreateDirectory(WidgetsDirectory);
        }
        catch (Exception ex)
        {
            Log.Warn($"创建扩展目录失败：{ex.Message}");
        }
    }

    /// <summary>重新扫描扩展目录。已经加载的实例会被保留，删掉的扩展从列表里消失。</summary>
    public static void Refresh()
    {
        EnsureDirectory();

        var found = new List<WidgetPluginEntry>();

        try
        {
            foreach (var file in Directory.GetFiles(WidgetsDirectory, "*.dll"))
            {
                if (Path.GetFileName(file).StartsWith('.')) continue;

                found.Add(new WidgetPluginEntry
                {
                    Key = Path.GetFileNameWithoutExtension(file),
                    Folder = WidgetsDirectory,
                    AssemblyPath = file
                });
            }

            foreach (var folder in Directory.GetDirectories(WidgetsDirectory))
            {
                if (Path.GetFileName(folder).StartsWith('.')) continue;

                // 一个文件夹里可能有主 dll 和它自己的依赖，取名字排最前的那个当入口
                var main = Directory.GetFiles(folder, "*.dll")
                    .Where(file => !Path.GetFileName(file).StartsWith('.'))
                    .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();

                if (main is null) continue;

                found.Add(new WidgetPluginEntry
                {
                    Key = Path.GetFileName(folder),
                    Folder = folder,
                    AssemblyPath = main
                });
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"扫描扩展目录失败：{ex.Message}");
        }

        found.Sort((left, right) => string.Compare(left.Key, right.Key, StringComparison.OrdinalIgnoreCase));

        var enabled = new HashSet<string>(
            SettingsStore.Current.EnabledWidgetPlugins ?? [], StringComparer.OrdinalIgnoreCase);

        foreach (var entry in found)
        {
            entry.Enabled = enabled.Contains(entry.Key);

            // 已经在跑的实例要接过来，否则刷新列表会把运行中的扩展丢掉
            var running = Items.FirstOrDefault(item =>
                string.Equals(item.Key, entry.Key, StringComparison.OrdinalIgnoreCase));

            if (running is { IsLoaded: true }) entry.Restore(running);
        }

        Items.Clear();
        Items.AddRange(found);

        Log.Info($"扩展目录扫描完成：{found.Count} 个，已启用 {found.Count(entry => entry.Enabled)} 个");
    }

    /// <summary>启动时加载用户启用过的扩展。</summary>
    public static void LoadEnabled()
    {
        Refresh();

        foreach (var entry in Items.Where(item => item.Enabled))
            Load(entry, out _);
    }

    /// <summary>加载一个扩展。</summary>
    public static bool Load(WidgetPluginEntry entry, out string? error)
    {
        error = null;

        if (entry.IsLoaded) return true;

        try
        {
            var context = new PluginLoadContext(entry.Folder);
            var assembly = context.LoadFromAssemblyPath(entry.AssemblyPath);

            var type = assembly.GetTypes().FirstOrDefault(candidate =>
                typeof(IHomepageWidgetPlugin).IsAssignableFrom(candidate) &&
                candidate is { IsAbstract: false, IsInterface: false });

            if (type is null)
            {
                context.Unload();
                entry.Error = error = "这个 dll 里没有实现 IHomepageWidgetPlugin 的类";
                return false;
            }

            if (Activator.CreateInstance(type) is not IHomepageWidgetPlugin plugin)
            {
                context.Unload();
                entry.Error = error = $"{type.Name} 无法创建：需要有一个无参构造函数";
                return false;
            }

            // 校验插件自己声明的 id：要作为小组件 id 参与排序与显隐，不能是空的
            if (string.IsNullOrWhiteSpace(plugin.Id))
            {
                context.Unload();
                entry.Error = error = "插件没有声明 Id";
                return false;
            }

            entry.Attach(context, plugin);

            Log.Info($"已加载扩展「{plugin.Title}」（{plugin.Id}，{Path.GetFileName(entry.AssemblyPath)}）");
            return true;
        }
        catch (Exception ex)
        {
            entry.Error = error = ex.Message;
            Log.Warn($"扩展加载失败 {entry.Key}：{ex.Message}");
            return false;
        }
    }

    /// <summary>启用并立即加载；加载失败不会写进设置。</summary>
    public static bool Enable(WidgetPluginEntry entry, out string? error)
    {
        var loaded = Load(entry, out error);

        entry.Enabled = loaded;
        Persist();

        return loaded;
    }

    /// <summary>停用并卸载。</summary>
    public static void Disable(WidgetPluginEntry entry)
    {
        entry.Detach();
        entry.Enabled = false;
        Persist();
    }

    private static void Persist()
    {
        try
        {
            SettingsStore.Current.EnabledWidgetPlugins = Items
                .Where(item => item.Enabled)
                .Select(item => item.Key)
                .ToList();

            SettingsStore.Save();
        }
        catch (Exception ex)
        {
            Log.Warn($"保存扩展启用状态失败：{ex.Message}");
        }
    }
}

/// <summary>
/// 每个扩展一个独立且可回收的加载上下文。
/// 宿主已有的程序集（尤其是 Api 契约）一律用宿主那一份，其余从扩展自己的目录找；
/// 这样停用扩展后能真正卸载，dll 文件随之解锁，可以被替换或删除。
/// </summary>
internal sealed class PluginLoadContext(string folder) : AssemblyLoadContext(isCollectible: true)
{
    private readonly string _folder = folder;

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is not { } name) return null;

        // 契约程序集必须与宿主共用同一份，否则插件里的接口类型与宿主不是同一个，转换会失败
        if (string.Equals(name, "StardewLauncher.Api", StringComparison.OrdinalIgnoreCase)) return null;

        // 宿主已经加载过的程序集同样用宿主那份，避免出现两份同名类型
        if (Default.Assemblies.Any(loaded =>
                string.Equals(loaded.GetName().Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var candidate = Path.Combine(_folder, name + ".dll");
        return File.Exists(candidate) ? LoadFromAssemblyPath(candidate) : null;
    }
}
