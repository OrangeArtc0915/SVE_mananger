namespace StardewLauncher.Api;

/// <summary>
/// 扩展能拿到的东西。刻意只给只读数据与取数工具：
/// 插件不能启动游戏、不能改 Mod、不能动启动器设置。
/// </summary>
public interface IWidgetContext
{
    /// <summary>启动器版本号，例如 <c>1.5.0</c>。可用于在界面上标注兼容性。</summary>
    string LauncherVersion { get; }

    /// <summary>
    /// 该扩展专属的可写目录（已创建），适合放联网缓存之类的东西。
    /// 用户卸载扩展时这个目录不会自动删除。
    /// </summary>
    string StorageDirectory { get; }

    /// <summary>写一条日志，会进启动器的「运行日志」页，方便排查。</summary>
    void Log(string message);

    /// <summary>
    /// GET 一个网址并返回文本内容。失败返回 <c>null</c>，不抛异常；
    /// 内部复用启动器自己的下载器（带超时、重试与 User-Agent）。
    /// </summary>
    Task<string?> HttpGetAsync(string url, CancellationToken cancellationToken = default);

    /// <summary>
    /// 取一份启动器数据快照（实例、当前实例的 Mod、游戏存档）。
    /// 扫描 Mods 与存档目录有磁盘开销，所以不要放在循环里反复调用。
    /// </summary>
    LauncherSnapshot GetSnapshot();
}
