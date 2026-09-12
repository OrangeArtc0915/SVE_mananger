using System.Diagnostics;
using System.IO;

namespace StardewLauncher.Core.Games;

/// <summary>一份星露谷安装的只读信息快照。</summary>
public sealed class StardewInstall
{
    private static readonly string[] GameAssemblyNames =
    [
        "Stardew Valley.dll",
        "StardewValley.dll"
    ];

    private StardewInstall(string directory, string executable, string? smapiExecutable,
        string? gameVersion, string? smapiVersion)
    {
        Directory = directory;
        Executable = executable;
        SmapiExecutable = smapiExecutable;
        GameVersion = gameVersion;
        SmapiVersion = smapiVersion;
    }

    /// <summary>游戏根目录。</summary>
    public string Directory { get; }

    /// <summary>游戏主程序完整路径。</summary>
    public string Executable { get; }

    /// <summary>SMAPI 主程序完整路径，未安装则为 null。</summary>
    public string? SmapiExecutable { get; }

    /// <summary>游戏版本，读不到则为 null。</summary>
    public string? GameVersion { get; }

    /// <summary>SMAPI 版本，未安装或读不到则为 null。</summary>
    public string? SmapiVersion { get; }

    public bool HasSmapi => SmapiExecutable is not null;

    /// <summary>Mods 目录（默认位置，固定用游戏根目录下的 Mods）。</summary>
    public string ModsDirectory => Path.Combine(Directory, "Mods");

    /// <summary>SMAPI 内部目录，存在即说明 SMAPI 装好了。</summary>
    public string SmapiInternalDirectory => Path.Combine(Directory, "smapi-internal");

    /// <summary>校验并在有效时构造信息快照。</summary>
    public static bool TryCreate(string? directory, out StardewInstall? install)
    {
        install = null;
        if (string.IsNullOrWhiteSpace(directory) || !System.IO.Directory.Exists(directory)) return false;

        var executable = GameLocator.FindExecutable(directory);
        if (executable is null) return false;

        var smapiExecutable = Path.Combine(directory, "StardewModdingAPI.exe");
        var hasSmapi = File.Exists(smapiExecutable);

        install = new StardewInstall(
            GameLocator.NormalizePath(directory),
            executable,
            hasSmapi ? smapiExecutable : null,
            ReadVersion(FindGameAssembly(directory)),
            ReadVersion(hasSmapi ? Path.Combine(directory, "StardewModdingAPI.dll") : null));

        return true;
    }

    private static string? FindGameAssembly(string directory)
        => GameAssemblyNames
            .Select(name => Path.Combine(directory, name))
            .FirstOrDefault(File.Exists);

    /// <summary>读取文件版本并截断为三段，例如 4.5.1.0 → 4.5.1。</summary>
    private static string? ReadVersion(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return null;

        try
        {
            var text = FileVersionInfo.GetVersionInfo(filePath).FileVersion;
            if (string.IsNullOrWhiteSpace(text)) return null;

            var parts = text.Split('.');
            var take = Math.Min(3, parts.Length);
            return string.Join('.', parts.Take(take));
        }
        catch
        {
            return null;
        }
    }
}
