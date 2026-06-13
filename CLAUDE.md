# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## 项目概述

Windows 桌面番茄钟应用，C# WinForms + WebView2 架构。UI 通过嵌入的 HTML/CSS/JS 渲染，C# 端负责窗口管理、系统托盘、原生通知。

## 编译命令

```
C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe /target:winexe /out:番茄钟.exe /r:Microsoft.Web.WebView2.Core.dll /r:Microsoft.Web.WebView2.WinForms.dll Main.cs
```

依赖：需安装 [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)（Windows 11 自带，Win10 需手动安装）。

## 架构

### 当前架构（单文件 `Main.cs`，~285 行）

```
Main.cs
├── Program.Main()           — 单实例互斥体入口
├── PomodoroForm             — 主窗体 + 系统托盘 + WebView2 宿主
│   ├── InitForm()           — 无边框窗口、自定义标题栏（拖拽/最小化/关闭）
│   ├── InitTray()           — 托盘图标、右键菜单（显示/置顶/退出）
│   ├── InitWebView2()       — 初始化 WebView2 环境，注册 C# ↔ JS 消息桥
│   └── LoadHtml()           — 优先读外部 AppPage.html，否则用内嵌 HTML
├── GetEmbeddedHtml()        — 内嵌完整 HTML UI（4 个页面：计时/任务/统计/设置）
├── GetEmbeddedJs()          — 内嵌 JS 逻辑（计时器、状态管理、localStorage 持久化）
└── NativeMethods            — user32.dll P/Invoke（窗口拖拽）
```

**C# ↔ JS 通信协议**（`chrome.webview.postMessage` ↔ `WebMessageReceived`）：

| 消息 | 方向 | 作用 |
|------|------|------|
| `tray:<文本>` | JS→C# | 更新托盘 tooltip |
| `top-on` / `top-off` | JS→C# | 切换窗口置顶 |
| `hide-tray` | JS→C# | 最小化到托盘 |
| `bell` | JS→C# | 播放提示音 |
| `notify:<标题>|<内容>` | JS→C# | 弹出托盘气泡通知 |

### 重构计划（进行中）

计划将单文件拆为 4 文件清晰分层（详见 `C:\Users\1\.claude\plans\elegant-petting-cray.md`）：

| 文件 | 职责 |
|------|------|
| `Main.cs` | 入口 + MainForm 外壳（TabControl 导航、系统托盘、窗口管理） |
| `Models.cs` | 数据模型（TaskItem、PomodoroRecord、AppConfig） |
| `Pages.cs` | 5 个页面面板（计时器、任务管理、统计、设置、历史记录） |
| `Services.cs` | 数据持久化、音频管理、主题管理、GDI+ 图表绘制 |

重构后移除 WebView2 依赖，改用纯 GDI+ 自绘 UI（减小体积、消除外部依赖）。

### 数据持久化

当前使用 WebView2 的 `localStorage`（JS 端），key 列表：
- `pm-config` — 计时/休息时长、长休间隔、置顶开关
- `pm-tasks` — 任务列表 JSON
- `pm-records` — 番茄完成记录 JSON
- `pm-today` — 今日日期 + 番茄计数（`日期|数量`）

## 设计规范

深色主题色板：

| 用途 | 色值 | 说明 |
|------|------|------|
| 窗口背景 | `#0f0f14` / `#1e1e2e` | 最深 |
| 卡片/表面 | `#1c1c2a` / `#2a2a3c` | 次深 |
| 边框/分隔 | `#252536` / `#3c3c54` | 最浅 |
| 主文字 | `#e8e8f0` | 高明度 |
| 次要文字 | `#8a8aa0` / `#a0a0b8` | 中明度 |
| 专注红 | `#ff6b6b` | 工作模式 |
| 短休青 | `#4ecdc4` | 短休息 |
| 长休蓝 | `#5b8def` | 长休息 |

## 注意事项

- 程序**不依赖外部 HTML 文件** — `AppPage.html` 不存在时自动使用内嵌 HTML，确保 exe 可单独分发
- 使用 `Global\PomodoroApp_S` 互斥体保证单实例运行
- 关闭窗口默认最小化到托盘，不退出进程；首次关闭弹出提示对话框
- `.gitignore` 已排除 `claude-settings.json` 和 `statusline.py`（个人配置文件，不属项目代码）
