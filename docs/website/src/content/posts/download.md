---
title: 下载渠道与提速说明
published: 2026-09-15
description: GitHub / Gitee 两个渠道，包里有什么、怎么用，以及为什么下载慢不一定是启动器的问题。
tags: [下载, 断点续传]
category: 使用文档
---

## 两个下载渠道

| 渠道 | 地址 | 适合 |
| --- | --- | --- |
| GitHub Releases | [github.com/OrangeArtc0915/SVE_mananger/releases](https://github.com/OrangeArtc0915/SVE_mananger/releases) | 海外线路，更新最先到这里 |
| Gitee 发行版 | [gitee.com/orangearc655743/SVE_mananger_File/releases](https://gitee.com/orangearc655743/SVE_mananger_File/releases) | 国内镜像，包的内容完全一样 |

## 包里有什么

```
StardewLauncher.exe   启动器本体（单文件、自带 .NET 8 运行时）
README.md             说明文档
NOTICE                第三方依赖与参考来源说明
LICENSE               MIT 许可证
```

解压到任意目录，双击 `StardewLauncher.exe` 即可。首次运行会在同目录生成 `Data\`；联机用的 frpc 由启动器在你要用时从樱花 FRP 官方接口下载。

## 下载提速：多连接分片

在启动器内下载 Mod 时：

- 服务器支持 Range 且文件较大 → 自动切成最多 **6 个连接**同时下，Mod 包越大越明显
- 进度按分片记录 → 断网、暂停、手动取消，下次都从各自的断点接着下，不用重头来
- 服务器不支持分片 → 自动退回单连接，不会因此失败

> 但速度上限仍取决于你的网络与站点线路：**启动器不缓存也不加速**。如果实在慢，可以先在浏览器里下好，再用「导入」把压缩包放进 Mod 库，效果一样。
