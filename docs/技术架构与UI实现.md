# 星露谷启动器 · 技术架构与 UI 实现说明

本文面向想了解或参与本项目的开发者，说明**用什么语言写的、界面是怎么做出来的、以及为什么这么做**。
所有内容都对应仓库当前代码，路径均可直接检索。

---

## 一、结论速览

| 项 | 内容 |
| --- | --- |
| 编程语言 | **C#**（`LangVersion=latest`，语法特性用到集合表达式 `[...]`、主构造函数、`required` 等） |
| 运行时 / 框架 | **.NET 8**（`net8.0-windows`）+ **WPF**（`UseWPF=true`） |
| 目标平台 | Windows 10 1809 及以上 / Windows 11，**x64** |
| 产物形态 | 自包含**单文件** `StardewLauncher.exe`（自带 .NET 8 运行时，用户无需装任何东西） |
| 项目数 | 2 个：`StardewLauncher.Core`（纯逻辑）+ `StardewLauncher.App`（WPF 界面） |
| 三方组件 | WebView2（内嵌浏览器）、SharpCompress（压缩包解析）、MQTTnet（已移除，见 §七） |
| UI 模式 | **XAML + 代码后置（code-behind）**，不是 MVVM（原因见 §七） |
| 测试 | 无单元测试项目；质量靠编译期检查 + 两套内置自检工具（§六） |

一句话概括：**一个用 C# / .NET 8 写的 WPF 桌面程序；界面几乎全部自绘（自绘窗口外壳、自研 SVG 图标解析、语义化主题系统），只有「浏览器」这类必须用系统内核的地方才交给 WebView2。**

---

## 二、代码规模与结构

```
src/
  StardewLauncher.Core/     纯逻辑，不依赖 WPF          57 个 .cs   约 7,156 行
    App/          应用元信息（AppInfo 版本唯一来源）、路径（Paths）、设置持久化（Settings）
    Appearance/   背景素材的导入与清理
    Games/        Steam VDF 解析、游戏目录定位
    Homepage/     每日一言、游戏内季节月历换算
    Instances/    实例模型与存储（原版 / Mod 端）
    IO/           文件与路径工具
    Launch/       游戏进程启动与输出采集
    Logging/      落盘日志（Log）+ 内存日志缓冲（ActivityLog）
    Mods/         manifest.json 解析、Mod 扫描、依赖解析、安装规划与执行（最大的一块，17 个文件）
    Multiplayer/  樱花 FRP 的接口客户端（SakuraFrpApi）与 frpc 进程管理（FrpcRunner）
    Nexus/        Nexus API、nxm:// 链接解析、分片并发 + 断点续传下载
    Smapi/        SMAPI 版本查询、云端镜像发现、安装器调用与结果校验
    Tasks/        后台任务中心
    Weather/      现实天气（Open-Meteo，无需 Key）
  StardewLauncher.App/      WPF 界面                    62 个 cs/xaml  约 11,827 行
    Windows/      主窗口 + 各功能弹窗（14 个）
    Pages/        7 个主页面：启动 / Mod 管理 / 游戏实例 / 资源中心 / 联机功能 / 设置 / 运行日志
    Views/        页面内嵌子视图：背景层、首页挂件、SMAPI 安装、Mod 站点浏览器、樱花FRP 面板
    Controls/     自绘控件（9 个）
      Svg/        自研 SVG 图标解析（3 个）
    Theme/        主题与强调色（ThemeService / ThemeColors）
    Resources/    Colors.xaml（配色）+ Controls.xaml（控件模板）
    Animation/    轻量动画引擎与缓动函数
    Interop/      Win32 / DWM 互操作
    Nexus/        nxm:// 单实例转发
    Assets/       程序图标、装饰图、SVG 图标包
```

**分层原则**：`Core` 里不允许出现任何 WPF 类型，所有能力都能被非界面代码复用（也方便将来做 CLI 或测试）；
`App` 只负责呈现与交互，业务状态一律通过 `Core` 的静态服务访问。

