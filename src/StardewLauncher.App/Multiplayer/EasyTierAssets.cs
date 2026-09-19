using System.IO;
using System.Reflection;
using StardewLauncher.Core.App;
using StardewLauncher.Core.Logging;

namespace StardewLauncher.App.Multiplayer;

/// <summary>
/// EasyTier 的 exe 与驱动 dll 作为嵌入资源随单文件 exe 分发，首次用到联机时自解压出来。
///
/// 解压到数据目录而不是 exe 同目录：单文件 exe 可能被放在只读位置（如 Program Files），
/// 数据目录才是保证可写的地方；也方便随程序一起搬走、随卸载一起清掉。
/// </summary>
public static class EasyTierAssets
{
    /// <summary>EasyTier 官方 Windows 发行包里的完整文件集。</summary>
    private static readonly string[] FileNames =
    [
        "easytier-core.exe",
        "easytier-cli.exe",
        "wintun.dll",
        "Packet.dll",
        "WinDivert64.sys"
    ];

    private static readonly object Gate = new();

    private static string? _toolDirectory;

    /// <summary>解压目标目录。</summary>
    public static string ToolDirectory => _toolDirectory ??= Path.Combine(Paths.Data, "EasyTier");

    public static string CoreExe => Path.Combine(ToolDirectory, "easytier-core.exe");

    /// <summary>确保组件都已就位。重复调用很廉价：按文件大小比对，一致就跳过。</summary>
    public static void EnsureExtracted()
    {
        lock (Gate)
        {
            System.IO.Directory.CreateDirectory(ToolDirectory);

            var assembly = typeof(EasyTierAssets).Assembly;
            var names = assembly.GetManifestResourceNames();

            foreach (var fileName in FileNames)
            {
                var target = Path.Combine(ToolDirectory, fileName);

                using var source = OpenResource(assembly, names, fileName)
                    ?? throw new FileNotFoundException($"程序内缺少 EasyTier 组件：{fileName}");

                if (IsUpToDate(target, source.Length)) continue;

                Log.Info($"解压 EasyTier 组件 {fileName}（{source.Length / 1024d / 1024:0.0} MB）");
                Extract(source, target);
            }
        }
    }

    private static Stream? OpenResource(Assembly assembly, string[] names, string fileName)
    {
        // 逻辑名形如 StardewLauncher.App.Assets.EasyTier.<文件名>；按后缀匹配，不依赖命名空间写法
        var suffix = $".EasyTier.{fileName}";
        var name = names.FirstOrDefault(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

        return name is null ? null : assembly.GetManifestResourceStream(name);
    }

    private static bool IsUpToDate(string path, long expectedLength)
    {
        try
        {
            return File.Exists(path) && new FileInfo(path).Length == expectedLength;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>先写临时文件再替换：避免中途失败留下半个 exe 被当成可用文件。</summary>
    private static void Extract(Stream source, string target)
    {
        var temp = target + ".tmp";

        using (var file = File.Create(temp)) source.CopyTo(file);

        File.Move(temp, target, overwrite: true);
    }
}
