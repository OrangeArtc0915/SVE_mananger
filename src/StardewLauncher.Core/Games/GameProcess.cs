using System.Diagnostics;
using StardewLauncher.Core.Logging;

namespace StardewLauncher.Core.Games;

/// <summary>检测星露谷物语是否正在运行（游戏本体或 SMAPI 启动器）。</summary>
public static class GameProcess
{
    /// <summary>用于判断"游戏是否在跑"的进程名。</summary>
    public static readonly string[] Names = ["StardewModdingAPI", "Stardew Valley"];

    public static bool IsRunning()
    {
        foreach (var name in Names)
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(name);
            }
            catch (Exception ex)
            {
                Log.Warn($"枚举进程 {name} 失败：{ex.Message}");
                continue;
            }

            try
            {
                if (processes.Length > 0) return true;
            }
            finally
            {
                foreach (var process in processes) process.Dispose();
            }
        }

        return false;
    }
}