**下载**：统一入口是 `Core/Nexus/ResumableDownloader`（Mod 下载与 SMAPI 安装包共用）。服务器支持 Range 且文件 ≥ 4 MB 时切成最多 **6 片并发**（每片 2 MB 均分），各连接写进同一个预分配 `.part` 文件的不同偏移，每片的进度记进 `.part.json`，中断后按同一方案续传；服务器不支持 Range 或文件太小则自动退回单连接。**实测**：12 MB 文件产生 6 条并发 Range 请求、区间覆盖完整且互不重叠；下到 40% 取消后重来，6 片全部从各自断点继续，最终文件 SHA-256 与源文件一致；对不支持 Range 的服务器与 1 MB 小文件均正确走单连接。

---

## 三、技术选型

| 用途 | 选型 | 为什么 |
| --- | --- | --- |
| 界面框架 | **WPF** | 需要自绘窗口外壳、模板化控件、矢量图标与逐帧动画；WinForms 做不出这套观感 |
| 运行时 | **.NET 8 自包含单文件** | 用户群体是普通玩家，不能要求装 .NET；单文件免解压、便于绿色部署 |
| 压缩包解析 | **SharpCompress** | 需要同时吃 `zip` / `rar` / `7z` / `tar` / `gz`，且要流式读取（Mod 包动辄几百 MB） |
| 内嵌浏览器 | **Microsoft.Web.WebView2** | 只在「浏览 Mod 站点」和「樱花FRP 面板」两处用；用系统 Edge 内核，不额外分发浏览器 |
| 图标 | **自研 SVG 解析**（`Controls/Svg/`） | 不引第三方图标库；把 lucide 的 `.svg` 作为 `Resource` 打进包，运行时解析成 `Geometry` 绘制，缩放不糊、颜色可跟随主题 |
| 配色 | **语义化资源键 + oklch 式派生**（见 §五.4） | 换肤只要改一处，界面不出现硬编码颜色 |
| 动画 | **自研 `AnimationEngine`** | WPF 内置 Storyboard 在「批量构建界面」时开销明显；自研引擎可按 key 抢占、可一键挂起 |

---

## 四、启动流程

入口在 `src/StardewLauncher.App/App.xaml.cs`（`StartupUri` 指向主窗口）：

1. **单实例判定**（必须最先做）：用 `NxmLinkRelay.MutexName` 命名互斥量抢占；已有实例时，把命令行里的 `nxm://` 链接通过命名管道转发过去并自行退出——避免用户点网页上的下载按钮时开出第二个启动器。
2. `Paths.Init()` + `SettingsStore.Load()`：确定数据目录（默认 exe 同目录 `Data\`，可用环境变量 `STARDEWLAUNCHER_DATA` 重定向）并读设置。
3. `ThemeService.Initialize(主题模式, 强调色)`：把整套画笔写进 `Application.Resources`，界面通过 `DynamicResource` 自动响应（§五.4）。
4. `NxmLinkRelay.StartListening(...)`：监听后启动进程转发来的链接（回调在后台线程，内部切回 UI 线程）。
5. `EnsureProtocolRegistration()`：确认 `nxm://` 协议关联还在（写当前用户注册表，不需要管理员）。
6. `SelfCheck.Run()`：**仅 Debug 构建**，启动自检（§六）。
7. 挂 `DispatcherUnhandledException`，把未处理异常写进日志而不是直接崩掉。

主窗口 `Windows/MainWindow.xaml` 默认 1080×680，最小 940×580。

---

## 五、UI 实现

### 5.1 窗口外壳：自绘标题栏 + DWM 玻璃化 + 自绘投影

目标是「窗口边缘有留白、留白里能看到自己画的投影，窗口本体是 12px 圆角的浮动卡片」。做法：

- `Window` 设 `WindowStyle="SingleBorderWindow"`、`AllowsTransparency="False"`、**`Background="{x:Null}"`**；
- 用 `WindowChrome`（`CaptionHeight=0`、`ResizeBorderThickness=6`、`UseAeroCaptionButtons=False`）关掉系统标题栏与按钮，保留边框拖拽缩放；
- `Interop/WindowInterop.cs` 调 `dwmapi.dll` 的 `DwmIsCompositionEnabled` / `DwmExtendFrameIntoClientArea`，**把 DWM 框架延伸到整个客户区**——这样「背景为 null 的区域会透出桌面」，边框那圈留白才看得见投影；
- 外壳结构（`MainWindow.xaml`）：`RootGrid` → `PanBack`（`Margin=8`，`Clip` 成 `RadiusX/Y=12` 的圆角矩形，挂 `DropShadowEffect` `BlurRadius=22 / ShadowDepth=0`）→ `PanForm`（窗口渐变底）→ 44px 标题栏 + 内容区。

