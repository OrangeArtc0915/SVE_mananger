namespace StardewLauncher.Core.Launch;

/// <summary>游戏日志等级，独立于启动器自身的日志等级。</summary>
public enum GameLogLevel
{
    Trace,
    Debug,
    Info,
    Warn,
    Error,
    Fatal
}

/// <summary>游戏进程的一行输出。</summary>
public sealed record GameLogLine(DateTime Time, GameLogLevel Level, string Text);
