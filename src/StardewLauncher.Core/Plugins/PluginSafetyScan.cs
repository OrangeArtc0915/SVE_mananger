using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace StardewLauncher.Core.Plugins;

/// <summary>
/// 扩展加载前的安全检查：只读 dll 的元数据，<b>不执行</b>扩展的任何代码。
///
/// <para>
/// 契约本身已经不给写入口，但扩展是 .NET 程序集，理论上仍能绕过接口直接调 <c>System.IO</c>。
/// 这道闸就是把那条路堵上：dll 里只要出现"能碰电脑"的类型引用，就直接拒绝加载，
/// 并在「扩展管理」里写明它用了什么、为什么不行。
/// </para>
///
/// <para>
/// 拦的是这五类：读写文件或目录、读写注册表、调用非托管代码（P/Invoke）、动态生成并执行代码、
/// 启动或操控别的进程。检查是静态的，判不了"读了但没用"这种语义，所以按类型引用一票否决 ——
/// 宁可拒绝一个干净的扩展，也不放一个能动手的进来。
/// </para>
/// </summary>
internal static class PluginSafetyScan
{
    /// <summary>
    /// <c>System.IO</c> 里这些只是纯内存操作或异常类型，不碰磁盘，放行。
    /// 其余的（File / Directory / FileStream / StreamWriter …）一律拦。
    /// </summary>
    private static readonly HashSet<string> HarmlessIoTypes = new(StringComparer.Ordinal)
    {
        "Path", "Stream", "MemoryStream", "SeekOrigin",
        "FileMode", "FileAccess", "FileShare",
        "IOException", "InvalidDataException", "EndOfStreamException",
        "FileNotFoundException", "DirectoryNotFoundException"
    };

    /// <summary>
    /// 扫一组 dll。任何一个命中就返回 false，并把原因写进 <paramref name="violation"/>。
    /// </summary>
    public static bool TryScan(IReadOnlyList<string> assemblyPaths, out string? violation)
    {
        violation = null;

        foreach (var path in assemblyPaths)
        {
            var reason = Describe(path);
            if (reason is null) continue;

            violation = $"{Path.GetFileName(path)} 用到了 {reason}";
            return false;
        }

        return true;
    }

    private static string? Describe(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);

            if (!pe.HasMetadata) return "不是 .NET 程序集";

            var reader = pe.GetMetadataReader();

            foreach (var handle in reader.TypeReferences)
            {
                var reference = reader.GetTypeReference(handle);
                var ns = reader.GetString(reference.Namespace);
                var name = reader.GetString(reference.Name);

                if (Forbid(ns, name) is { } reason) return $"{ns}.{name}（{reason}）";
            }

            return null;
        }
        catch (Exception ex)
        {
            // 连元数据都读不出来，就更不该让它在启动器进程里跑起来
            return $"读不出元数据的文件（{ex.Message}）";
        }
    }

    private static string? Forbid(string ns, string name)
    {
        if (ns == "System.IO") return HarmlessIoTypes.Contains(name) ? null : "读写电脑上的文件或目录";

        // System.IO.Compression / Pipes / MemoryMappedFiles / Packaging … 同样能落盘
        if (ns.StartsWith("System.IO.", StringComparison.Ordinal)) return "读写电脑上的文件或目录";

        if (ns.StartsWith("Microsoft.Win32", StringComparison.Ordinal)) return "读写注册表";

        if (ns.StartsWith("System.Runtime.InteropServices", StringComparison.Ordinal)) return "调用非托管代码";

        if (ns.StartsWith("System.Reflection.Emit", StringComparison.Ordinal)) return "动态生成并执行代码";

        if (ns == "System.Diagnostics" && name is "Process" or "ProcessStartInfo" or "ProcessThread")
            return "启动或操控别的进程";

        return null;
    }
}