DWM 不可用时（老系统或关闭了合成）自动退化为贴边窗口，不影响功能。

### 5.2 自绘控件（`src/StardewLauncher.App/Controls/`）

| 控件 | 基类 | 说明 |
| --- | --- | --- |
| `LauncherPage` | `UserControl` | 页面基类：`OnEnter()` / `OnLeave()` 由外壳调用，页面在此刷新数据；`SubViewCount` / `SelectSubView()` 供自检遍历折叠内容（§六） |
| `SurfaceCard` | `ContentControl` | 卡片容器。模板里 `CardBorder` + `TitleElement`，标题空则自动折叠；`UseShadow=False` 会真正摘掉 `DropShadowEffect`——列表里成百上千条各带一个投影会明显拖慢滚动，所以必须能关 |
| `OutlineButton` | `Button` | 主按钮。`Tone` 四档：`Outline` 描边 / `Solid` 实心 / `Danger` 危险 / `Plain` 纯文字 |
| `RoundIconButton` | `Button` | 圆形图标按钮（工具栏的返回、刷新等） |
| `NavItem` | `RadioButton` | 侧栏导航项。`GroupName` 实现互斥；选中态由「背景 + 图标文字颜色 + 左侧竖条」三者共同表达 |
| `MasonryPanel` | `Panel` | 瀑布流：每个子元素投入当前最矮的一列，列宽按可用宽度均分 |

### 5.3 图标：自研 SVG 解析

- `Controls/Svg/SvgPathParser.cs`：解析 path 的 `d` 属性，支持 `M/L/H/V/C/S/Q/T/A/Z` 及相对形式；未支持的指令安全跳过而不是抛异常。
- `Controls/Svg/SvgIconLoader.cs`：从**内嵌资源**里按名取图标（标识形如 `lucide/play`，省略包名走默认包），解析结果按名缓存。
- `Controls/Svg/SvgIcon.cs`：`FrameworkElement`，按 24×24 viewBox 描边绘制，线宽以 viewBox 为单位给出，因此任意缩放都不糊。

图标包在 `Assets/IconPacks/`：`lucide/`（87 个，含 `LICENSE.txt`）+ `app/`（7 个自绘天气图标）。
之所以不用现成图标库：一是少一个依赖，二是描边色要能跟随主题实时变化。

### 5.4 主题系统（`Theme/ThemeService.cs`）

核心是**语义化资源键**：界面里只写 `{DynamicResource Text.Secondary}` 这类键，绝不写具体颜色。

`BuildBrushes()` 一次生成约 40 个键，分 6 组：

| 组 | 键 | 用途 |
| --- | --- | --- |
| `Accent.*` | `Deep` `Base` `Bright` `Hover` `Soft` `Faint` | 强调色阶：`Base` 给图标点睛，`Bright` 给主要按钮与胶囊 |
| `Text.*` | `Primary` `Secondary` `Tertiary` `Disabled` `OnAccent` | 文字层级 |
| `Surface.*` | `Window` `Panel` `PanelGlass` `Card` `CardGlass` `CardHover` `Sunken` `Overlay` | 承载面 |
| `Border.*` | `Default` `Strong` | 描边 |
| `Status.*` | `Warn` `WarnSoft` `Danger` `DangerSoft` `Success` | 状态色 |
| `Nav.*` | `ItemHover` `ItemActive` `Indicator` `Text` `TextActive` | 侧栏导航 |

另有两个渐变画笔：`Brush.WindowBackground`（114° 斜向、两端浓中间淡，避免卡片贴在一块死板上）、`Brush.CardCover`，以及 `Shadow.Tint`。

