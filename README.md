<div align="center">

# DeskFrzA

**定格此刻，随时归位。**  
现代、极简的 Windows 桌面图标布局记忆与多屏恢复工具。

[**中文**](README.md) | [**English**](README_EN.md)

[![Latest Release](https://img.shields.io/github/v/release/flackoye/DeskFrzA?include_prereleases&label=Release&color=2f4f4f)](https://github.com/flackoye/DeskFrzA/releases)
[![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011%20(x64)-blue)](https://github.com/flackoye/DeskFrzA/releases)
[![License](https://img.shields.io/github/license/flackoye/DeskFrzA?color=brightgreen)](LICENSE)

[**下载安装版**](https://github.com/flackoye/DeskFrzA/releases/latest/download/DeskFrzA-Setup.exe) · [**下载便携版**](https://github.com/flackoye/DeskFrzA/releases/latest/download/DeskFrzA-Portable-win-x64.zip) · [**所有版本**](https://github.com/flackoye/DeskFrzA/releases)

</div>

---

## 为什么需要 DeskFrzA？

插拔外接显示器、切换主副屏或分辨率变化后，精心摆好的桌面图标常常被系统瞬间打乱。

**DeskFrzA** 像 DesktopOK 一样只专注做好“桌面图标记忆与恢复”这一件事，但带来了更现代的界面与更省心的多屏适配体验：

- 🧊 **一键定格与归位**：记录当前桌面图标位置，打乱后随时一键恢复。
- 🖥️ **懂你的多屏切换**：单屏、双屏、多屏布局分类保存；切换主副屏或更换显示器时，支持**跨屏迁移预览**与等比自适应。
- 💻 **拔屏自动恢复**：断开外接显示器回到单屏时，自动切换回你设定的**默认单屏布局**。
- 🛡️ **安全无损、可撤销**：恢复时自动保留桌面新增文件并避让冲突；每次恢复前自动备份，随时可**撤销上次恢复**。
- 🍃 **纯粹、轻量、本地化**：无广告、无多余功能，所有布局数据仅保存在本机。

<div align="center">
  <img src="docs/images/dashboard.png" alt="DeskFrzA Dashboard" width="680" />
</div>

## 下载与安装

| 版本 | 说明 | 下载链接 |
| :--- | :--- | :--- |
| **安装版 (Setup)** | 支持自定义路径、桌面快捷方式及应用内一键更新 | [DeskFrzA-Setup.exe](https://github.com/flackoye/DeskFrzA/releases/latest/download/DeskFrzA-Setup.exe) |
| **便携版 (Portable)** | 解压即用，直接运行 `DeskFrzA.exe` | [DeskFrzA-Portable-win-x64.zip](https://github.com/flackoye/DeskFrzA/releases/latest/download/DeskFrzA-Portable-win-x64.zip) |

> 注：当前为 **0.1.15 预览版**。若上方直链暂未生效，请前往 [Releases 页面](https://github.com/flackoye/DeskFrzA/releases) 下载最新预发布包。

## 快速上手

1. **设默认**：首次打开，将当前整理好的单屏桌面保存为 **默认单屏布局**。
2. **存布局**：外接显示器或调整桌面后，点击 **保存布局** 随时定格。
3. **秒恢复**：在记录列表中点击 **恢复布局**（跨屏时可先预览效果再确认）。

## 使用须知

- **系统要求**：Windows 10 (1703+) / Windows 11 x64。如提示缺少运行库，请安装微软官方 [WebView2 Runtime](https://developer.microsoft.com/en-us/microsoft-edge/webview2/)。
- **自动恢复**：拔屏自动归位需保持应用运行；重新打开应用时也会自动检测当前单屏状态。
- **行为边界**：恢复图标不会修改你的 Windows 主显示器设置、缩放比例或图标大小。
- **数据隐私**：布局存储于本地 `%LOCALAPPDATA%\DeskFrz`，卸载应用默认保留历史布局记录。

## 文档与开发

- [恢复与迁移规则说明](docs/RESTORE.md)
- [本地构建与发布指南](docs/DEVELOPMENT.md)
- [多屏场景验证记录](docs/VALIDATION.md)

基于 .NET 8 与原生前端构建，采用 [MIT License](LICENSE) 开源。
