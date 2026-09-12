using System.IO;
using System.IO.Pipes;
using System.Text;
using StardewLauncher.Core.Logging;

namespace StardewLauncher.App.Nexus;

/// <summary>
/// 单实例所需的进程间通信。用户点 <c>nxm://</c> 时系统会再启动一个本程序进程，
/// 新进程把链接通过命名管道发给已在运行的实例后立即退出，避免每点一次多开一个窗口。
/// 管道名与互斥量名使用同一前缀。
/// </summary>
public static class NxmLinkRelay
{
    /// <summary>单实例互斥量名。</summary>
    public const string MutexName = "StardewLauncher.SingleInstance";

    private const string PipeName = "StardewLauncher.NxmPipe";

    /// <summary>空消息表示「只把已有窗口激活到前台」。</summary>
    public const string ActivateOnly = "";

    /// <summary>
    /// 把消息转发给已在运行的实例。传 null / 空串表示只激活窗口。
    /// 连不上返回 false，绝不抛异常。
    /// </summary>
    public static bool TryForward(string? nxmUri)
    {
        var payload = string.IsNullOrWhiteSpace(nxmUri) ? ActivateOnly : nxmUri.Trim();

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                client.Connect(2000);

                using var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };
                writer.WriteLine(payload);

                Log.Info(attempt == 0
                    ? "已把启动参数转发给运行中的实例"
                    : $"第 {attempt + 1} 次尝试后成功转发启动参数");
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn($"转发启动参数失败（第 {attempt + 1} 次）：{ex.Message}");
                Thread.Sleep(150);
            }
        }

        return false;
    }

    /// <summary>在后台线程循环监听管道，收到消息后回调（回调在后台线程执行）。</summary>
    public static void StartListening(Action<string> onMessage)
    {
        var thread = new Thread(() => ListenLoop(onMessage))
        {
            IsBackground = true,
            Name = "NxmLinkPipe"
        };

        thread.Start();
    }

    private static void ListenLoop(Action<string> onMessage)
    {
        while (true)
        {
            try
            {
                using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.None);

                server.WaitForConnection();

                using var reader = new StreamReader(server, Encoding.UTF8);
                var line = reader.ReadLine();

                onMessage(string.IsNullOrWhiteSpace(line) ? ActivateOnly : line.Trim());
            }
            catch (Exception ex)
            {
                // 管道异常不能让进程崩掉，稍等后重建管道实例
                Log.Warn($"nxm 管道监听异常：{ex.Message}");
                Thread.Sleep(200);
            }
        }
    }
}