- **四套强调色**：`Stardew`（暖焦糖，默认）/ `SkyBlue` / `BerryPink` / `Autumn`，每套一个 `AccentPalette`（深、基、亮、悬停、柔、淡）。
- **深浅色**：`ThemeMode.Light / Dark / System`；`System` 会读注册表 `AppsUseLightTheme` 并用 5 秒定时器轮询，用户改系统主题时界面跟着切。
- **半透明玻璃**：`Surface.PanelGlass` / `Surface.CardGlass` 刻意留 22% / 18% 的透出量——个性化背景要能透到整个窗口，但文字必须清楚。`Surface.Card` 保持不透明，避免「半透明套半透明」叠出不可控的通透度。

### 5.5 页面与导航

- 页面编号集中在 `Pages/NavPages.cs`；侧栏是 200px 的 `NavItem` 列表，底部「关于」单独分组（弹独立窗口，不参与页面切换）。
- 主窗口用字典缓存页面实例（`GetPage()`），切换时只切可见性并调 `OnEnter()` / `OnLeave()`，所以页面状态天然保留（例如 Mod 列表的滚动位置）。
- 进入页面时 `RunEnterAnimation()` 让内容块**错峰淡入 + 轻微上移**。

### 5.6 动画引擎（`Animation/AnimationEngine.cs` + `Ease.cs`）

以 `CompositionTarget.Rendering` 为帧驱动，按 **key** 管理动画组；同名 key 会先停掉旧动画（避免快速悬停时动画叠加）。批量构建界面时用 `Suspend()` 临时关掉一切动画（`IsEnabled` 即「挂起计数为 0」），构建完再恢复。
缓动函数在 `Ease.cs`（如 `OutFluent`）。

> 注意：动画**尚未**接入系统的「减少动态效果」设置（`SystemParameters.ClientAreaAnimation`），目前只受 `Suspend()` 控制。这是待改项之一。

### 5.7 背景层（`Views/BackgroundLayer.xaml`）

铺满整个窗口（**含标题栏与侧栏**，`Grid.RowSpan="2"`，位于所有界面之下），支持三类素材：

- **静态图**：直接铺；
- **GIF 动图**：用 WPF 自带的 `GifBitmapDecoder` 逐帧解码 + 按帧延时定时切换，**不引第三方库**；窗口最小化时停掉，别白烧 CPU；
- **视频**：`MediaElement` 静音循环；起播必须放在 `MediaOpened` 事件里（`LoadedBehavior=Manual` 下过早调 `Play()` 会被丢弃），并处理 `MediaFailed` —— 解不开就收掉视频层，宁可露出主题渐变也不留一块黑屏。

素材由 `Core/Appearance/BackgroundService` 复制进 `Data\Background\`，避免用户原图被移动后背景失效。

### 5.8 内嵌浏览器（两处，各自独立的用户数据目录）

| 视图 | 用途 | 用户数据目录 |
| --- | --- | --- |
| `Views/NexusBrowserView.xaml` | 资源中心「在线浏览」：八个 Mod 站点切换栏；拦截 `nxm://` 链接转交 `NxmDownloadWindow`，因此**不依赖 `nxm://` 协议注册、也不需要 Nexus 会员** | `Data\WebView2` |
| `Views/SakuraPanelView.xaml` | 联机功能页右侧常驻的樱花FRP **官方面板**（建隧道、改名、删除都在里面做） | `Data\WebView2-Sakura` |

两个视图都用 `CoreWebView2Environment.CreateAsync(null, 用户数据目录)` 显式指定数据目录：登录状态留在本机、能随程序一起搬走；
**必须分开**，因为同一份用户数据目录不能被两个 WebView2 内核同时占用。
两者都做了「内核创建失败 → 显示提示卡」的兜底（Windows 10 需要装一次免费的 WebView2 Runtime）。

### 5.9 日志

