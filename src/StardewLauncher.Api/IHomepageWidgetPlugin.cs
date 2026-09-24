namespace StardewLauncher.Api;

/// <summary>
/// 主页小组件扩展。实现本接口、编译成 dll、放进启动器数据目录下的 <c>Widgets</c> 文件夹，
/// 再到主页「自定义」菜单的「扩展管理」里启用即可。
///
/// <para>
/// 插件只负责两件事：取数据、描述要显示什么。卡片外观由启动器按统一样式渲染，
/// 所以插件不需要引用 WPF，也不需要关心配色与圆角。
/// </para>
///
/// <para>
/// 插件的能力是刻意收窄的：只能产出文字与数字，拿不到任何写入口 ——
/// 不能写文件、不能写日志、不能启动游戏或改启动器设置，产出的行也没有可点击的东西。
/// 详见 <see cref="IWidgetContext"/> 与 <see cref="WidgetItem"/>。
/// </para>
/// </summary>
/// <example>
/// <code>
/// public sealed class PlayTimeWidget : IHomepageWidgetPlugin
/// {
///     public string Id => "sample-playtime";
///     public string Title => "游玩时长";
///
///     public Task&lt;WidgetContent&gt; RefreshAsync(IWidgetContext context, CancellationToken token)
///     {
///         var snapshot = context.GetSnapshot();
///         var current = snapshot.CurrentInstance;
///
///         var content = new WidgetContent
///         {
///             Subtitle = current?.Name ?? "还没有实例",
///             EmptyText = "先在「游戏实例」里建一个实例",
///             Items = current is null ? [] : [WidgetItem.KeyValue("累计游玩", FormatSeconds(current.TotalPlaySeconds))]
///         };
///
///         return Task.FromResult(content);
///     }
/// }
/// </code>
/// </example>
public interface IHomepageWidgetPlugin
{
    /// <summary>
    /// 唯一标识：建议只用小写字母、数字和连字符，例如 <c>mmm-playtime</c>。
    /// 用户排序、隐藏以及配置里都存这个 id，发布后不要再改，否则用户的布局会丢。
    /// </summary>
    string Id { get; }

    /// <summary>卡片标题，显示在卡片左上角。</summary>
    string Title { get; }

    /// <summary>
    /// 取一次要显示的内容。进入主页时调用，用户点卡片右上角的刷新按钮时也会调用。
    /// 联网或耗时操作请尊重传入的 <paramref name="cancellationToken"/>：
    /// 超时会被宿主取消，抛出的异常会被捕获并显示在卡片上，不会影响启动器本身。
    /// </summary>
    Task<WidgetContent> RefreshAsync(IWidgetContext context, CancellationToken cancellationToken);
}
