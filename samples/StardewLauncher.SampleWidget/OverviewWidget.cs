using StardewLauncher.Api;

namespace StardewLauncher.SampleWidget;

/// <summary>
/// 示例扩展：把启动器已有的数据汇总成一张卡片，用来当写扩展的模板。
///
/// <para>写自己的扩展只要三步：</para>
/// <list type="number">
///   <item>改 <see cref="Id"/>（唯一、发布后别改）与 <see cref="Title"/>；</item>
///   <item>在 <see cref="RefreshAsync"/> 里取数据，返回要显示的内容；</item>
///   <item>编译出的 dll 丢进启动器数据目录的 <c>Widgets</c> 文件夹，到主页「自定义 → 扩展管理」里启用。</item>
/// </list>
/// </summary>
public sealed class OverviewWidget : IHomepageWidgetPlugin
{
    public string Id => "sample-overview";

    public string Title => "启动器概览";

    public Task<WidgetContent> RefreshAsync(IWidgetContext context, CancellationToken cancellationToken)
    {
        // 一次取全部需要的数据；接口内部有短短几秒的缓存，多个扩展同时刷新不会重复扫盘
        var snapshot = context.GetSnapshot();
        var current = snapshot.CurrentInstance;

        var items = new List<WidgetItem>
        {
            WidgetItem.KeyValue("实例数量", $"{snapshot.Instances.Count} 个"),
            WidgetItem.KeyValue("当前实例", current?.Name ?? "未选择"),
            WidgetItem.KeyValue("存档数量", $"{snapshot.Saves.Count} 个")
        };

        if (current is not null && !current.IsVanilla && snapshot.TotalModCount > 0)
        {
            var ratio = (double)snapshot.EnabledModCount / snapshot.TotalModCount;

            items.Add(WidgetItem.Progress("已启用 Mod", ratio,
                $"{snapshot.EnabledModCount} / {snapshot.TotalModCount}"));
        }

        if (snapshot.Saves.Count > 0)
        {
            var latest = snapshot.Saves[0];

            items.Add(WidgetItem.Divider());
            items.Add(WidgetItem.KeyValue("最近存档", latest.FarmName));
            items.Add(WidgetItem.KeyValue("游戏内日期", latest.GameDateText));
            items.Add(WidgetItem.Link("打开存档目录", latest.Directory, "lucide/folder-open"));
        }

        items.Add(WidgetItem.Divider());
        items.Add(WidgetItem.Text(snapshot.GameRunning ? "游戏正在运行中" : "游戏未运行"));

        return Task.FromResult(new WidgetContent
        {
            Subtitle = $"启动器 {snapshot.LauncherVersion}",
            Items = items,
            Footnote = "这是随启动器附带的示例扩展，代码见 samples 目录"
        });
    }
}
