using System.Diagnostics;
using System.IO;
using StardewLauncher.Core.Games;
using StardewLauncher.Core.Logging;

namespace StardewLauncher.Core.Launch;

/// <summary>一次启动请求。</summary>
/// <param name="GameDirectory">游戏根目录。</param>
/// <param name="UseSmapi">是否经 SMAPI 启动。</param>
/// <param name="ModsPathOverride">非空时通过环境变量 SMAPI_MODS_PATH 指定 Mods 目录。</param>
public sealed record LaunchRequest(string GameDirectory, bool UseSmapi = true, string? ModsPathOverride = null);

/// <summary>游戏启动入口。只负责把进程拉起来，实例的时间记录由调用方处理。</summary>
public static class GameLauncher
{
    /// <summary>按请求启动游戏。失败时返回 false 并给出可读原因，不抛异常。</summary>
    public static bool TryLaunch(LaunchRequest request, out GameProcessSession? session, out string? error)
    {
        session = null;
        error = null;

        if (request is null)
        {
            error = "启动请求为空";
            return false;
        }

        var directory = request.GameDirectory;
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            error = $"游戏目录不存在：{directory}";
            return false;
        }

        string executable;
        if (request.UseSmapi)
        {
            executable = Path.Combine(directory, "StardewModdingAPI.exe");
            if (!File.Exists(executable))
            {
                error = $"未找到 {Path.GetFileName(executable)}，游戏目录可能尚未安装 SMAPI：{directory}";
                return false;
            }
        }
        else
        {
            var found = GameLocator.FindExecutable(directory);
            if (found is null)
            {
                error = $"未找到游戏主程序：{directory}";
                return false;
            }

            executable = found;
        }

        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = false,
            // SMAPI 的日志含中文，不指定 UTF-8 会乱码
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };

        if (!string.IsNullOrWhiteSpace(request.ModsPathOverride))
            startInfo.Environment["SMAPI_MODS_PATH"] = request.ModsPathOverride;

        try
        {
            var process = Process.Start(startInfo);
            if (process is null)
            {
                error = "进程启动失败：Process.Start 返回空";
                return false;
            }

            session = new GameProcessSession(process);
            Log.Info($"已启动游戏：{executable}");
            return true;
        }
        catch (Exception ex)
        {
            error = $"启动游戏失败：{ex.Message}";
            Log.Error($"启动游戏失败：{executable}", ex);
            return false;
        }
    }
}
