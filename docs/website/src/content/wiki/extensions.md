---
title: 写一个主页扩展
description: 用 C# 写一个自己的主页小组件：接口长什么样、能拿到哪些数据、怎么装进启动器。
category: 扩展开发
order: 1
---

主页的小组件是开放的：你可以自己写一个 dll 丢进去，它就会和月历、天气一样出现在主页的小组件区，一样能拖动排序、一样能收起。

## 能做什么、不能做什么

| | |
| --- | --- |
| **可以** | 自己联网取数据、读启动器已有的数据（实例、Mod、存档）、在卡片里显示文字 / 键值 / 进度条 / 链接 / 分隔线 |
| **不可以** | 启动游戏、启停 Mod、改启动器设置 —— 扩展只能"看"，不能"动" |

卡片的外壳、配色、间距、圆角全部由启动器渲染，所以扩展再多也不会冒出另一种风格的卡片；你也不需要引用 WPF。

## 三步上手

1. **写一个类**，实现 `IHomepageWidgetPlugin`；
2. **编译成 dll**（示例工程见启动器仓库的 `samples/StardewLauncher.SampleWidget`，复制它改成自己的即可）；
3. 把 dll 放进扩展目录，然后在主页「自定义 → 扩展管理…」里**手动启用**。

扩展目录的位置可以在「扩展管理」窗口里看到，点「打开目录」直接打开：

```
<启动器数据目录>\Widgets\
```

- 一个 dll = 一个扩展（只会认里面的第一个实现类，要做多个扩展就分成多个 dll）
- 需要带多个文件的，放进一个**子文件夹**，文件夹名就是它的标识

## 接口

```csharp
public interface IHomepageWidgetPlugin
{
    string Id { get; }     // 唯一标识，建议小写字母 + 连字符，例如 mmm-playtime
    string Title { get; }  // 卡片标题

    Task<WidgetContent> RefreshAsync(IWidgetContext context, CancellationToken cancellationToken);
}
```

`Id` 会写进用户的布局配置里，**发布后不要再改**，否则用户的排序和显隐会丢。

`RefreshAsync` 在进入主页时调用、用户点卡片上的刷新按钮时也会调用。它有 15 秒上限，超时会被取消 —— 请尊重传进来的 `cancellationToken`。

## 能拿到什么

`IWidgetContext` 提供的一切：

| 成员 | 说明 |
| --- | --- |
| `GetSnapshot()` | 一份启动器数据快照：实例列表、当前实例、当前实例的 Mod、全部存档摘要。**不要放在循环里反复调**，内部有 3 秒缓存，但仍有磁盘开销 |
| `HttpGetAsync(url)` | GET 一个网址拿文本；失败返回 `null`，不抛异常（复用启动器自己的下载器，带超时与重试） |
| `StorageDirectory` | 该扩展专属的可写目录，适合放联网缓存 |
| `Log(message)` | 写一条日志，会进启动器的「运行日志」页 |
| `LauncherVersion` | 启动器版本号 |

数据快照里比较常用的几项：

| 字段 | 说明 |
| --- | --- |
| `Instances` / `CurrentInstance` | 全部实例 / 当前选中的实例（没有就是 `null`） |
| `CurrentInstance.PlayTimeText` | 累计游玩时长，已经格式化好 |
| `Mods` / `EnabledModCount` | 当前实例扫到的 Mod 列表与启用数量 |
| `Saves[0]` | 最近一次存档，带 `FarmName`、`GameDateText`、`PlayTimeText`、`SizeText` |
| `GameRunning` | 游戏是不是正在运行 |

## 怎么显示

返回一个 `WidgetContent`，里面按顺序放 `WidgetItem`：

```csharp
public Task<WidgetContent> RefreshAsync(IWidgetContext context, CancellationToken token)
{
    var snapshot = context.GetSnapshot();
    var latest = snapshot.Saves.FirstOrDefault();

    return Task.FromResult(new WidgetContent
    {
        Subtitle = $"当前版本 {snapshot.LauncherVersion}",
        Items =
        [
            WidgetItem.KeyValue("存档数量", $"{snapshot.Saves.Count} 个"),
            WidgetItem.Progress("已启用 Mod", snapshot.TotalModCount == 0 ? 0
                : (double)snapshot.EnabledModCount / snapshot.TotalModCount, $"{snapshot.EnabledModCount} / {snapshot.TotalModCount}"),
            latest is null ? WidgetItem.Text("还没有存档") : WidgetItem.KeyValue("最近农场", latest.FarmName),
            WidgetItem.Divider(),
            WidgetItem.Link("打开存档目录", latest?.Directory ?? "", "lucide/folder-open")
        ],
        Footnote = "数据来自启动器本地记录"
    });
}
```

| 行类型 | 效果 |
| --- | --- |
| `WidgetItem.Text(文本)` | 一段会自动换行的正文 |
| `WidgetItem.KeyValue(标签, 取值)` | 左标签右取值的两栏行 |
| `WidgetItem.Progress(标签, 0~1, 文本)` | 一行进度条 |
| `WidgetItem.Link(标签, 地址, 图标)` | 可点击跳转的行，用系统默认程序打开 |
| `WidgetItem.Divider()` | 分隔线 |

图标可以用启动器内置的图标名（`lucide/clock`、`lucide/package` 这类）；写错了只是不显示，不会报错。

## 出错会怎样

启动器按「坏扩展不能拖累别人」来做：

- 加载失败（不是扩展、没有无参构造函数、Id 为空）：在「扩展管理」里显示失败原因，不启用；
- 运行时抛异常或超时：**只在这张卡片里显示错误**，其它卡片和启动器本身照常；
- 扩展是第三方代码，会以当前用户权限运行 —— **只启用你信任的扩展**，这也是默认不自动加载、要手动启用的原因。

## 一点约定

- 别在扩展里做长期驻留的循环或后台线程；需要定时更新就给卡片加个刷新按钮让用户点（内部已有刷新按钮）。
- 联网请走 `HttpGetAsync`，不要自己 `new HttpClient()`，否则超时与重试都要你自己兜。
- 缓存请写到 `StorageDirectory`，不要往扩展目录或启动器数据目录其它地方写文件。
