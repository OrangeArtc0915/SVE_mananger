# 星露谷启动器

面向《星露谷物语》(Stardew Valley) 的 Windows 桌面管理工具，把**游戏实例、SMAPI、Mod 和存档**收进一个窗口。

官网：<https://orangeartc0915.github.io/SVE_mananger/>

- 实例管理：一套「原版」+ 多套「Mod 端」，各自独立的 Mod 目录、存档目录与 SMAPI 配置，互不干扰
- Mod 管理：直接吃 `zip` / `rar` / `7z` / `tar` / `gz`，自动识别安装方式；支持启用/禁用、标签分类、Mod 库
- 安装规划：安装前先列出「会发生什么」（覆盖哪些文件、替换哪些 XNB、装到哪个目录），确认后再动手
- 依赖检查：递归解析 `manifest.json` 的前置关系，缺哪个一键补齐
- **一键装 SMAPI**：在 Mod 管理页直接下载并安装 SMAPI，官方与启动器云端镜像两条线路可选
- 在线下载：内嵌浏览器浏览八个 Mod 站点，Nexus 页面上点「Mod Manager Download」即可在启动器内下载，非会员也能用
- 现实天气与月历：接的是现实生活的时间与天气，不是游戏内存档

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
LICENSE               MIT 许可证
```

### 运行环境

| 项目 | 要求 |
| --- | --- |
| 操作系统 | Windows 10 1809 及以上 / Windows 11 |
| 架构 | x64 |
| 运行时 | **不需要**单独安装 .NET，exe 已自包含 .NET 8 |
| WebView2 | Windows 11 自带；Windows 10 若在线浏览 Mod 页面时报缺失，装一次免费的 [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) 即可 |
| 权限 | 普通用户即可，**不需要**管理员权限（`nxm://` 协议只写当前用户注册表） |

### 安装后第一次运行

1. 打开「设置 → 游戏与实例」，确认或手动指定星露谷物语的安装目录
   - Steam 版一般能自动从注册表和 `libraryfolders.vdf` 里找到
2. 到「游戏实例」新建一个实例，选**原版**或 **Mod 端**
   - 原版：直接拉起 `Stardew Valley.exe`
   - Mod 端：走 SMAPI 启动，Mod 管理功能只对这个实例生效
3. 到「Mod 管理」把压缩包导入（或先在「下载中心」在线下载），勾选后一键安装

> 启动器**不分发游戏本体**。SMAPI 可以由启动器代装（见下），装好后实例页会显示当前 SMAPI 版本。

### 让启动器代装 SMAPI

Mod 管理页工具栏上的 **「下载 SMAPI」**，两条线路：

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

### 卸载与数据位置

程序是绿色的，删掉整个目录即可。运行时会**在 exe 同目录**创建 `Data\`：

```
Data\Settings.json     设置（含 Nexus API Key，注意别外传）
Data\instances\        实例定义、每个实例的 Mod / 存档目录
Data\Cache\            缓存
Data\Log\              运行日志，反馈问题时请带上
```

删掉 `Data\` 等同于恢复出厂设置。也可以用环境变量 `STARDEWLAUNCHER_DATA` 把数据目录重定向到别处，做便携部署或测试隔离。

---

## 从源码构建

需要 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)。

### 一键发行（推荐）

双击根目录的 `build.bat`，它会：

1. 从 `src/StardewLauncher.Core/App/AppInfo.cs` 读取版本号（版本号只在这一处定义）
2. 发布自包含单文件 exe 到 `publish\`
3. 把 exe + `README.md` + `NOTICE` + `LICENSE` 打成 `publish\StardewLauncher-v<版本>-win-x64.zip`

> `publish\Data\` 是你的运行时数据，`build.bat` 不会碰它。

### 手动构建

```bat
:: 调试运行
dotnet build src\StardewLauncher.App\StardewLauncher.App.csproj -c Debug
src\StardewLauncher.App\bin\Debug\net8.0-windows\StardewLauncher.exe

:: 等价于 build.bat 的发布命令
dotnet publish src\StardewLauncher.App\StardewLauncher.App.csproj ^
    -c Release -r win-x64 --self-contained true ^
    -p:PublishSingleFile=true -p:DebugType=none -p:Version=1.0.0 -o publish
```

也可以直接双击 `一键编译并运行.bat` 做「编译 + 启动」。

---

## 目录结构

```
src/
  StardewLauncher.Core/     纯逻辑，不依赖 WPF
    App/        应用元信息、路径、设置持久化
    Games/      Steam VDF 解析与游戏目录定位
    Instances/  实例模型与存储
    Mods/       manifest 解析、Mod 扫描、依赖解析、安装规划与执行
    Nexus/      Nexus API、nxm:// 链接、断点续传下载
    Smapi/      SMAPI 版本查询、云端镜像发现、安装包识别与安装器调用
    Weather/    现实天气（Open-Meteo，无需 Key）
    Launch/     游戏启动与进程日志
    Tasks/      后台任务中心
  StardewLauncher.App/      WPF 界面
    Windows/    主窗口及各功能弹窗
    Pages/      启动 / Mod 管理 / 游戏实例 / 设置
    Controls/   自绘控件与 SVG 图标解析
    Resources/  语义化配色与控件样式
docs/
  website/      官网（单文件静态页，GitHub Pages 从这里发布）
build.bat       发行构建脚本
```

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
A：速度取决于本机网络与站点线路，启动器只做转发，不缓存也不加速。可以先在浏览器里下好，再用「导入」把压缩包放进 Mod 库，效果一样。SMAPI 安装包也一样，云端线路里可以换成 Gitee 源。

**Q：支持 macOS / Linux 吗？**
A：不支持，只做 Windows。

---

## 许可证

本项目以 **MIT License** 发布，详见 [LICENSE](LICENSE)。

界面结构、配色与图标为独立设计。参考过的开源项目、借鉴的思路以及使用的第三方包（含各自许可证要求）都写在 [NOTICE](NOTICE) 里，请一并阅读。

内置图标来自 [Lucide](https://lucide.dev)（ISC License）。

## 免责声明

本项目是第三方工具，与 ConcernedApe 无关，未获其授权或背书。

- **不提供游戏本体分发**，使用前请自行通过正规渠道购买《星露谷物语》
- Mod 下载以**当前登录用户自己的身份**进行，请遵守对应站点的服务条款
- `src/StardewLauncher.App/Assets/Art/` 下有少量装饰图片来源于游戏，版权归 ConcernedApe 所有，仅作个人本地使用的界面点缀，本项目不主张任何权利
