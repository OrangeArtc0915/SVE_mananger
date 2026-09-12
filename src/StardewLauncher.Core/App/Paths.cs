using System.IO;

namespace StardewLauncher.Core.App;

/// <summary>
/// 工具自身的数据目录。划分依据：可随程序带走 / 可随时重建 / 可随时删除。
/// 可通过环境变量 STARDEWLAUNCHER_DATA 重定向整个数据目录，便于便携部署与测试。
/// </summary>
public static class Paths
{
    public const string DataEnvVar = "STARDEWLAUNCHER_DATA";

    public static string ExecutableDirectory { get; private set; } = AppContext.BaseDirectory;

    public static string Data { get; private set; } = string.Empty;

    public static string Instances { get; private set; } = string.Empty;

    public static string Cache { get; private set; } = string.Empty;

    public static string Log { get; private set; } = string.Empty;

    public static string Temp { get; private set; } = string.Empty;

    public static string Downloads { get; private set; } = string.Empty;

    public static string SettingsFile => Path.Combine(Data, "Settings.json");

    public static bool IsInitialized { get; private set; }

    public static void Init()
    {
        if (IsInitialized) return;

        var custom = Environment.GetEnvironmentVariable(DataEnvVar);
        Data = string.IsNullOrWhiteSpace(custom)
            ? Path.Combine(ExecutableDirectory, "Data")
            : Path.GetFullPath(custom);

        Instances = Path.Combine(Data, "instances");
        Cache = Path.Combine(Data, "Cache");
        Log = Path.Combine(Data, "Log");
        Temp = Path.Combine(Path.GetTempPath(), "StardewLauncher");
        Downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StardewLauncher", "Downloads");

        foreach (var dir in new[] { Data, Instances, Cache, Log, Temp, Downloads })
        {
            try { Directory.CreateDirectory(dir); }
            catch { /* 目录不可用时由上层在使用点报错，这里不阻断启动 */ }
        }

        IsInitialized = true;
    }

    public static string InstanceFile(string id) => Path.Combine(Instances, id + ".json");

    public static string InstanceModsDir(string id) => Path.Combine(Instances, id, "Mods");

    public static string InstanceSelectedModsDir(string id) => Path.Combine(Instances, id, "Selected Mods");

    public static string InstanceSavesDir(string id) => Path.Combine(Instances, id, "Saves");
}
