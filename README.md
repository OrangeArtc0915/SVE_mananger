# 星露谷启动器

面向《星露谷物语》(Stardew Valley) 的 Windows 桌面管理工具，把**游戏实例、SMAPI、Mod、存档和联机**收进一个窗口。

官网：<https://orangeartc0915.github.io/SVE_mananger/>

- 实例管理：一套「原版」+ 多套「Mod 端」，每个实例自己选游戏目录、自己决定是否走 SMAPI 启动
- Mod 管理：直接吃 `zip` / `rar` / `7z` / `tar` / `gz`，自动识别安装方式；支持启用/禁用、标签分类、Mod 库
- Mod 配置档：把当前的 Mod 启停组合存成一档（「美化包」「剧情包」这样成套切），一键应用，可导出 / 导入 json
- 拖拽安装：把压缩包或一个 Mod 文件夹拖到窗口任意位置，自动切到 Mod 管理页开始导入
- 安装规划：安装前先列出「会发生什么」（覆盖哪些文件、替换哪些 XNB、装到哪个目录），确认后再动手
- 依赖检查：递归解析 `manifest.json` 的前置关系，缺哪个一键补齐
- 存档管理（侧栏「工具箱 → 存档管理」）：列出游戏存档的摘要（进度、金钱、游玩时长、技能、体积），一键备份、按需回滚；备份放在启动器数据目录里，不往游戏存档目录塞东西
- **一键装 SMAPI**：在「资源中心」直接下载并安装 SMAPI，官方与启动器云端镜像两条线路可选
- 资源中心：安装 SMAPI，或在内嵌浏览器里直接浏览八个 Mod 站点，Nexus 页面上点「Mod Manager Download」即可在启动器内下载，非会员也能用
- **樱花 FRP 联机**：填自己的樱花 FRP 访问密钥，在可用节点上建一条 UDP 24642 隧道并拉起官方 frpc；域名地址与 IP 地址各有一键复制，页面右侧还内嵌了官方管理面板（建隧道、改名、删除都在那里）
- 运行日志：侧栏的「运行日志」页把启动器操作与游戏输出分成两个标签；日志收在内存里，切走页面也不会丢
- 使用手册：侧栏直接在内嵌浏览器里打开官网 Wiki，右上角留了「用浏览器打开」的退路
- 主页小组件：月历、现实天气（接的是现实生活的时间与天气，不是游戏内存档）、每日一言，加上**存档概览**、**自定义图片**、**Mod 概览**共 6 张卡片；可拖动排序、勾选显隐，顺序会自动记住，还能「导出布局…」「导入布局…」
- 可自定义的外观：自定义强调色（色相滑块即改即看，或填色值点「按色值」应用，「用预设配色」回到四套预设）、面板不透明度（更透 / 默认 / 更实 / 不透明）、侧栏导航上移下移与收起显示
- 个性化背景与主题：背景可换成自己的图片 / GIF，界面半透明玻璃质感，四套配色 + 深浅色可切

---

## 下载与安装

### 面向普通玩家

