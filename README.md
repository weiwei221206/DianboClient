<div align="center">

<img src="assets/logo.png" alt="点波音乐 Logo" width="100" height="100" />

# 点波音乐


基于 **WinUI 3** 的 Windows 第三方波点音乐客户端。

</div>

---

## 说明

本项目使用第官方服务接口，接口行为可能变化。请遵守相关服务条款，并仅播放自己有权访问的内容。仅供学习交流，严禁用于商业用途。

---

## 项目介绍

点波音乐是一个独立的桌面音乐播放器，基于官方接口在WinUI3开发，提供音乐发现、搜索、播放、歌单管理和同步歌词等功能。它通过独立的 mpv 音频后端播放歌曲，界面和播放控制均由本项目实现。

本项目与波点音乐官方无关联。歌曲能否播放以及可用音质取决于服务端返回的地址和当前账号权益。

---

## 下载与使用

从 Releases 下载 `点波音乐-v<版本>-win-x64.zip`，解压后的目录：

---



---

## 效果展示

<!-- 截图预览待补充 -->

---

## 功能

自己下载去看看，懒得写了

---

## 第三方组件与许可

mpv 以**独立进程**方式调用：程序只使用它的命令行参数与 JSON IPC 接口，不链接其代码，也不修改其二进制。

| 组件 | 版本 | 许可 | 备注 |
|---|---|---|---|
| [mpv](https://github.com/mpv-player/mpv) | v0.41.0-dev-ga1f50f2c3 | GPL-2.0-or-later | 随发布包分发，位于 `app\mpv\` |
| .NET 运行时 | 10.0.12 | MIT | 自包含发布，用户无需另装 |
| [Windows App SDK](https://github.com/microsoft/WindowsAppSDK) | 2.5.1 | MIT | WinUI 3 界面与运行时 |

 **播放后端**：`app\mpv\mpv.exe`（mpv v0.41.0-dev-ga1f50f2c3）

mpv 二进制取自官方 CI 发布 [git-release](https://github.com/mpv-player/mpv/releases/tag/git-release)，固定版本为 `mpv-v0.41.0-dev-ga1f50f2c3-36640285359-x86_64-pc-windows-msvc.zip`（SHA-256 `5cea8bd5e60ac1e93e209b55e995342161863c2fe3e066ff2cff6d45efe423ff`）；对应源码可从 commit [`a1f50f2c3`](https://github.com/mpv-player/mpv/commit/a1f50f2c3) 取得。


---
