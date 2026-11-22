# 第三方材料与开源声明 (Third-Party Notices)

本项目（[`Rss3208/gravitybox-win`](https://github.com/Rss3208/gravitybox-win)）是基于上游开源项目 [`Enzoo404/gravitybox`](https://github.com/Enzoo404/gravitybox)（MIT License）移植并增强的 **Windows 10/11 原生版**（同时保留上游 macOS 代码与完整跨平台能力）。为了确保开源合规与清晰的版权隔离，特对上游归属、第三方组件许可、运行环境与商标作如下声明：

## 1. 上游开源项目归属 (Upstream Attribution)

- **上游项目**：[GravityBox](https://github.com/Enzoo404/gravitybox)
- **原作者**：Enzoo404 (`Copyright (c) 2026 Enzoo404`)
- **开源协议**：[MIT License](./LICENSE)
- **本仓库二次开发内容** (`Copyright (c) 2026 Rss3208`)：
  - 基于 `.NET 8 / C# WPF` 的 Windows 10/11 原生客户端（`native-win/GravityBox.App`）、Win32 互操作层（`native-win/GravityBox.Win32`）及轻量辅助程序（`native-win/GravityBox.WinHelper`）；
  - 跨平台后端平台抽象层（`src/platform/`）：Windows Named Pipe IPC、DPAPI 凭据加密、Antigravity 进程与环境变量探针、`WScript.Shell` / COM 快捷方式管理与一键无损恢复；
  - Windows 端默认简体中文体验（`zh-Hans` 开箱即用）与无感启动快捷方式向导；
  - Windows 打包流水线（自包含便携 ZIP 与 Inno Setup 每用户安装程序）及 Windows SDD 规格文档（`docs/sdd/`）。

本项目完整保留上游 `LICENSE` 版权声明与 Git 提交历史，并继续以 **MIT License** 开源发布。

## 2. 词典架构与独立实现 (Dictionary Architecture)

GravityBox 的基础词典与版本分层词典结构（`dict/`，按功能与版本分层）、文案提取与动态插值模板引擎，以及基于官方发布包 SHA256 指纹的覆盖率校验均为上游及本项目独立设计与维护实现。

## 3. 官方界面包与版权隔离 (Vendor Isolation)

- Antigravity 官方应用的界面包（JavaScript bundle / ASAR）版权归原权利人所有。
- **版权隔离措施**：本项目严格遵循版权隔离原则，`vendor/` 目录已被彻底移出 Git 版本控制并加入 `.gitignore`。开源仓库中不包含、亦不分发任何官方二进制与未经授权的代码副本。
- 本项目仅在 `ui-strings/` 目录下分发经公开合法接口提取的文案键值结构与官方指纹元数据，并在 `tools/verify.mjs` 和 `tools/coverage-report.mjs` 中提供免 vendor 校验模式，供开源社区参与词典维护与对账。

## 4. 运行环境与第三方开源依赖 (Runtime & Dependencies)

### Node.js 运行环境
- Windows 发布包内置构建时复制的 Windows (`win-x64`) Node.js 22 运行环境，macOS 安装包内置 Apple Silicon (`arm64`) Node.js 22 运行环境。
- 随包分发其官方 LICENSE（包含 Node.js、V8、libuv 等第三方组件许可，位于本仓库 [`licenses/NODE-LICENSE`](./licenses/NODE-LICENSE) 与发布包内 `licenses/NODE-LICENSE`）。
- 来源：[Node.js 官方网站](https://nodejs.org/)（MIT License）。

### Windows 客户端开源组件
- **.NET 8 Runtime & WPF** — Microsoft Corporation — [MIT License](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT)
- **CommunityToolkit.Mvvm** (`8.2.2`) — .NET Foundation and Contributors — [MIT License](https://github.com/CommunityToolkit/dotnet/blob/main/License.md)
- **Inno Setup**（仅用于构建 Windows 每用户安装程序 `setup.exe`）— Jordan Russell — [Inno Setup License](https://jrsoftware.org/files/is/license.txt)

### 开发与测试依赖（不随应用分发）
- **TypeScript** (`^5.x`) — Microsoft Corporation — Apache-2.0 License
- **xUnit.net** (`2.9.0`) & **Microsoft.NET.Test.Sdk** — .NET Foundation / Microsoft — Apache-2.0 / MIT License

## 5. 商标与品牌免责声明 (Trademark Disclaimer)

- **Google**、**Antigravity**、**Gemini**、**Google Cloud Platform** 均为 Google LLC 的商标或注册商标。
- **Claude** 为 Anthropic PBC 的商标或注册商标。
- **OpenAI**、**ChatGPT** 为 OpenAI Inc. 的商标或注册商标。
- **Microsoft**、**Windows**、**.NET** 均为 Microsoft Corporation 的商标或注册商标。
- **Apple**、**macOS**、**AppKit**、**SF Symbols** 均为 Apple Inc. 的商标或注册商标。

**免责声明**：GravityBox 是一个独立的第三方开源工具箱，与 Google LLC、Anthropic PBC、OpenAI Inc.、Microsoft Corporation 或 Apple Inc. 无任何形式的官方隶属、合作、关联、赞助或背书关系。项目中出现的商标名称、品牌标识与模型识别图标仅用于技术事实性描述、功能兼容指引及模型供应商识别（Nominative Fair Use），其合法权利归各自权利人所有。
