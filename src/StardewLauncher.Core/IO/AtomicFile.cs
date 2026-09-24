using System.IO;

namespace StardewLauncher.Core.IO;

/// <summary>
/// 原子写文本文件：先写同目录的临时文件，落盘后再替换目标文件。
///
/// <para>
/// 直接覆盖写目标文件的话，进程在写入途中断掉（崩溃、被任务管理器结束、断电）会留下一个
/// 截断的半个文件：下次读到的就是"配置损坏"。对设置、实例、标签这类文件，
/// 这等于用户的配置全丢，所以这里一律走临时文件 + 替换。
/// </para>
///
/// <para>
/// 临时文件名固定（目标名 + .tmp），所以即使上一次中断留下了临时文件，这次也会直接覆盖，
/// 不会越攒越多。替换用 File.Move 覆盖模式，同盘之下是原子的。
/// </para>
/// </summary>
public static class AtomicFile
{
    /// <summary>把文本写入文件。失败会抛异常，由调用方按各自的语义处理。</summary>
    public static void WriteAllText(string path, string contents)
    {
        var temp = path + ".tmp";

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(contents);
            writer.Flush();

            // 先把缓冲刷到磁盘再替换，避免"文件已经换过去了、内容还在缓存里"
            stream.Flush(true);
        }

        File.Move(temp, path, overwrite: true);
    }
}
