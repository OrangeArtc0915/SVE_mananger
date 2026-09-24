---
title: 下载与安装
description: 从哪下载、怎么装、数据存在哪、怎么彻底卸载。
category: 入门
order: 2
---

## 下载

| 渠道 | 地址 | 适合 |
| --- | --- | --- |
| GitHub Releases | <https://github.com/OrangeArtc0915/SVE_mananger/releases> | 海外线路，更新最先到这里 |
| Gitee 发行版 | <https://gitee.com/orangearc655743/SVE_mananger_File/releases> | 国内镜像，包的内容完全一样 |

下载文件名形如 `StardewLauncher-v1.6.0-win-x64.zip`。

## 安装

启动器是**绿色软件**，没有安装程序：

1. 把压缩包解压到任意目录，例如 `D:\StardewLauncher`
2. 双击 `StardewLauncher.exe`

> 建议放在**你的用户目录或非系统盘**，避免放到 `Program Files` —— 那里写入需要管理员权限，而启动器是以普通用户身份运行的。

发行包内容：

```
StardewLauncher.exe   启动器本体（单文件、自带 .NET 8 运行时）
README.md             说明文档
NOTICE                第三方依赖与参考来源说明
LICENSE               MIT 许可证
```

联机用的樱花 FRP 客户端 frpc **不在包内**，由程序在你点「准备 frpc」时从樱花 FRP 官方接口下载，并用接口返回的 MD5 校验完整性。

## 数据目录

首次运行会在 exe 同级目录生成 `Data\`：

```
Data\
  Settings.json               所有设置（含各种密钥，见下）
  instances\                  实例定义：每个实例一个 json
    <id>.json                 实例配置（用哪个游戏目录、是否走 SMAPI）
  save-backups\               存档备份：每个存档一份目录，里面按时间排
  profiles\                   Mod 配置档：一档一个 json
  Cache\                      缓存
  Log\                        落盘日志
  Background\                 你导入的背景素材副本
  SakuraFrp\                  frpc 客户端（用到联机时才下载）
  WebView2\  WebView2-Sakura\  WebView2-Wiki\  内嵌浏览器的缓存
```

还有两处数据在数据目录之外：

| 位置 | 内容 |
| --- | --- |
| `%LOCALAPPDATA%\StardewLauncher\Downloads` | 下载中的临时文件（含断点续传的进度文件） |
| `%TEMP%\StardewLauncher` | 解压、安装过程中的临时文件 |

> 想让整个数据目录改到别处（比如做成便携版放 U 盘），设置环境变量 `STARDEWLAUNCHER_DATA` 指向新目录即可，启动器所有数据都会写到那里。

## 关于密钥

设置里有两类密钥，都**只保存在 `Data\Settings.json` 里**，不会上传、不会写进日志、不会进版本库，界面上只显示掩码：

- **Nexus API 密钥**：用于查询 Mod 详情与在线下载，可选
- **樱花 FRP 访问密钥**：用于开联机隧道，等价于账号密码，请勿分享

## 卸载

1. 关闭启动器
2. 删除它的文件夹

存档不在启动器目录里（默认在 `%appdata%\StardewValley\Saves`），所以删掉启动器**不会影响存档**。想连实例配置一起清掉，直接把整个文件夹删掉即可。
