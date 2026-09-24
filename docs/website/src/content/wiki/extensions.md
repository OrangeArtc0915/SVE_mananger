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
| **可以** | 自己联网取数据（只读 GET）、读启动器已有的数据（实例、Mod、存档）、在卡片里显示文字 / 键值 / 进度条 / 分隔线 |
| **不可以** | 写任何文件、写日志、启动游戏、启停 Mod、改启动器设置 —— 扩展只能"看"和"说"，不能"动" |

一句话概括：**扩展只能产出文字与数字**。它拿不到任何写入口，产出的内容里也没有可点击的东西，所以不会碰到你电脑上的文件，也不会改变启动器或游戏的任何状态。

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
| `HttpGetAsync(url)` | GET 一个网址拿文本；失败返回 `null`，不抛异常（复用启动器自己的下载器，带超时与重试）。请求方式固定为 GET，无法向网上写东西 |
| `LauncherVersion` | 启动器版本号 |

> 就这三个。没有写文件、写日志这类入口 —— 需要留痕就把内容放进卡片（正文 / 键值 / 备注），或者直接抛异常，启动器会替你记进「运行日志」。

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
            latest is null ? WidgetItem.Text("存档目录：未知") : WidgetItem.Text($"存档目录：{latest.Directory}")
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
| `WidgetItem.Divider()` | 分隔线 |

就这四种。卡片上不会出现按钮、链接或任何能点开别的东西的行 —— 扩展想让你去看某个地址，只能把地址当文字显示出来。

## 出错会怎样

启动器按「坏扩展不能拖累别人」来做：

- **加载前先做安全检查**（见下一节）：不合格的直接拒绝，并且**一行扩展代码都不会被执行**；
- 加载失败（不是扩展、没有无参构造函数、Id 为空）：在「扩展管理」里显示失败原因，不启用；
- 运行时抛异常或超时：**只在这张卡片里显示错误**，其它卡片和启动器本身照常；
- 扩展是第三方代码，会以当前用户权限运行 —— **只启用你信任的扩展**，这也是默认不自动加载、要手动启用的原因。

## 安全检查

启用一个扩展时，启动器会先读一遍它的元数据（**只读，不运行**），dll 里只要出现下面这几类引用就直接拒绝加载，并在「扩展管理」里写明它是哪个 dll、用了哪个类型：

| 拦下的 | 举例 |
| --- | --- |
| 读写电脑上的文件或目录 | `System.IO.File`、`System.IO.Directory`、`System.IO.FileStream`、`System.IO.Compression.*` |
| 读写注册表 | `Microsoft.Win32.Registry` |
| 调用非托管代码 | `System.Runtime.InteropServices.*`（含 `[DllImport]`） |
| 动态生成并执行代码 | `System.Reflection.Emit.*` |
| 启动或操控别的进程 | `System.Diagnostics.Process` |

`System.IO.Path`、`System.IO.MemoryStream` 这类纯内存操作是放行的。

判定是静态的：**看引用，不看用途**。所以哪怕你只是引用了一下没真用，也会被拒 —— 这是刻意选的"宁可误拒，不可放过"。

> 单个 dll 形式的扩展只扫它自己（同一个 `Widgets` 目录里可能还有别人的扩展）；文件夹形式的扩展会把文件夹里**所有** dll 都扫一遍，所以把访问文件的代码藏进一个附带依赖里也绕不过去。

## 一点约定

- 别在扩展里做长期驻留的循环或后台线程；需要定时更新就给卡片加个刷新按钮让用户点（内部已有刷新按钮）。
- 联网请走 `HttpGetAsync`，不要自己 `new HttpClient()`，否则超时与重试都要你自己兜。
- **不要碰文件系统**：不读写任何文件、不写注册表、不启进程。契约里已经没有这类入口，硬要绕过接口直接调 `System.IO` 的扩展会被上面那道安全检查直接拦掉。
- 需要缓存数据时，把上一次的结果留在扩展自己的静态字段里即可（进程存活期间有效）；启动器不提供落盘位置。
