# GravityBox 工程架构总览

## 核心定位
GravityBox (`1.1.3`) 是基于上游 [`Enzoo404/gravitybox`](https://github.com/Enzoo404/gravitybox)（MIT）移植并增强的轻量级 Google Antigravity 正式版跨平台伴侣工具箱，面向 **Windows 10/11 (`x64`, `.NET 8 WPF`)** 与 macOS 14+ (Apple Silicon `arm64`, AppKit)，在 Windows 端提供**开箱默认简体中文 + 无感启动快捷方式**、**原生 WPF / Node 后端 / HUD 全链路中英双语界面**、零侵入式 IDE 双语热适配、输入框实时会话遥测 HUD、双周期配额监控、多账号四阶段原子切换（Windows 集成 DPAPI 凭据加密）、只读 SQLite 用量与时序计费核算及非破坏性缓存维护。

## 目录与模块分工
- `native-win/`：Windows 10/11 原生客户端解决方案（`GravityBox.App` .NET 8 WPF 5 页主控制台 + 系统托盘悬浮窗、`GravityBox.Win32` DPAPI/JobObject/快捷方式互操作、`GravityBox.WinHelper` 轻量无感启动与卸载恢复助手、`GravityBox.Tests` xUnit 测试套件）
- `native/`：上游 macOS 原生 AppKit 前端（常驻菜单栏面板、5 页主控制台、`L10n.swift` 本地化运行时、进程守护与自更新校验）
- `src/`：Node 22 / TypeScript 跨平台核心后端（`src/platform/` 平台抽象层 PAL、Named Pipe / Unix Socket IPC、CDP 注入控制、应用级双语引擎 `i18n.ts`、账号切换事务、双周期配额聚合、`sqlite-worker` 只读投影、时序计费与重估、`metrics-engine` HUD 引擎）
- `runtime/`：注入 Isolated World 的零侵入 DOM 观察引擎（`engine.js`）与双语会话遥测 HUD 实例（`hud-i18n.js` / `hud-instance.js` / `hud-popover.js` / `hud-styles.js`）
- `dict/` & `ui-strings/`：分层词典与基于官方正式版 SHA-256 指纹提取的界面文案基准（覆盖 `2.23.0` ~ `2.34.0`）
- `assets/`：图标（含 Windows `.ico` / 托盘图标）、应用级双语语言包（`i18n/en.json` / `i18n/zh-Hans.json`）、实机截图（含 `screenshots/win/` Windows 浅/深色截图）、公开模型目录与时序定价基准（`model-catalog.json` / `pricing-feed.json`）
- `tools/` & `tools/win/`：构建（`clean-dist` / `copy-runtime` / `win/build-win`）、价格同步（`sync-pricing-feed`）、隐私与文档门禁（`check-privacy` / `check-docs`）、全量门禁（`verify`）及安装包生成（`win/package-win` / `win/installer.iss` / `package-mac`）
- `docs/sdd/`：Windows 移植 Spec-Driven Development 完整文档（需求规格、架构设计、执行计划）
- `test/`：Node 单元与集成测试、Windows 平台契约测试（`--win`）、中英双语测试、无头 Chromium 浏览器回归（`test/browser.mjs`）及原生测试套件
