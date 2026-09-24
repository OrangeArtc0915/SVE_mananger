namespace StardewLauncher.Api;

/// <summary>
/// 扩展能拿到的东西。刻意只给只读数据与取数工具：
/// 扩展既拿不到任何写文件的入口，也没有写日志的入口，更不能启动游戏、改 Mod 或动启动器设置 ——
/// 它唯一能做的事就是把「要显示什么文字与数字」描述出来，交给启动器渲染。
///
/// <para>
/// 需要记录下来给人看的东西，请放进返回的内容里（正文 / 标签取值 / 备注），
/// 或者直接抛异常 —— 启动器会捕获异常、写进「运行日志」并显示在卡片上。
/// </para>
/// </summary>
public interface IWidgetContext
{
    /// <summary>启动器版本号，例如 <c>1.6.1</c>。可用于在界面上标注兼容性。</summary>
    string LauncherVersion { get; }

    /// <summary>
    /// GET 一个网址并返回文本内容。失败返回 <c>null</c>，不抛异常；
    /// 内部复用启动器自己的下载器（带超时、重试与 User-Agent）。
    /// 只读：请求方式固定为 GET，无法向网上写入任何数据。
    /// </summary>
    Task<string?> HttpGetAsync(string url, CancellationToken cancellationToken = default);

    /// <summary>
    /// 取一份启动器数据快照（实例、当前实例的 Mod、游戏存档）。
    /// 扫描 Mods 与存档目录有磁盘开销，所以不要放在循环里反复调用。
    /// </summary>
    LauncherSnapshot GetSnapshot();
}
