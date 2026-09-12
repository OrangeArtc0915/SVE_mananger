using System.Diagnostics;
using System.IO;
using StardewLauncher.Core.Logging;

namespace StardewLauncher.Core.IO;

/// <summary>调用系统外壳打开网址或目录。</summary>
public static class ShellHelper
{
    public static void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"打开链接失败 {url}：{ex.Message}");
        }
    }

    public static void OpenFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            if (!Directory.Exists(path))
            {
                Log.Warn($"目录不存在，无法打开：{path}");
                return;
            }

            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"打开目录失败 {path}：{ex.Message}");
        }
    }
}