1. 打开 **[Releases](https://github.com/OrangeArtc0915/SVE_mananger/releases)** 页面，下载最新版的 `StardewLauncher-v*-win-x64.zip`
2. 把压缩包解压到任意目录，比如 `D:\StardewLauncher`
3. 双击 `StardewLauncher.exe` 即可运行

国内访问 GitHub 较慢时，可改用 **Gitee 发行版页**：<https://gitee.com/orangearc655743/SVE_mananger_File/releases>

发行包内容：

```
StardewLauncher.exe   启动器本体（单文件、自带 .NET 8 运行时）
README.md             本文件
NOTICE                第三方依赖与参考来源说明
LICENSE               许可条款（保留所有权利，允许原样转发）
```

> 发行包里没有任何第三方闭源二进制。联机用的樱花 FRP 客户端 frpc **不在包内**，由程序在你点「准备 frpc」时从樱花 FRP 官方接口下载，并用接口返回的 MD5 校验。

### 运行环境

| 项目 | 要求 |
| --- | --- |
| 操作系统 | Windows 10 1809 及以上 / Windows 11 |
| 架构 | x64 |
| 运行时 | **不需要**单独安装 .NET，exe 已自包含 .NET 8 |
| WebView2 | Windows 11 自带；Windows 10 若在线浏览 Mod 页面时报缺失，装一次免费的 [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) 即可 |
| 权限 | 普通用户即可，**不需要**管理员权限（联机走樱花 FRP 的普通进程，不装网卡也不装驱动） |

### 安装后第一次运行

1. 打开「设置 → 游戏与实例」，确认或手动指定星露谷物语的安装目录
   - Steam 版一般能自动从注册表和 `libraryfolders.vdf` 里找到
2. 到「游戏实例」新建一个实例，选**原版**或 **Mod 端**
   - 原版：直接拉起 `Stardew Valley.exe`
   - Mod 端：走 SMAPI 启动，Mod 管理功能只对这个实例生效
3. 到「资源中心」下载 Mod，或把压缩包导入「Mod 管理」后勾选安装
4. 想和朋友一起种地：到「联机功能」填访问密钥 → 建一条 UDP 隧道 → 启动，页面会出现「域名地址」与「IP 地址」两栏，各点一下「复制」，任选一个发给朋友

> 启动器**不分发游戏本体**。SMAPI 可以由启动器代装（见下），装好后实例页会显示当前 SMAPI 版本。

### 备份与回滚存档

侧栏「工具箱 → 存档管理」读的是游戏自己的存档目录 `%APPDATA%\StardewValley\Saves`，只读不改：

1. 每张卡片就是一份存档，写着玩家名、农场名、进度（第几年 / 季节 / 第几天）、金钱、游玩时长、五项技能、最后保存时间、存档体积和已有备份数；主存档文件缺失时会红字提示
2. 点卡片上的「备份」就把这份存档整目录复制一份到 `Data\save-backups\<存档Id>\<时间戳>\`；页面右上角还有「全部备份一次」
3. 点「备份与回滚…」打开备份管理窗口：新建备份、恢复（回滚）、删除。回滚前会先给当前存档自动打一份 `-auto` 快照，所以这一步本身也能反悔
4. 游戏运行中允许备份（此时备份的是上次保存的状态），但**回滚要求先退出游戏**

> 备份一律放在启动器的 `Data\save-backups\` 里，**不往游戏存档目录里塞文件**，免得被 Steam 云同步带走。每个存档默认最多保留 10 份（超出自动删最旧的），回滚前默认自动快照，这两项由 `Data\Settings.json` 里的 `SaveBackupKeepCount` 与 `SnapshotBeforeRestore` 控制。

### 用配置档切换 Mod 组合

到「Mod 管理 → 配置档」，把当前的启停组合存成一份档（比如「美化包」「剧情包」「性能包」），下次一键切回来：

1. 先调好这一套的启停状态，点「保存当前状态」，起个名字
2. 想换组合时选中对应的档点「应用」——启动器只重命名 Mod 文件夹（加 / 去开头的点），**不删改任何 Mod 文件**
3. 还能「用当前状态覆盖」「重命名」「导出」成 json，以及「导入配置档…」；配置档存在 `Data\profiles\`，一档一个文件

> 配置档之间靠 Mod 的 UniqueId（没有 UniqueId 就用去掉点前缀的文件夹名）对应，所以禁用改名之后也不会错乱。原版实例不加载 Mod，配置档对它没有意义。

### 让启动器代装 SMAPI

「资源中心 → 下载 SMAPI」，两条线路：

| 线路 | 来源 | 说明 |
| --- | --- | --- |
| 官方 | GitHub Releases（Pathoschild/SMAPI） | 总是取最新版 |
| 启动器云端仓库 | 本项目的 `resource` 分支 | 国内可达性更好，可选 GitHub / Gitee 两个源（默认跟随设置页） |

用法：

1. 点「下载 SMAPI」，在「安装目录」里选一个已有的**游戏实例**（没有实例时可直接填路径或浏览选择）
2. 选线路 → 点「下载并安装」
3. 安装过程中会**弹出一个黑色控制台窗口，这是 SMAPI 官方安装器自身的正常行为**，不要关掉它，几秒后会自动关闭
4. 装完启动器会校验游戏目录里的 `StardewModdingAPI.exe` 和 `smapi-internal`，并显示装好的版本

想自己一步步走官方流程的话，用同一窗口里的「手动安装…」：启动器会自动下载并解压好安装包，然后调起官方的 `install on Windows.bat`，剩下的在控制台里按提示操作即可。窗口上会显示识别到的脚本路径。

> 安装器是 SMAPI 官方程序，启动器只负责下载、解压、定位脚本并传参（`--install --game-path <目录> --no-prompt`）。

### 用樱花 FRP 联机

1. 到「联机功能」页，把樱花 FRP 面板里的**访问密钥**填进来，点「验证并加载」（右侧已内嵌官方网页版面板，登录一次即可）
   - 免费账号也能开隧道；密钥等价于账号密码，只存在本机 `Data\Settings.json`
2. 点「准备 frpc」把官方客户端下载到本机（只下一次，带 MD5 校验）
3. 填隧道名 → 「加载节点」选一个节点 → 「创建 UDP 隧道（本地端口 24642）」
4. 在「我的 UDP 隧道」里点「启动」，等页面出现连接地址后，「域名地址」与「IP 地址」各点一下「复制」，任选一个发给朋友
5. 你在游戏里开合作模式（存档需要有联机小屋），朋友走「合作 → 加入局域网游戏 → 输入这个地址」

> 隧道由樱花 FRP 的服务端与官方 frpc 维持，启动器只负责拉起进程、读取日志、把地址显示出来。改名、删除、改端口都在页面右侧那块内嵌面板里做（不弹系统浏览器）。用完后记得点「停止隧道」。

### 卸载与数据位置

程序是绿色的，删掉整个目录即可。运行时会**在 exe 同目录**创建 `Data\`：

```
Data\Settings.json     设置（含 Nexus API Key 与樱花 FRP 访问密钥，注意别外传）
Data\instances\        实例定义（每个实例只记「用哪个游戏目录 + 是否走 SMAPI」）
Data\save-backups\     存档备份（每个存档一个子目录，一份备份一个时间戳文件夹）
Data\profiles\         Mod 配置档（一档一个 json）
Data\SakuraFrp\        樱花 FRP 的 frpc 客户端（首次使用时下载）
Data\WebView2\         资源中心内嵌浏览器的用户数据（Mod 站点登录状态）
Data\WebView2-Sakura\  樱花FRP 面板的用户数据（面板登录状态）
Data\WebView2-Wiki\    使用手册内嵌浏览器的用户数据
Data\Background\       自定义背景素材
Data\Homepage\         主页「自定义图片」小组件的图片素材（选图时复制进来）
Data\Cache\            缓存
Data\Log\              运行日志，反馈问题时请带上
```

另外，下载的安装包放在 `%LOCALAPPDATA%\StardewLauncher\Downloads\`。

删掉 `Data\` 等同于恢复出厂设置。也可以用环境变量 `STARDEWLAUNCHER_DATA` 把数据目录重定向到别处，做便携部署或测试隔离。

---

## 从源码构建

需要 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)。

> 先想了解整体架构、技术选型与界面是怎么实现的？看 [docs/技术架构与UI实现.md](docs/技术架构与UI实现.md)。

### 一键发行（推荐）

双击根目录的 `build.bat`，它会：

1. 从 `src/StardewLauncher.Core/App/AppInfo.cs` 读取版本号（版本号只在这一处定义）
2. 发布自包含单文件 exe 到 `publish\`
3. 把 exe + `README.md` + `NOTICE` + `LICENSE` 打成 `publish\StardewLauncher-v<版本>-win-x64.zip`

> `publish\Data\` 是你的运行时数据，`build.bat` 不会碰它。

#### 发版时要传的资产

启动器的「一键自动更新」靠发布页里的 **单文件 `StardewLauncher.exe`** 实现：它下载这个文件、校验后原地替换自己。

所以发新版本时，除了 `StardewLauncher-v<版本>-win-x64.zip`，**还要把 `publish\StardewLauncher.exe` 本身作为 release 资产一起传上去**（名字以 `StardewLauncher` 开头、以 `.exe` 结尾即可，例如 `StardewLauncher.exe` 或 `StardewLauncher-v1.6.0-win-x64.exe`）。

发布页里只有压缩包、没有单文件 exe 时，检查更新仍然能用，但按钮只会是「打开下载页」，用户得手动下载。

### 手动构建

```bat
:: 调试运行
dotnet build src\StardewLauncher.App\StardewLauncher.App.csproj -c Debug
src\StardewLauncher.App\bin\Debug\net8.0-windows\StardewLauncher.exe

:: 等价于 build.bat 的发布命令
dotnet publish src\StardewLauncher.App\StardewLauncher.App.csproj ^
    -c Release -r win-x64 --self-contained true ^
    -p:PublishSingleFile=true -p:DebugType=none -p:Version=1.6.0 -o publish
```

也可以直接双击 `一键编译并运行.bat` 做「编译 + 启动」。

---

## 目录结构

```
src/
  StardewLauncher.Core/     纯逻辑，不依赖 WPF
    App/        应用元信息、路径、设置持久化
    Appearance/ 背景素材的导入与清理
    Games/      Steam VDF 解析与游戏目录定位
    Instances/  实例模型与存储
    Mods/       manifest 解析、Mod 扫描、依赖解析、安装规划与执行
    Saves/      游戏存档扫描（摘要）与备份 / 回滚
    Multiplayer/ 樱花 FRP 的接口客户端与 frpc 进程管理
    Nexus/      Nexus API、nxm:// 链接、断点续传下载
    Smapi/      SMAPI 版本查询、云端镜像发现、安装包识别与安装器调用
    Weather/    现实天气（Open-Meteo，无需 Key）
    Launch/     游戏启动与进程日志
    Tasks/      后台任务中心
  StardewLauncher.App/      WPF 界面
    Windows/    主窗口及各功能弹窗
    Pages/      启动 / Mod 管理 / 游戏实例 / 资源中心 / 联机功能 / 工具箱 / 设置 / 运行日志
    Views/      嵌入页面的子视图（含主页小组件、内嵌 Wiki）与背景层
    Controls/   自绘控件与 SVG 图标解析
    Assets/     图标与装饰图
    Resources/  语义化配色与控件样式
    Theme/      主题与强调色
  StardewLauncher.Api/     主页扩展的契约程序集（写扩展只需要引用它）
samples/
  StardewLauncher.SampleWidget/  示例扩展，可直接复制改成自己的
docs/
  website/      官网源码（Astro 项目，基于开源模板 Mizuki 定制，不参与 .NET 构建）
    src/content/posts/  文章：首页列表 / 归档 / 分类标签的数据来源
    src/content/spec/   独立页面正文（关于、赞助）
    public/             静态资源与图片（壁纸、头像、收款码、图标）
    dist/               构建产物（pnpm build 生成，不纳入版本管理）
build.bat       发行构建脚本
```

---

## 主页扩展（API）

除了内置的月历、天气、每日一言等小组件，主页还支持用户自己写的扩展：实现 `IHomepageWidgetPlugin`、编译成 dll、丢进扩展目录，再在主页「自定义 → 扩展管理…」里手动启用，它就会作为一张卡片出现在小组件区，一样能拖动排序、一样能收起。

- **契约程序集**：`src/StardewLauncher.Api`（纯 `net8.0`，不含 WPF）。扩展只描述"取什么数据、显示什么"，卡片外观由启动器按统一样式渲染，所以不会有风格不一致的卡片。
- **扩展能做什么**：只读 GET 联网取数、读启动器已有的数据（实例 / 当前实例的 Mod / 存档摘要），产出文字 / 键值 / 进度条 / 分隔线。
- **扩展不能做什么**：不写任何文件（契约里没有写入口，也没有可写的缓存目录）、不写日志、不启动游戏、不启停 Mod、不改设置；卡片上也不提供可点击的行。一句话：**扩展只能产出文字与数字**。
- **加载方式**：只加载用户在扩展管理里明确启用的扩展，默认不自动执行任何第三方代码；单个扩展加载失败或运行出错只影响它自己那张卡片，错误原因会写进「运行日志」。
- **加载前的安全检查**：启用时先只读 dll 元数据（不运行代码），命中「读写文件或目录 / 注册表 / 非托管调用 / 动态生成代码 / 启动其它进程」这几类引用就直接拒绝，并在扩展管理里写明是哪个 dll 用了哪个类型。文件夹形式的扩展会把该文件夹里所有 dll 一起扫。详见官网「写一个主页扩展 → 安全检查」。
- **示例**：`samples/StardewLauncher.SampleWidget`，编译出的 dll 直接可放进扩展目录：

```powershell
dotnet build samples\StardewLauncher.SampleWidget\StardewLauncher.SampleWidget.csproj -c Release
# 产物复制到 <启动器数据目录>\Widgets\ 后在扩展管理里启用
```

写扩展的完整说明见官网使用手册的「写一个主页扩展」。


---

## 官方网站

官网是独立的 Astro 项目，源码在 `docs/website/`，与 .NET 构建互不影响。

```powershell
cd docs/website
npm i -g pnpm        # 模板要求用 pnpm（package.json 里锁了 only-allow pnpm）
pnpm install
pnpm dev             # 本地预览： http://localhost:4321
pnpm build           # 产出静态站点到 dist/
```

站点按 `astro.config.mjs` 里的 `site` + `base` 生成绝对路径，当前配置为 GitHub Pages
项目页 `https://orangeartc0915.github.io/SVE_mananger/`；若换成自定义域名，这两个值要同步改。

界面基于开源模板 [Mizuki](https://github.com/saicaca/Mizuki)（MIT）定制，改动清单见 `NOTICE` 第十一节。
日常改内容不需要碰样式：

- 加／改文章：`docs/website/src/content/posts/*.md`（另有独立的「关于」`spec/about.md`、「赞助」`spec/helpus.md`）
- 换壁纸与头像：`docs/website/public/assets/banner/`、`public/assets/images/avatar.jpg`，改完重新 `pnpm build`
- 导航、站点标题、主题色：`docs/website/src/config.ts`

---

## 常见问题

**Q：为什么报「没有找到游戏目录」？**
A：到「设置 → 游戏与实例」手动指定，或确认游戏是 Steam 版且已至少启动过一次。

**Q：Mod 装了但游戏里没生效？**
A：三个常见原因——① 实例选的是「原版」而不是「Mod 端」；② 该 Mod 被禁用了（名字前缀是点号）；③ 缺前置，去「Mod 管理」跑一次依赖检查。

**Q：Nexus 的下载按钮点了没反应？**
A：需要在「设置 → 网络与账号」填入 Nexus API Key。免费账号也能用，`nxm://` 关联是 Nexus 官方给第三方管理器留的标准通道。

**Q：装 SMAPI 时弹出一个黑窗口，是不是出问题了？**
A：不是。那是 SMAPI 官方安装器自己的控制台窗口，启动器无法也不该隐藏它（它的代码里有必须依赖真实控制台的清屏调用）。等它自己关掉即可，启动器会在之后校验安装结果。

**Q：下载很慢？**
A：下载本身走多连接分片（服务器支持时最多 6 个连接同时下，中断了还能接着下），但速度上限仍取决于你的网络与站点线路——启动器不缓存也不加速。可以先在浏览器里下好，再用「导入」把压缩包放进 Mod 库，效果一样；SMAPI 安装包也可以换成 Gitee 源。

**Q：樱花 FRP 要花钱吗？**
A：需要你自己在樱花 FRP 面板拿一个访问密钥，免费账号也能开隧道。星露谷联机走 UDP，隧道要建 **UDP 类型**（本地端口 24642），TCP 隧道用不了。

**Q：开了隧道，朋友却连不上？**
A：先确认隧道是 UDP 类型并且已启动、frpc 已连上节点（页面上会出现连接地址）。把「复制地址」拿到的地址原样发给对方，对方在游戏里走「合作 → 加入局域网游戏 → 输入地址」。地址没出现时不要复制，那说明 frpc 还没连上。

**Q：联机速度慢？**
A：启动器只是把官方 frpc 拉起来，不参与转发也不加速；实际速度取决于樱花 FRP 的节点线路和你自己的上行带宽。

**Q：支持 macOS / Linux 吗？**
A：不支持，只做 Windows。

---

## 许可证

本项目为「**保留所有权利**」的专有软件，详见 [LICENSE](LICENSE)。

允许在**不修改安装包内任何文件**的前提下把完整安装包原样转发给他人；除此之外，不允许修改、拆分、复用其中的任何部分，也不允许用于商业用途。如需其他授权，请先取得版权所有人的书面许可。

界面结构、配色与图标为独立设计。参考过的开源项目、借鉴的思路以及使用的第三方包（含各自许可证要求）都写在 [NOTICE](NOTICE) 里，请一并阅读。

内置图标来自 [Lucide](https://lucide.dev)（ISC License）。

联机功能依赖第三方内网穿透服务「樱花FRP」。它的客户端 frpc **不随本项目分发**，由程序在用户点击「准备 frpc」时从樱花 FRP 官方接口下载并做 MD5 校验，具体见 [NOTICE](NOTICE)。

## 免责声明

本项目是第三方工具，与 ConcernedApe 无关，未获其授权或背书。

- **不提供游戏本体分发**，使用前请自行通过正规渠道购买《星露谷物语》
- Mod 下载以**当前登录用户自己的身份**进行，请遵守对应站点的服务条款
- `src/StardewLauncher.App/Assets/Art/` 下有少量装饰图片来源于游戏，版权归 ConcernedApe 所有，仅作个人本地使用的界面点缀，本项目不主张任何权利
