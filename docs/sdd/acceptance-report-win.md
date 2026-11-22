# GravityBox for Windows 移植与验收报告

- **版本基线**：`GravityBox v1.1.3`
- **目标平台**：Windows 10 / Windows 11 (`x64`)
- **分支**：`feat/windows-port`
- **规格基准**：`docs/sdd/01-requirements.md`、`docs/sdd/02-design.md`、`docs/sdd/03-execution.md`

---

## 1. 架构与核心红线核验

| 红线 / 架构要求 | 实现与核验结论 |
| :--- | :--- |
| **零改包 (`INV-01`)** | 绝不解包/篡改/重签名 `Antigravity.exe` 与 `app.asar`；全部汉化与 HUD 通过 `127.0.0.1` 回环 CDP 隔离世界运行。 |
| **零遥测 (`INV-02`)** | 无第三方统计/上报 SDK；远端价格同步与自动更新默认关闭。 |
| **内容隔离 (`INV-03`)** | 对话正文、代码块、终端、文件名、项目名与品牌词受隔离保护，不入词典候选。 |
| **零数据丢失与可逆性 (`INV-04`)** | 切号采用 `node:sqlite` (`DatabaseSync`) `BEGIN IMMEDIATE` 四阶段事务与 7 天回滚；缓存清理前置归档并支持一键原位还原；快捷方式参数备份至 `shortcut-backups.json` 并支持无损还原；安全重启仅发 `WM_CLOSE`，超时绝不强杀。 |
| **仅管官方正式版 (`INV-05`)** | 排除 Insiders 路径与快捷方式，并在切号/清理前拦截冲突进程。 |
| **默认简体中文 (`INV-06` / `FR-001`)** | 全新安装无论 Windows 系统语言为何，默认 `appLanguage = "zh-Hans"`，同步激活中文控制台 + 热汉化 + 中文 HUD，支持热切 `system` / `en`。 |
| **零第三方生产依赖 (`ADR-008`)** | `package.json` `dependencies` 保持为空，无 `node-gyp`/C++ 原生扩展。 |

---

## 2. 自动化门禁与实机验证记录

### 2.1 全量 Windows 门禁（`node tools/verify.mjs --no-vendor`）
- **隐私门禁 (`node tools/check-privacy.mjs`)**：通过（扫描全部仓库文件，0 真实邮箱或敏感标识泄露）。
- **.NET 8 构建与单元测试 (`dotnet test native-win/GravityBox.sln -c Release`)**：**28/28 通过**（0 警告、0 错误），覆盖：
  - `Win32ProcessNetwork`（Toolhelp32、64 位 PEB 命令行与环境变量读取、`GetExtendedTcpTable`、`WindowCloser`、`JobObjectGuard`）
  - `Win32SecurityCred`（`WinVerifyTrust`、`CredentialStore`、`SecurityAclHelper`、`RecycleBinHelper`）
  - `Win32Shortcut`（COM `IShellLinkW` 扫描/创建/更新/Insiders 过滤、`SystemLocaleHelper`）
  - `CoreServices`（`BackendBridge` Named Pipe + `ipc.token`、日志轮转、`ConfigurationStore` 未知字段保留、`L10nService`、`ThemeService`）
  - `LifecycleSync`（`WM_CLOSE` 超时零强杀、安全重启、`HKCU\Run` 免 UAC 自启、抑制期保护）
  - `UpdateManager`（SemVer 比较、SHA-256 sidecar 校验与篡改拦截）
  - `TrayFlyout` / `Overview` / `Accounts` / `Usage` / `Settings` ViewModel 契约与多屏高 DPI 定位算法
- **Node.js 后端单元与 Windows 专项集成 (`node tools/run-tests.mjs --win`)**：**30/30 通过**，覆盖路径沙箱（含中文与空格路径）、文件原子写、应用探测、`node:sqlite` 事务、Named Pipe IPC、`WinHelperClient` stdio JSON-RPC（暖进程 < 10ms）、默认简体中文启动、`dict/win32.json` 分层及快捷方式服务。
- **Headless Chromium CDP E2E (`node test/browser.mjs`)**：**61/61 检查通过**（`rssMB=114.4`）。
- **全版本词典门禁**：`2.23.0`–`2.33.0` 全部 **14 个**版本快照 100% 通过。

### 2.2 实机只读探针（`node tools/win/smoke-probe-win.mjs`）
- 官方目录探测命中 `Antigravity 2.34.0`。
- `inspectBundle` 验真通过（`ProductName="Antigravity"`, `CompanyName="Google"`, `FileVersion="2.34.0"`），已将 Windows `2.34.0` `main.js` 指纹登记至 `src/bundle.ts` `PLATFORM_VERIFIED_BUNDLES`。
- `discover()` 命中 `127.0.0.1` 回环 CDP 端点；`probeLiveLanguageServer` 成功返回实时订阅等级（`PRO`）与 24 个模型配额；`Win32CredentialAdapter` 凭据读取正常。

### 2.3 打包产物（`npm run package:win`）
- 自包含 `.NET 8 win-x64` 发布目录：`build/win-stage/GravityBox/`（含 `GravityBox.exe`、`GravityBox.WinHelper.exe`、`runtime/node.exe`、`core/dist/`、`dict/`、`assets/`）。
- 便携压缩包与校验文件：`成品/GravityBox-1.1.3-win-x64-portable.zip` 与 `.sha256` sidecar 校验通过。
- Inno Setup 免 UAC 安装包脚本：`tools/win/installer.iss`（注：本机未预装 `ISCC.exe`，CI Windows Release Job 可直接编译出 `setup.exe`）。

### 2.4 文档一致性检查
- `node tools/check-docs.mjs --source-only`：通过（0 不一致）。
