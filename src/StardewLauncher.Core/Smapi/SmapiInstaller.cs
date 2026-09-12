using System.Diagnostics;
using System.IO;
using StardewLauncher.Core.Games;
using StardewLauncher.Core.Logging;

namespace StardewLauncher.Core.Smapi;

/// <summary>一次静默安装的最终结果。</summary>
public sealed record SmapiInstallResult(bool Success, string Message);

/// <summary>
/// SMAPI 安装器的定位与静默安装。两条实测约束决定了这里的实现方式：
/// 1) 官方安装器内部会无条件调用 <c>Console.Clear()</c>，一旦 stdout 被重定向就会抛「句柄无效」并崩溃，
///    因此启动进程时不能设置任何重定向，且必须保留控制台窗口（CreateNoWindow = false）；
/// 2) 安装失败时安装器仍返回退出码 0，所以只能用文件系统（StardewModdingAPI.exe + smapi-internal）判断成败。
/// </summary>
public static class SmapiInstaller
{
    private const string InstallerRelativePath = @"internal\windows\SMAPI.Installer.exe";
    private const string InstallScriptName = "install on Windows.bat";

    /// <summary>
    /// 在解压根目录本身及其第一层子目录里查找安装器（部分压缩包会多套一层目录），
    /// 并要求 <c>install on Windows.bat</c> 与安装器同时存在。
    /// </summary>
    public static bool TryLocateInstaller(string packageRoot, out SmapiInstallerPackage? package, out string? error)
    {
        package = null;
        error = null;

        if (string.IsNullOrWhiteSpace(packageRoot) || !Directory.Exists(packageRoot))
        {
            error = $"解压目录不存在：{packageRoot}";
            return false;
        }

        var roots = new List<string> { packageRoot };

        try
        {
            roots.AddRange(Directory.EnumerateDirectories(packageRoot));
        }
        catch (Exception ex)
        {
            error = $"读取解压目录失败：{ex.Message}";
            return false;
        }

        foreach (var root in roots)
        {
            var installerExe = Path.Combine(root, InstallerRelativePath);
            if (!File.Exists(installerExe)) continue;

            // 安装器在 internal\windows 下，上溯两级才是安装包根目录（bat 脚本所在处）
            var windowsDirectory = Path.GetDirectoryName(installerExe);
            if (windowsDirectory is null) continue;

            var resolvedRoot = Path.GetFullPath(Path.Combine(windowsDirectory, "..", ".."));
            var installScript = Path.Combine(resolvedRoot, InstallScriptName);

            if (!File.Exists(installScript))
            {
                error = $"缺少安装脚本「{InstallScriptName}」：{installScript}";
                return false;
            }

            package = new SmapiInstallerPackage(resolvedRoot, installerExe, installScript);
            Log.Info($"已识别 SMAPI 安装器：{installerExe}；安装脚本：{installScript}");
            return true;
        }

        error = $"没有在 {packageRoot} 里找到 {InstallerRelativePath}";
        return false;
    }

    /// <summary>静默安装到指定游戏目录，装完后用文件系统复核结果。</summary>
    public static async Task<SmapiInstallResult> InstallSilentlyAsync(string gameDir,
        SmapiInstallerPackage package, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir))
            return new SmapiInstallResult(false, $"游戏目录不存在：{gameDir}");

        if (GameLocator.FindExecutable(gameDir) is null)
            return new SmapiInstallResult(false, "该目录下没有找到 Stardew Valley.exe / StardewValley.exe");

        if (!File.Exists(package.InstallerExe))
            return new SmapiInstallResult(false, $"安装器不存在：{package.InstallerExe}");

        // 绝不能重定向任何流：安装器内部的 Console.Clear() 在重定向后会抛「句柄无效」
        var startInfo = new ProcessStartInfo(package.InstallerExe)
        {
            WorkingDirectory = package.PackageRoot,
            UseShellExecute = false,
            CreateNoWindow = false
        };

        startInfo.ArgumentList.Add("--install");
        startInfo.ArgumentList.Add("--game-path");
        startInfo.ArgumentList.Add(gameDir);
        startInfo.ArgumentList.Add("--no-prompt");

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null) return new SmapiInstallResult(false, "无法启动 SMAPI 安装器");

            await process.WaitForExitAsync(token);
        }
        catch (OperationCanceledException)
        {
            return new SmapiInstallResult(false, "安装已取消");
        }
        catch (Exception ex)
        {
            Log.Error($"启动 SMAPI 安装器失败：{package.InstallerExe}", ex);
            return new SmapiInstallResult(false, $"启动安装器失败：{ex.Message}");
        }

        // 安装器失败时也返回退出码 0，只能查文件系统
        var smapiExe = Path.Combine(gameDir, "StardewModdingAPI.exe");
        var smapiInternal = Path.Combine(gameDir, "smapi-internal");

        if (File.Exists(smapiExe) && Directory.Exists(smapiInternal))
            return new SmapiInstallResult(true, "SMAPI 安装完成");

        var missing = new List<string>();
        if (!File.Exists(smapiExe)) missing.Add("StardewModdingAPI.exe");
        if (!Directory.Exists(smapiInternal)) missing.Add("smapi-internal 目录");

        return new SmapiInstallResult(false, $"安装未生效，游戏目录下缺少：{string.Join("、", missing)}");
    }

    /// <summary>打开 bat 安装脚本，让用户在控制台窗口里自己完成交互安装。</summary>
    public static void LaunchManualInstaller(SmapiInstallerPackage package)
    {
        try
        {
            Process.Start(new ProcessStartInfo(package.InstallScript) { UseShellExecute = true });
            Log.Info($"已打开手动安装脚本：{package.InstallScript}");
        }
        catch (Exception ex)
        {
            Log.Warn($"打开手动安装脚本失败：{package.InstallScript}（{ex.Message}）");
        }
    }
}