- `Core/Logging/Log`：**落盘**日志，写在 `Data\Log\`，带文件数量与大小上限，反馈问题时让用户带上这个目录。
- `Core/Logging/ActivityLog`：**内存**环形缓冲（最近 500 条）+ `Appended` 事件。页面向它订阅，因此日志不再绑在某个页面上——切走页面、页面被销毁重建，历史都还在。
- `Pages/PageLog`：「运行日志」页，分「应用日志 / 游戏日志」两个标签，共用上面这份缓冲。

---

## 六、质量保障与自检工具

项目**没有单元测试**，改为「编译期检查 + 两套内置自检」：

1. **`SelfCheck.cs`（仅 Debug 构建）**：启动时扫一遍资源。起因很实际——图标是以字符串形式在 XAML 里引用的（`Icon="lucide/play"`），**名字写错不会报错、只会静默不显示**，所以启动时校验 `Assets/IconPacks/` 下每个图标都能被解析，并把游戏目录探测等依赖外部环境的结果一并写进日志。
2. **`DebugCapture.cs`**：两个环境变量驱动的无头工具。
   - `SL_CAPTURE=<png 路径>`：把窗口用 `RenderTargetBitmap` 渲染成 PNG 后退出（直接渲染视觉树，不受遮挡影响），可配 `SL_CAPTURE_DELAY` 延时。
   - `SL_OVERLAP_SCAN=1280,1440`：按**窗口宽度 × 页面 × 子视图**逐个切过去，两两计算控件矩形交集，把结果写进日志后退出——用来抓「某个宽度下卡片压住按钮」这类只在特定尺寸才出现的问题。

---

## 七、已知取舍与待改

诚实列出当前的技术债，避免后来者踩坑：

1. **`CommunityToolkit.Mvvm 8.4.2` 被引用但全项目没有一处使用**（无 `ObservableObject` / `RelayCommand`）。界面是 XAML + 代码后置：页面逻辑与控件直接交互，少了 ViewModel 一层。可以移除此依赖；若将来要 MVVM 化，再按页面逐步迁移。
2. **视频背景依赖系统自带的 Windows Media Player 组件**。在 LTSC 等精简系统上该组件不存在，`MediaElement` 无法解码，程序会回退为无背景并在日志里写明原因；图片与 GIF 不受影响。
3. **WebView2 需运行时**：Windows 11 自带；Windows 10 若缺失，在线浏览与内嵌面板会显示提示卡，引导安装一次免费的 WebView2 Runtime。
4. **无单元测试**：`Core` 层本来是最适合写测试的（Mod 依赖解析、安装规划、VDF 解析等），目前只靠自检与手工验证。
5. **动画未接入系统「减少动态效果」**：`AnimationEngine.IsEnabled` 只反映 `Suspend()` 状态，没有读 `SystemParameters.ClientAreaAnimation`，用户在系统里关掉动画后界面仍会做过渡。
6. **页面切换是「缓存 + 切可见性」**：好处是状态保留，代价是常驻页面越多、内存占用越高（内嵌浏览器这类重对象已改为「首次显示时才创建」）。

---

## 八、构建与发布

```bat
:: 调试运行
dotnet build src\StardewLauncher.App\StardewLauncher.App.csproj -c Debug
src\StardewLauncher.App\bin\Debug\net8.0-windows\StardewLauncher.exe

:: 发行（等价于双击 build.bat）
dotnet publish src\StardewLauncher.App\StardewLauncher.App.csproj ^
    -c Release -r win-x64 --self-contained true ^
    -p:PublishSingleFile=true -p:PublishTrimmed=false ^
    -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none ^
    -p:Version=<版本号> -o publish
```

- **版本号只在一处定义**：`src/StardewLauncher.Core/App/AppInfo.cs`，`build.bat` 会读它并传给 MSBuild，并据此命名发行包 `StardewLauncher-v<版本>-win-x64.zip`。
- `PublishTrimmed=false`：WPF 对裁剪支持有限，裁了容易在运行时缺类型。
- `IncludeNativeLibrariesForSelfExtract=true`：把原生依赖一并塞进单文件，首次运行自解压。

---

## 九、参考与许可

界面结构、配色与图标为**本项目独立设计**；参考过的开源项目、借鉴的架构思路（如 PCL-CE 的窗口玻璃化思路）、使用的第三方包及各自的许可证要求，都写在根目录 [`NOTICE`](../NOTICE) 里，请一并阅读。
内置图标来自 [Lucide](https://lucide.dev)（ISC License）。
