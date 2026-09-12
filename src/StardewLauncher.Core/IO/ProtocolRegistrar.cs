using System.Diagnostics;
using Microsoft.Win32;
using StardewLauncher.Core.Logging;

namespace StardewLauncher.Core.IO;

/// <summary>
/// 注册 / 注销 <c>nxm://</c> 协议处理器。
/// 只写 <c>HKEY_CURRENT_USER\Software\Classes</c>，不需要管理员权限，随时可以关闭。
/// </summary>
public static class ProtocolRegistrar
{
    private const string ProtocolName = "nxm";
    private const string ProtocolKeyPath = @"Software\Classes\nxm";
    private const string CommandKeyPath = @"Software\Classes\nxm\shell\open\command";

    /// <summary>当前程序是否已注册为 nxm:// 处理器。</summary>
    public static bool IsRegistered()
    {
        try
        {
            var command = RegisteredCommand;
            if (string.IsNullOrWhiteSpace(command)) return false;

            var registeredExe = ExtractExecutablePath(command);
            var currentExe = ResolveExecutablePath();

            if (string.IsNullOrWhiteSpace(registeredExe) || string.IsNullOrWhiteSpace(currentExe)) return false;

            return string.Equals(Path.GetFullPath(registeredExe), Path.GetFullPath(currentExe),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Log.Warn($"读取 nxm 协议注册状态失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>已注册时指向的命令行（用于诊断显示）；未注册返回 null。</summary>
    public static string? RegisteredCommand
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(CommandKeyPath);
                return key?.GetValue(null) as string;
            }
            catch (Exception ex)
            {
                Log.Warn($"读取 nxm 协议命令行失败：{ex.Message}");
                return null;
            }
        }
    }

    /// <summary>注册 nxm:// 到当前可执行文件。失败返回 false 并把原因写进 error。</summary>
    public static bool TryRegister(out string? error)
    {
        error = null;

        var exe = ResolveExecutablePath();
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
        {
            error = "无法确定当前可执行文件路径";
            Log.Warn($"注册 nxm 协议失败：{error}");
            return false;
        }

        try
        {
            using (var protocolKey = Registry.CurrentUser.CreateSubKey(ProtocolKeyPath))
            {
                if (protocolKey is null)
                {
                    error = "无法创建注册表项 HKCU\\Software\\Classes\\nxm";
                    Log.Warn($"注册 nxm 协议失败：{error}");
                    return false;
                }

                protocolKey.SetValue(null, "URL:NXM Protocol");
                // 空值也必须存在，Windows 才会把它当成 URL 协议
                protocolKey.SetValue("URL Protocol", "");
            }

            using var commandKey = Registry.CurrentUser.CreateSubKey(CommandKeyPath);
            if (commandKey is null)
            {
                error = "无法创建注册表项 HKCU\\Software\\Classes\\nxm\\shell\\open\\command";
                Log.Warn($"注册 nxm 协议失败：{error}");
                return false;
            }

            commandKey.SetValue(null, BuildCommand(exe));

            Log.Info($"已注册 nxm:// 协议处理器，指向：{exe}");
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Log.Warn($"注册 nxm 协议失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>注销 nxm://（删除整个子树）。</summary>
    public static bool TryUnregister(out string? error)
    {
        error = null;

        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(ProtocolKeyPath, throwOnMissingSubKey: false);
            Log.Info("已注销 nxm:// 协议处理器");
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Log.Warn($"注销 nxm 协议失败：{ex.Message}");
            return false;
        }
    }

    private static string BuildCommand(string exePath) => $"\"{exePath}\" \"%1\"";

    /// <summary>从「"C:\...\App.exe" "%1"」里取出可执行文件路径。</summary>
    private static string? ExtractExecutablePath(string command)
    {
        var text = command.Trim();
        if (text.Length == 0) return null;

        if (text[0] == '"')
        {
            var end = text.IndexOf('"', 1);
            return end > 1 ? text[1..end] : null;
        }

        var space = text.IndexOf(' ');
        return space > 0 ? text[..space] : text;
    }

    /// <summary>单文件发布时 Assembly.Location 为空，因此优先用 ProcessPath。</summary>
    private static string? ResolveExecutablePath()
    {
        var path = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(path)) return path;

        try
        {
            return Process.GetCurrentProcess().MainModule?.FileName;
        }
        catch (Exception ex)
        {
            Log.Warn($"获取当前进程路径失败：{ex.Message}");
            return null;
        }
    }
}
