# GravityBox for Windows — 需求规格说明书（SRS / PRD）

> **文档编号**：`SDD-REQ-WIN-001`  
> **基线版本**：对齐 `GravityBox v1.1.3`（macOS 上游提交 `1b6e0c8`）  
> **目标平台**：Windows 10（22H2+）/ Windows 11（`x64`，架构预留 `arm64`）  
> **桌面端技术栈**：`.NET 8 / C#` 原生桌面壳（WPF + Windows 11 Fluent/Mica 风格）+ 复用原版 `Node 22 / TypeScript` 核心引擎  
> **文档用途**：作为 Spec-Driven Development（SDD）的唯一需求基线（Single Source of Truth），所有设计与实现必须可追溯至本文件编号条目。

---

## 1. 项目背景与核心目标

### 1.1 背景
`GravityBox v1.1.3` 是面向 macOS 14+（Apple Silicon）的 Google Antigravity 原生伴侣应用，核心由两部分构成：
- **核心后端与注入引擎（`src/` + `runtime/` + `dict/` + `assets/`）**：基于 Node 22 + TypeScript，零第三方运行时依赖，已具备完整的 CDP 隔离世界热汉化、对话状态栏 HUD、双周期配额聚合、只读 SQLite 用量总账与时序计费、多账号四阶段原子切换及可撤回缓存维护能力。
- **桌面原生前端（`native/`）**：基于 Swift 6 / AppKit 实现常驻菜单栏浮窗、5 页主控制台、进程守护与自更新器。

当前仓库中存在大量 macOS 专属耦合（`/Applications/Antigravity.app`、`/bin/ps`、`/usr/sbin/lsof`、`/usr/bin/security`、`/usr/bin/sqlite3`、`/usr/bin/codesign`、`/usr/libexec/PlistBuddy`、Unix Domain Socket `control.sock`、POSIX `uid` 校验、`~/.Trash` 与 Swift/AppKit UI），导致无法在 Windows 平台构建与运行。

### 1.2 核心移植目标
1. **全功能对齐 macOS v1.1.3**：在 Windows 10/11 上完整交付零改包热汉化、对话状态栏 HUD、双周期配额监控、多账号安全管理与原子切换、只读 SQLite 用量核算与价格表热更新、可撤回缓存维护、系统托盘快捷浮窗及 5 页原生主控制台。
2. **开箱默认简体中文 + 无感启动汉化**：
   - 无论 Windows 系统语言为何，首次安装启动默认启用简体中文（GravityBox 界面 + Antigravity IDE 热汉化 + 中文 HUD），同时保留随时切换至「跟随系统 / English」的能力。
   - 解决 Windows 用户日常通过桌面/开始菜单/任务栏快捷方式打开 Antigravity 缺少 `--remote-debugging-port=0` 参数导致汉化未自动生效的核心痛点，提供**可选、透明、一键可还原的快捷方式参数接管**与**优雅安全重启接管**。
3. **纯 Windows 原生体验**：采用 `.NET 8 / C#` 原生桌面应用（对标原版 Swift/AppKit），无缝融入 Windows 11 深浅色外观、高 DPI 多屏缩放、系统托盘浮窗与 Win32 生命周期管理。
4. **跨平台核心共生（零破坏上游 macOS）**：通过平台抽象层（PAL）隔离 OS 差异，`src/` 与 `runtime/` 核心逻辑复用率 ≥ 90%，不破坏既有 macOS 源码结构与测试契约。

### 1.3 不可违背的红线原则（Product Invariants）
| 原则 | 强制约束 |
| :--- | :--- |
| **INV-01 零改包原则** | 严禁解包、修改、重签名或替换官方 Antigravity 的 `Antigravity.exe`、`app.asar` 或任何安装目录文件；IDE 汉化与 HUD 仅通过本机回环（`127.0.0.1`）CDP 隔离世界运行。 |
| **INV-02 零遥测零外联** | 严禁集成第三方埋点或崩溃上报 SDK；未获用户主动开启或触发前，禁止任何后台静默外联请求（自动更新检查、远端价格同步默认均为关闭）。 |
| **INV-03 用户内容绝对隔离** | 输入框草稿、对话正文、代码块、终端输出、文件名、项目名与品牌词受 DOM 选择器与白名单严格保护，绝不被汉化引擎改写。 |
| **INV-04 零数据丢失与可逆性** | 切号前必须确认 Antigravity 安全退出，采用四阶段事务与崩溃自愈日志；缓存清理前必须完成用量数据库归档，采用可撤回保险库移动并支持一键原位还原；快捷方式修改必须保留原始元数据并支持一键还原。 |
| **INV-05 仅管理官方正式版** | 仅连接与管理官方正式版 `Antigravity`；若检测到 Insiders 预览版进程运行，切号与缓存写操作必须安全拦截以避免共享凭据损坏，但不篡改 Insiders 安装。 |
| **INV-06 纯本地用户隔离** | 所有账号凭据、SQLite 账本、词典、恢复点与 Named Pipe 均受当前 Windows 用户 SID ACL 保护，禁止其他本地用户读取或注入。 |

---

## 2. 目标运行环境与兼容性矩阵

| 维度 | 规格要求 | 说明 |
| :--- | :--- | :--- |
| **操作系统** | Windows 10 22H2 (Build 19045+) / Windows 11 22H2+ | 需支持深浅色主题、现代系统托盘、DPAPI / CredMan、Authenticode 校验 |
| **CPU 架构** | `win-x64`（首发必选）；`win-arm64`（架构兼容、后续 CI 补齐） | 与内置 Node 22 运行时架构一致 |
| **托管桌面框架** | `.NET 8.0`（`net8.0-windows10.0.19041.0`） | 发布版支持 Self-Contained 零外部依赖分发 |
| **内置核心引擎** | `Node.js >= 22.15.0 < 23`（随安装包内置 `node.exe`） | 强依赖 Node 22 内置 `node:sqlite` (`DatabaseSync`) 与 `worker_threads` |
| **目标 IDE** | 官方正式版 Google Antigravity for Windows | 覆盖常规每用户安装目录 `%LOCALAPPDATA%\Programs\Antigravity\` 与系统级安装目录 `%ProgramFiles%\Antigravity\`，并支持高级配置自定义路径 |
| **DPI 与多屏** | 100% / 125% / 150% / 175% / 200% Per-Monitor DPI V2 | 托盘浮窗与主控制台在副屏/不同缩放比下不裁切、不偏移、不模糊 |

---

## 3. 核心用户旅程（User Journeys）

### UJ-01：首次安装与开箱默认汉化
1. 用户安装并启动 GravityBox for Windows。
2. 无需任何手动语言设置，GravityBox 主控制台与托盘浮窗即以**简体中文**呈现，后端自动处于中文汉化就绪状态。
3. 若本机检测到官方 Antigravity 快捷方式尚未附带调试端口参数，主控制台顶部或首次引导卡片展示**轻量非阻断提示**：「开启无感启动汉化（为桌面/开始菜单快捷方式附加本机回环调试参数，不修改官方程序，可随时一键还原）」。
4. 用户点击「一键开启」或直接点击「打开 Antigravity」后，Antigravity 启动并在 ≤ 2 秒内自动完成全界面中文汉化与输入框下方中文 HUD 挂载。

### UJ-02：日常无感启动汉化
1. GravityBox 已开启开机自启或驻留系统托盘。
2. 用户按日常习惯直接双击桌面或任务栏上的 Antigravity 图标启动 IDE。
3. 快捷方式自动附带 `--remote-debugging-port=0`，Antigravity 在 `%APPDATA%\Antigravity\DevToolsActivePort` 写入随机回环端口。
4. GravityBox 文件监听立即捕获变更，完成签名与进程验真后自动挂载隔离世界，用户无需操作任何按钮即可直接使用全中文 IDE 与实时 HUD。

### UJ-03：外部无调试端口启动时的安全重启接管
1. 用户未开启快捷方式接管，直接从外部启动了 Antigravity。
2. GravityBox 检测到 `Antigravity.exe` 正在运行但无有效 `DevToolsActivePort`，状态切换为 `debug-port-missing`（「未开启调试端口」）。
3. 主控制台总览页与托盘浮窗顶部均显示引导横幅，提供「安全重启以连接」按钮。
4. 用户点击后，GravityBox 向 Antigravity 主窗口发送优雅关闭消息（`WM_CLOSE`），等待窗口与子进程安全退出（超时 10 秒绝不强杀并提示用户手动保存退出），退出后自动以 `--remote-debugging-port=0` 重新拉起并建立连接。

### UJ-04：多账号安全切换与异常回滚
1. 用户在账号页查看各账号 5 小时/每周配额及智能推荐候选，点击目标账号「切换」。
2. 后端执行 `account_switch_preflight` 预检：检测 Antigravity 与 Insiders 进程状态、CDP 活跃生成/智能体任务及 `.pb` 写入活跃度。
3. 若存在活跃生成任务，阻断并提示原因；用户确认退出 Antigravity 后，GravityBox 优雅关闭 IDE，执行四阶段原子切换（SQLite `state.vscdb` 事务 + Windows 凭据管理器同步 + 账号索引原子写入 + `agy` CLI 凭据非侵入同步）。
4. 切号完成后自动带调试端口重拉起 Antigravity；若任一阶段失败，自动依据日志原子回滚至原账号；成功切号后 7 天内可随时在账号页「撤销上次切换」。

### UJ-05：配额监控、用量核算与价格表热更新
1. 用户通过系统托盘图标点击唤起无边框三标签浮窗（用量 / 配额 / 账号），`Ctrl+1/2/3` 极速切换，失焦自动收起。
2. 写代码时直接在 Antigravity 输入框下方查看三联 HUD（对话轮次与速度 / Token 与缓存命中率 / 上下文水位与压缩徽章），悬停或点击展开明细卡片。
3. 在主控制台「用量」页按今日 / 近 7 天 / 近 30 天 / 全部查看趋势图、模型占比环图（自动折叠思考档位变体）与等效 API 费用，并支持检索/导出请求流水、在线同步最新官方定价表与历史账单重估/回滚。

### UJ-06：可撤回缓存瘦身与一键原位还原
1. 用户进入「设置 → 存储与维护」，查看空间组成条（运行缓存 / 超期后台日志 / 受保护核心数据）。
2. 默认勾选无损运行缓存，按需勾选超期历史日志（保留 7 天 / 30 天 / 全部非活跃）。
3. 点击清理前，若存在活跃对话或 Antigravity 运行中则触发预检保护；清理前自动归档待移动的 Subagent 数据库事件至总账；所有被清对象按事务清单移至本地恢复保险库（或系统回收站关联视图），并支持一键原位还原与崩溃后自动对账恢复。

---

## 4. 功能性需求规格（Functional Requirements）

### 4.1 默认汉化与无感启动汉化系统

#### `FR-001` 开箱默认简体中文策略
- **描述**：全新安装（无历史 `config.json` 与 `runtime-preferences.json`）启动 GravityBox for Windows 时，默认语言配置必须为 `zh-Hans`，而非因英文 Windows 系统导致回退为 `en` 暂停汉化。
- **输入与状态优先级**：
  1. 显式命令行参数 `--locale <code|system>`（最高优先级，供测试与高级启动覆盖）。
  2. 已持久化的 `config.json.appLanguage` 或 `runtime-preferences.json.language`。
  3. 兼容旧版 `runtime-preferences.json.translationEnabled`（`false` → `en`，`true` → `zh-Hans`）。
  4. **全新安装默认值**：`configuredLanguage = "zh-Hans"`，`resolvedLocale = "zh-Hans"`，`translationEnabled = true`，`hudEnabled = true`。
- **行为约束**：
  - 用户可在「设置 → 显示语言」或托盘浮窗 `···` 菜单中自由切换 `简体中文 (zh-Hans)`、`跟随系统 (system)`、`English (en)` 及后续新增语言包，无需重启即热生效。
  - 选择 `system` 时，后端与前端必须一致读取 Windows 用户首选 UI 语言列表（`GetUserPreferredUILanguages` / `CultureInfo.CurrentUICulture`）并按回退链解析。
  - 选择 `en` 时，自动恢复 Antigravity 官方英文界面，同时保持英文版 HUD 独立工作（当 HUD 开关开启时）。
- **验收标准（AC）**：
  - `AC-001.1`：在系统语言为 `en-US` 的全新 Windows 环境启动 GravityBox，首次读取 `status` 返回 `locale === "zh-Hans"`、`configuredLanguage === "zh-Hans"`、`translationEnabled === true`。
  - `AC-001.2`：切换为 `English` 后，主控制台、托盘浮窗、IDE 界面与 HUD 在 2 秒内全部切为英文且重启后保持记忆。
  - `AC-001.3`：切换为 `跟随系统` 在 `zh-CN` 系统解析为 `zh-Hans`，在 `en-US` 系统解析为 `en`。

#### `FR-002` 零侵入 CDP 隔离世界热汉化
- **描述**：复用 `runtime/engine.js` 与 `src/controller.ts`，仅通过 `127.0.0.1` 回环 CDP 连接 Antigravity 页面，在独立隔离世界 `GRAVITYBOX-localization-v1` 中执行增量 DOM 翻译与保护守卫。
- **行为约束**：
  - 连接前必须校验 `DevToolsActivePort` 端口仅监听在 `127.0.0.1` / `[::1]`，且监听进程可执行路径匹配已验真的官方 `Antigravity.exe`。
  - 页面 URL 必须符合 `allowedTarget()` 白名单（拦截 `/api`、`/oauth`、`/login`、`/browser`、`/preview` 等敏感路径）。
  - 每次 DOM 翻译严格执行 `PROTECTED` / `USER_CONTENT` / `BRAND_NAMES` 保护守卫，绝不翻译对话内容、代码块、终端、文件名、项目名、第三方产品名。
  - 退出 GravityBox 或切换英文时，必须完整调用 `dispose()` 还原已翻译 DOM 节点并移除 `Page.addScriptToEvaluateOnNewDocument` 注册。
- **验收标准（AC）**：
  - `AC-002.1`：官方 `Antigravity.exe` 及 `resources\app.asar` 文件哈希在安装、运行、退出全周期内保持 100% 不变。
  - `AC-002.2`：Headless Chromium 回归测试套件（`test/browser.mjs`）在 Windows 上全部通过，受保护区域误译率为 0。

#### `FR-003` 无感启动汉化：Windows 快捷方式参数管理（可选、可逆）
- **描述**：为用户提供无感启动汉化能力——无需每次从 GravityBox 启动，日常点击桌面/开始菜单/任务栏的 Antigravity 图标即可自动携带 `--remote-debugging-port=0` 启动。
- **扫描范围（仅当前用户可写路径，不触碰系统公共目录以规避 UAC 提权）**：
  - 用户桌面：`%USERPROFILE%\Desktop\*.lnk`（以及 OneDrive 重定向后的实际 `FOLDERID_Desktop`）
  - 开始菜单程序：`%APPDATA%\Microsoft\Windows\Start Menu\Programs\**\*.lnk`
  - 任务栏固定栏：`%APPDATA%\Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar\*.lnk`
- **操作契约**：
  - **只读扫描（`shortcut_scan`）**：枚举指向官方 `Antigravity.exe` 的快捷方式，返回路径、显示位置标签、当前参数、是否已含 `--remote-debugging-port`、是否已有备份。
  - **显式授权应用（`shortcut_apply`）**：仅在用户明确点击开启后执行；先将每个 `.lnk` 的原始 `TargetPath`、`Arguments`、`WorkingDirectory` 与文件 SHA-256 原子写入 `%LOCALAPPDATA%\GravityBox\shortcut-backups.json`，再在保留原有参数的前提下安全追加 `--remote-debugging-port=0`（若已有 `--remote-debugging-port=...` 则幂等跳过，不重复追加）。
  - **一键无损还原（`shortcut_restore`）**：依据 `shortcut-backups.json` 精准移除追加的 `--remote-debugging-port=0` 或恢复原始 `Arguments`；软件卸载向导默认提供勾选「还原 Antigravity 快捷方式参数」。
- **验收标准（AC）**：
  - `AC-003.1`：对包含自定义参数（如 `--disable-gpu`）的 `.lnk` 执行应用后，原参数完整保留且仅追加一次 `--remote-debugging-port=0`；重复应用具有幂等性。
  - `AC-003.2`：执行一键还原后，`.lnk` 的 `Arguments` 精确恢复至备份值。
  - `AC-003.3`：对指向非官方路径或 Insiders 的 `.lnk` 严格跳过，绝不修改。

#### `FR-004` 启动 Antigravity 与安全重启接管
- **描述**：在主控制台侧栏与托盘浮窗底部提供「打开 Antigravity」主按钮；当 Antigravity 已在外部未带调试端口运行（`debug-port-missing`）时，提供「安全重启以连接」。
- **行为约束**：
  - 直接启动时通过 `CreateProcess` / `Process.Start` 附带 `--remote-debugging-port=0` 启动官方 `Antigravity.exe`。
  - 安全重启时先枚举属于官方 `Antigravity.exe` 的顶层窗口并发送 `WM_CLOSE`（优雅关闭，允许 IDE 保存会话或提示未保存文件），轮询等待主进程及渲染/GPU 子进程退出（默认忽略分离的 `language_server`），超时 10 秒绝不强杀，给出明确提示。
  - 进程确认退出后等待 300ms 文件锁释放，最多重试 3 次带 `--remote-debugging-port=0` 重新拉起，并校验 `DevToolsActivePort` 或命令行参数就绪。
- **验收标准（AC）**：
  - `AC-004.1`：Antigravity 未运行时点击「打开 Antigravity」，启动后的进程命令行包含 `--remote-debugging-port=0`，并在 5 秒内进入 `connected` 或 `adaptive` 状态。
  - `AC-004.2`：外部无调试端口启动时，5 秒内界面弹出「未开启调试端口」横幅；点击「安全重启以连接」后优雅关闭并自动拉起成功连接。

#### `FR-005` Windows 专属词条与新版（2.34+）自适应全量汉化增强
- **描述**：确保 Windows 版 Antigravity 界面（含 Windows 顶栏菜单、系统快捷键文案与 Windows 独立打包的 `main.js` 哈希）获得与 macOS 一致的高覆盖汉化体验。
- **行为约束**：
  - 词典分层加载支持平台覆盖层：在 `dict/base.json` 之上加载 `dict/win32.json`（仅在 `process.platform === 'win32'` 时生效），补充 Windows 特有文案（如 `File / Edit / Selection / View / Go / Run / Terminal / Help`、`Exit`、`Minimize`、`Maximize`、`Show in File Explorer`、`Ctrl+...` / `Alt+...` 提示文案及通用设置术语）。
  - 保留 `SUPPORTED_BUNDLES` 指纹白名单机制，扩展为支持同一版本号多平台哈希集合（`Map<string, string | string[]>`），收录 Windows 官方版本实测 `main.js` SHA-256。
  - 针对尚未收录指纹的新版本（如 `2.34.0+` 或首次发布的 Windows 版本），复用现有高置信度自适应升级机制：DOM 契约通过且 `computeBundleDictionaryCoverage() >= 85%`（且抽取串 ≥ 10）时，后台自动无缝由保守模式升级为全量汉化模式，同时生成 `reports/pending-<version>.json` 供后续词典补译。
- **验收标准（AC）**：
  - `AC-005.1`：在 Windows 上加载词典时，`dict/win32.json` 自动合并且层级顺序位于 `base.json` 之上、`个人覆盖.json` 之下；在 macOS 上不受影响。
  - `AC-005.2`：面对未收录哈希的 Windows 新版 Antigravity，DOM 契约校验通过且词典覆盖率 ≥ 85% 时，自动升级至全量汉化并在界面正常渲染中文。

#### `FR-006` 个人词典覆盖与增量补译热重载
- **描述**：在 `%LOCALAPPDATA%\GravityBox\词典\` 自动初始化 `个人覆盖.json` 与 `增量补全.json`，监听文件变更在 150ms 防抖后热推至所有已连接的 Antigravity 页面。
- **验收标准（AC）**：
  - `AC-006.1`：编辑并保存 `个人覆盖.json` 后，无需重启任何进程，Antigravity 页面对应文本在 1 秒内更新。
  - `AC-006.2`：写入损坏 JSON 时，保留上一份有效词典继续运行并在状态中报告 `dictionaryError`，修复后自动清除错误。

#### `FR-007` 旧版静态改包检测拦截
- **描述**：读取官方 `resources\app.asar` 内 `dist/preload.js`、`dist/main.js`、`dist/utils.js`，检测是否存在第三方/旧版静态改包特征；若检测到，进入 `legacy-blocked` 状态并提示用户恢复官方安装包，避免双重注入冲突。
- **验收标准（AC）**：
  - `AC-007.1`：构造含旧版注入标记的测试 `app.asar` 时，`inspectBundle()` 返回 `legacyPatch: true` 且控制器进入 `legacy-blocked`。

#### `FR-008` 元数据扫描与覆盖率报告
- **描述**：保留 `metadata_scan_start` 与覆盖率报告生成能力，读取 `%USERPROFILE%\.gemini\config\plugins` 清单与 `app.asar` 原生文案，产出脱敏报告到 `%LOCALAPPDATA%\GravityBox\reports\`。
- **验收标准（AC）**：
  - `AC-008.1`：扫描过程可取消，完成后产出合法 `coverage.json` 与 `deep-scan.json`，不泄露任何凭据或绝对用户路径。

---

### 4.2 对话状态栏（HUD）与遥测卡片

#### `FR-009` 输入框三联遥测胶囊与多栏隔离
- **描述**：在每个 Antigravity 对话输入框（`.surface-composer` / `[data-testid="agent-input-box"]`）正下方嵌入三枚独立胶囊（对话与速度 / Token 与缓存命中率 / 上下文水位与压缩徽章），支持分栏独立会话，不抢占输入焦点。
- **验收标准（AC）**：
  - `AC-009.1`：与 `runtime/hud-*.js`、`src/hud/metrics-engine.ts` 完全对齐，支持浅色/深色自动跟随、中文/英文实时随全局语言切换。
  - `AC-009.2`：分屏打开 2 个对话窗口时，各自展示对应会话的独立轮次、Token 与上下文水位。

#### `FR-010` 向上展开明细卡片（v1.1.3 规格）
- **描述**：悬停或点击胶囊向上展开 3 张明细卡片（对话统计、Token 用量与等效费用、当前上下文与曾用模型上下文角色徽章），支持 `Esc` 或点击空白处关闭。
- **验收标准（AC）**：
  - `AC-010.1`：三张卡片数据与 SQLite `steps` / `gen_metadata` / `executor_metadata` 一致，包含模型/工具耗时拆分、缓存节省金额、未公开定价 `≥ $X` 标记及角色徽章（`规划` / `执行`）。

#### `FR-011` 零阻塞只读 SQLite 增量扫描
- **描述**：通过 `node:worker_threads` + `node:sqlite` (`DatabaseSync` `readOnly: true` + `PRAGMA query_only=ON`) 扫描 `%USERPROFILE%\.gemini\antigravity\conversations\*.db`，禁止长事务，绝不阻塞 Antigravity 宿主写入或 WAL Checkpoint。
- **验收标准（AC）**：
  - `AC-011.1`：Windows NTFS 文件锁环境下并发读取正在写入的 `.db` + `-wal` 文件不引发宿主报错或 `SQLITE_BUSY` 锁死。

#### `FR-012` HUD 独立开关与纯 HUD 模式
- **描述**：在设置页提供「对话状态栏」开关（默认开启）；选择英文界面时，关闭汉化但 HUD 开关为开启时进入英文 HUD-only 模式。
- **验收标准（AC）**：
  - `AC-012.1`：开启/关闭 HUD 在 1 秒内生效并持久化至 `config.json.hudEnabled` 与 `runtime-preferences.json.hudEnabled`。

---

### 4.3 双周期配额监控与备用账号策略

#### `FR-013` 本机 Language Server 实时配额探针（Windows 适配）
- **描述**：无需额外云端 OAuth Client 配置，直接发现正在运行的官方 Antigravity `language_server` 进程、提取本机 CSRF Token（支持 `<= 2.33` 命令行 `--csrf_token` 与 `>= 2.34` 进程环境变量 `ANTIGRAVITY_SERVER_CSRF_TOKEN`）与回环监听端口，调用本机 RPC（`GetUserStatus` + `RetrieveUserQuotaSummary`）刷新当前活跃账号实时身份、订阅等级（FREE/PLUS/PRO/TEAM/ULTRA/ENTERPRISE）与配额。
- **验收标准（AC）**：
  - `AC-013.1`：在 Antigravity 2.33 及 2.34+ Windows 版本打开任意工作区后，总览页与账号页可实时识别当前登录邮箱、订阅等级与实时配额。
  - `AC-013.2`：仅信任经签名与路径校验的官方 `language_server` 回环端口，拒绝非回环或伪造进程端口。

#### `FR-014` 双周期配额与独立高阶池展示
- **描述**：拆分展示 **5 小时滚动限额**、**每周限额**、模型家族池（Gemini / Claude）及独立高阶池（Argon / Carbon）与重置倒计时，完全复用 `src/quota.ts` 与 `src/ipc-projections.ts` 契约。
- **验收标准（AC）**：
  - `AC-014.1`：所有配额合约单测（`quota-contracts`、`quota-grouping-tiers`、`quota-system`、`standby-cooling`）在 Windows 全量通过。

#### `FR-015` 2.34+ 备用账号超时静默降级与「预计已重置」
- **描述**：无本地 `.env` OAuth Client 凭据时，未激活备用账号在过期前 5–7 分钟自动执行 Last-Mile 静默补刷；过期后静默跳过不弹窗报错，保留历史快照并在超过重置时间后标注「预计已重置」供推荐引擎参考。
- **验收标准（AC）**：
  - `AC-015.1`：未配置 `.env` 时点击「刷新全部」，对过期备用账号返回 `standbyDeferred` 温和提示而非红字错误。
  - `AC-015.2`：用户在 `%LOCALAPPDATA%\GravityBox\.env` 配置 `GRAVITYBOX_GOOGLE_CLIENT_ID` / `SECRET` 后，自动启用后台云端刷新与本地 OAuth 登录流程。

#### `FR-016` 定时刷新与低配额智能推荐
- **描述**：支持手动 / 每 5 / 10 / 15 / 30 / 60 分钟自动刷新配额；支持配置智能推荐策略（受监控模型、阈值、策略优先级），默认只读推荐预览，执行切号必须显式 `confirmed: true`。
- **验收标准（AC）**：
  - `AC-016.1`：只读预览命令 `auto_switch_best` 在任何情况下不触发实际切号；仅在用户二次确认后调用 `auto_switch_execute`。

---

### 4.4 多账号安全管理与四阶段原子切换

#### `FR-017` 保存当前会话账号
- **描述**：一键采集当前 Antigravity 正在使用的账号身份与 OAuth 凭据（按优先级综合 Windows 凭据存储 / `%USERPROFILE%\.gemini` CLI 令牌文件 / `%APPDATA%\Antigravity\User\globalStorage\state.vscdb` 与本机 LS 实时探测）并存入账号库。
- **验收标准（AC）**：
  - `AC-017.1`：即使 `language_server` 尚未启动，也能从 Windows 凭据存储或 `state.vscdb` 中正确提取当前账号并去重更新。

#### `FR-018` JSON 批量导入与二次确认安全导出
- **描述**：支持通过文件/目录选择器批量导入 JSON 账号文件（按邮箱去重，支持单文件与目录遍历），以及经二次确认后导出全量账号备份（设置当前用户独占 NTFS ACL）。
- **验收标准（AC）**：
  - `AC-018.1`：未传 `confirmed: true` 时拒绝导出；导出目标拒绝符号链接/非当前用户目录。

#### `FR-019` 账号管理基础操作（增删改排 + OAuth / Refresh Token）
- **描述**：支持重命名备注、拖拽或按钮调整排序、删除账号（含 tombstone 防复活机制）、刷新单个/全部账号配额、通过 Refresh Token 或本地配置后的 Google OAuth PKCE 添加账号。
- **验收标准（AC）**：
  - `AC-019.1`：删除账号写入 `accounts-deleted.json` tombstone，异常重启后不会因残留文件复活已删账号。

#### `FR-020` 切号前安全预检（Preflight）
- **描述**：切号前检查官方 Antigravity 与 Insiders 进程是否运行、CDP 页面是否有正在进行的生成/智能体任务、以及最近 3 秒是否有 `.pb` 对话轨迹写入。
- **验收标准（AC）**：
  - `AC-020.1`：检测到生成中或活跃智能体任务时返回 `safe: false, state: "busy"` 及具体中文原因，前端阻断自动切号。
  - `AC-020.2`：检测到 Insiders 运行时，明确阻断切换并提示退出 Insiders。

#### `FR-021` 四阶段原子切号与 `agy` CLI 非侵入同步
- **描述**：在确认 Antigravity 完全退出后，加进程租约锁执行原子切号：
  1. 尝试回收切出账号最新本地令牌；
  2. 通过 Node 22 内置 `node:sqlite` 开启 `BEGIN IMMEDIATE` 事务更新 `%APPDATA%\Antigravity\User\globalStorage\state.vscdb`（更新 `oauthToken`、`userStatus`、`enterprisePreferences`、`antigravityOnboarding`，清理 `agentManagerInitState`）；
  3. 同步写入 Windows 凭据管理器（`go-keyring` 兼容格式）；
  4. 原子提交账号索引与凭据记录，落盘脱敏 `account-switch-journal-stable.json`；
  5. 非侵入附属同步本机已存在的 `%USERPROFILE%\.gemini\antigravity-cli\antigravity-oauth-token` 与 `jetski-standalone-oauth-token`（不存在绝不创建，不触碰 `@google/gemini-cli`）。
- **验收标准（AC）**：
  - `AC-021.1`：任一阶段发生异常（模拟 DB 写失败、凭据写失败、文件被外部篡改），自动回滚且原账号登录态完整可用。
  - `AC-021.2`：切换后自动带 `--remote-debugging-port=0` 重拉起 Antigravity，IDE 无需重新登录即进入目标账号。

#### `FR-022` 7 天内一键回滚与崩溃恢复点管理
- **描述**：切号后 7 天内支持一键「撤销上次切换」；若回滚过程中发生崩溃，下次启动时依据 `state.vscdb` 已提交状态自动执行 `reconcileRollback()` 对账恢复；支持用户显式确认清理历史恢复点。
- **验收标准（AC）**：
  - `AC-022.1`：通过全套 `rollback-recovery` 与 `account-switch-security` 故障注入测试。

---

### 4.5 只读 SQLite 用量总账与时序计费引擎

#### `FR-023` 多周期用量统计与思考档位折叠
- **描述**：扫描 `%USERPROFILE%\.gemini\antigravity\conversations\*.db` 并在 `%LOCALAPPDATA%\GravityBox\usage-ledger.sqlite` 维护去重历史总账，提供今日 / 近 7 天 / 近 30 天 / 全部汇总、24h/按日趋势、模型占比与请求数统计；汇总视图自动折叠 `-low`/`-medium`/`-high`/`-xhigh`/`-thinking`/`-max`/`-tiered` 思考档位变体。
- **验收标准（AC）**：
  - `AC-023.1`：所有 `usage-contracts`、`usage-ledger`、`phase4-usage-charts` 测试在 Windows 全量通过。

#### `FR-024` 请求明细流水与快照分页导出
- **描述**：支持打开「请求明细」独立窗口/弹窗，按时间周期、关键字与账号筛选分页浏览原始请求记录（保留原始未折叠模型标识），支持固定快照翻页（`pinSnapshot`）与导出 JSON/CSV 报告。
- **验收标准（AC）**：
  - `AC-024.1`：翻页过程中后台新事件写入不会导致当前列表重复行或跳页；关闭弹窗自动释放快照引用（`usage_snapshot_release`）。

#### `FR-025` 公开价格基准热更新
- **描述**：内置 `assets/pricing-feed.json` 与 `assets/model-catalog.json`，支持设置页「自动更新价格基准」（默认关闭，开启后每 24h 检查）与「立即检查」，弹窗展示生效模型数、新增/变更价目与未定价实时模型。
- **验收标准（AC）**：
  - `AC-025.1`：同步远端价目后即时刷新主界面、托盘浮窗与 IDE HUD 费用显示，且更新 `pricingRevision`。

#### `FR-026` 历史账单时序重估与回滚
- **描述**：价格基准更新后，支持先预览（`api_pricing_revalue_preview`）再经用户确认重估历史账单（仅重估未定价 / 全量重估），并保留回滚快照支持一键撤销（`api_pricing_revalue_rollback`）。
- **验收标准（AC）**：
  - `AC-026.1`：重估前后差额精确一致，回滚后账单金额与费率锁完整还原。

---

### 4.6 可撤回缓存维护与存储瘦身

#### `FR-027` 空间扫描与三段式存储分布
- **描述**：扫描 `%APPDATA%\Antigravity` 与 `%USERPROFILE%\.gemini\antigravity`，区分：
  - **运行缓存（安全无损，默认勾选）**：`Cache`、`Code Cache`、`GPUCache`、`Dawn*Cache`、`ShaderCache`、`CachedData`、`CachedExtensionVSIXs`、`CachedProfilesData`、`Crashpad`、`logs`、`blob_storage`、`PartitionsCache`、`AgentBrowserCache`、`OrphanAgentData`。
  - **超期后台日志（可选瘦身，默认不勾选，支持 7 天 / 30 天 / 全部非活跃）**：`AgentTaskStepLogs`、`AgentVerboseTranscripts`、`AgentSubagentDatabases`。
  - **受保护核心数据（只读统计，绝对禁清）**：`User/`（含 `state.vscdb`）、`Cookies`、`Local Storage`、`Session Storage`、`WebStorage`、`Network Persistent State`、`conversation_summaries.db`、所有非孤儿主对话 `.db`、运行中/已归档会话树、`%LOCALAPPDATA%\GravityBox`。
- **验收标准（AC）**：
  - `AC-027.1`：受保护白名单目录及符号链接/Junction 目标在扫描和清理阶段绝对不被移动。

#### `FR-028` 清理前预检与用量数据库强制归档
- **描述**：执行清理前检查无运行中后台任务、无活跃对话（180s 内写入检测）、Antigravity 及浏览器子进程未占用目标文件；若包含 `AgentSubagentDatabases`（`.db`），必须先调用 `archiveDatabasesBeforeClear()` 将 Token 账单事件固化至总账后再移动，并在归档后复核文件指纹防止并发变更。
- **验收标准（AC）**：
  - `AC-028.1`：清理超期 Subagent 数据库后，「用量」页历史 Token 与费用总计保持 100% 不减少。

#### `FR-029` 事务化可撤回移动与一键原位还原
- **描述**：将选中缓存项按写前日志（`恢复清单.json`）移入 `%LOCALAPPDATA%\GravityBox\maintenance-trash\GravityBox-缓存-<timestamp>`（与源盘同卷时优先原子 `rename`，跨卷时采用安全复制+校验+删除并记录事务阶段）；支持展示历史快照、一键原位还原、打开恢复目录以及“彻底清理旧快照至 Windows 回收站”。
- **验收标准（AC）**：
  - `AC-029.1`：一键还原可将目录与 `-wal`/`-shm` 伴随文件精确放回原路径；若原路径已被 IDE 重新生成，安全中止还原而不覆盖新文件。
  - `AC-029.2`：模拟中途崩溃重启后，`recoverPendingSnapshots()` 可自动对账修复或标示待恢复状态。

#### `FR-030` 文件锁占用友好提示（Windows 特有）
- **描述**：Windows 下被外部进程（如残留 `Antigravity.exe`、浏览器进程或防病毒扫描）锁定的缓存文件在移动时会触发 `EBUSY` / `EPERM`；系统必须在预检阶段尽量拦截，并在执行中遇到文件占用时自动回滚已移项、向用户明确指出需先关闭的应用，绝不留下半残状态。
- **验收标准（AC）**：
  - `AC-030.1`：对锁定文件执行清理失败后，事务完整回滚，所有已移项归位，前端返回清晰的中文错误指引。

---

### 4.7 Windows 原生桌面客户端（.NET 8 WPF）

#### `FR-031` 系统托盘驻留与无边框三标签快捷浮窗
- **描述**：应用常驻 Windows 系统托盘（通知区域），图标状态随连接状态切换；左键单击托盘图标在任务栏上方弹出无边框圆角快捷浮窗（宽度 `424 DIP`、自适应高度），内置 **用量 / 配额 / 账号** 三标签（支持 `Ctrl+1/2/3` 快捷键切换、`Esc` 或点击窗口外部自动收起）、顶部刷新按钮与 `···` 快捷菜单（显示语言切换、打开设置、退出）、底部「打开 Antigravity」与「打开主界面」；右键托盘图标展示标准原生右键菜单。
- **验收标准（AC）**：
  - `AC-031.1`：在任务栏位于底部/顶部/左侧/右侧、多显示器不同 DPI 缩放下，浮窗均精准贴靠托盘区域且完全位于工作区内不被裁切。
  - `AC-031.2`：关闭主窗口默认最小化隐藏至系统托盘，保持后台服务与汉化/HUD 持续运行。

#### `FR-032` 5 页原生主控制台与模态弹窗体系
- **描述**：采用左侧导航栏 + 右侧内容区布局，完整复刻 macOS v1.1.3 的 5 个主页面及 4 个子弹窗：
  1. **总览（Overview）**：未开启调试端口横幅（按需）、无感快捷方式引导卡（按需）、当前账号 Hero 卡片与双周期配额进度条、3 张今日指标磁贴（Token / 请求数 / 等效费用）、Bento 双栏看板（24h 消耗趋势图 + 今日活跃模型速览）。
  2. **账号（Accounts）**：顶部「管理账号…」菜单（保存当前、JSON 批量导入、备份导出、添加账号弹窗）、智能推荐策略条与配置弹窗、全部折叠/展开、待生效/回滚横幅、账号卡片列表（支持单卡刷新、切换、重命名、禁用/删除、折叠摘要）。
  3. **用量（Usage）**：4 周期切换分段控（今日/7天/30天/全部）、刷新与导出按钮、Token 趋势图卡、模型费用环图卡、缓存命中率轨与请求明细弹窗入口、折叠后模型消耗清单。
  4. **设置（Settings）**：
     - *Antigravity 增强*：显示语言（简体中文 / 跟随系统 / English）、无感启动汉化快捷方式开关与管理弹窗、对话状态栏 HUD 开关。
     - *配额与计价*：配额定时刷新间隔、自动更新价格基准开关与「立即检查」弹窗。
     - *系统与存储*：开机自启、跟随 Antigravity 启停、存储与维护入口（带可释放空间徽章）与存储维护弹窗。
  5. **关于（About）**：版本号与构建信息、检查更新/下载进度/一键安装、自动检查更新开关、运行环境信息、打开数据目录（在资源管理器中显示）、GitHub Releases 链接。
- **验收标准（AC）**：
  - `AC-032.1`：所有页面支持随 Windows 深色/浅色模式（`AppsUseLightTheme`）实时热切换，零硬编码白底/黑字割裂。
  - `AC-032.2`：所有文案统一从 `assets/i18n/*.json` 动态加载，切换语言后全界面实时刷新且无截断重叠。

#### `FR-033` 后端进程生命周期守护与防孤儿机制
- **描述**：WPF 前端启动内置 `runtime\node.exe` 运行 `core\dist\main.js`，将其绑定至 Windows **Job Object**（启用 `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`），确保前端异常崩溃时 Node 后端绝不残留成为孤儿进程；启动前优雅清理陈旧后端实例，后端异常退出时具备指数退避自愈重启能力；后端 stderr 写入 `%LOCALAPPDATA%\GravityBox\logs\backend.log` 并支持超过 1MB 自动轮转归档（最多保留 10 份）。
- **验收标准（AC）**：
  - `AC-033.1`：使用任务管理器强杀 `GravityBox.exe` 后，其拉起的 `node.exe` 在 1 秒内由内核 Job Object 自动终止。

#### `FR-034` 开机自启与跟随 Antigravity 启停
- **描述**：
  - **开机自启**：通过当前用户注册表 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 写入/移除 `GravityBox`（附带 `--minimized` 托盘静默启动参数），无需管理员权限。
  - **跟随 Antigravity 启停**：
    - 退出同步：开启后，GravityBox 监测到曾经运行中的 Antigravity 主进程全部退出（且非切号/安全重启保护窗口内），延迟 600ms 二次确认后自动优雅停止后端并退出自身。
    - 自动启动：通过轻量用户级定时任务/后台监视器（或配合快捷方式启动器）实现随 Antigravity 启动时拉起 GravityBox（若启用该选项且 GravityBox 未运行）。
- **验收标准（AC）**：
  - `AC-034.1`：开关开机自启无需 UAC 提权，立即反映在 Windows 任务管理器「启动应用」中。
  - `AC-034.2`：执行切号或安全重启时，保护期（`suppressLifecycleAutoQuit`）内绝不误触发自动退出。

#### `FR-035` 自更新检查与无损覆盖升级
- **描述**：对齐 `AppUpdateManager.swift`，默认关闭后台自动检查；支持手动/定时请求 GitHub Releases API，识别 Windows 发布包（`GravityBox-<version>-win-x64-setup.exe` 与 `.sha256` sidecar），流式下载并强制校验 SHA-256 哈希，用户点击「立即更新」后先优雅停止后端与释放文件锁，拉起静默/向导安装器完成覆盖无损升级并重启新版本（用户数据目录 `%LOCALAPPDATA%\GravityBox` 保持不动）。
- **验收标准（AC）**：
  - `AC-035.1`：SHA-256 校验不匹配时立即中止安装并删除损坏临时包。
  - `AC-035.2`：覆盖升级后，原有账号库、历史用量总账、个人词典、快捷方式备份与偏好配置 100% 保留。

#### `FR-036` 规范安装包与便携版分发
- **描述**：提供双分发形态：
  1. **标准安装版**（`GravityBox-<version>-win-x64-setup.exe`，Inno Setup Per-User 默认免 UAC 安装至 `%LOCALAPPDATA%\Programs\GravityBox`，卸载时提供「还原 Antigravity 快捷方式参数」选项）；
  2. **便携免安装版**（`GravityBox-<version>-win-x64-portable.zip`）；
  两者均生成标准 `.sha256` 校验文件。
- **验收标准（AC）**：
  - `AC-036.1`：在无 Node.js 与无 .NET 8 运行时的纯净 Windows 10/11 虚拟机中，双击安装即可直接启动并正常运行全部功能。

#### `FR-037` 本地日志与数据目录透明可访问
- **描述**：关于页提供「在资源管理器中显示」按钮，调用 `explorer.exe` 打开 `%LOCALAPPDATA%\GravityBox`（替代 macOS「在访达中显示」）。

---

## 5. 非功能性需求（Non-Functional Requirements）

| 编号 | 类别 | 指标 / 约束 | 验证方式 |
| :--- | :--- | :--- | :--- |
| `NFR-001` | **冷启动性能** | 托盘启动到后端 IPC 就绪 ≤ 1.5s；主控制台首次渲染 ≤ 400ms；托盘浮窗点击唤起 ≤ 120ms。 | 启动计时基准测试 |
| `NFR-002` | **资源占用** | 后台驻留（主窗口关闭、仅托盘 + Node 引擎连接 1 个 IDE 窗口）：WPF 壳私有内存 ≤ 85 MB，Node 后端 RSS ≤ 120 MB（`--max-old-space-size=256`），空闲 CPU ≤ 0.5%。 | 连续 30 分钟空闲监测 |
| `NFR-003` | **零外部运行时依赖** | 生产发布包内置 Node 22 与自包含 .NET 8 运行时；后端 `package.json` 保持零第三方生产 `dependencies`。 | 洁净 Windows 沙箱验证 + `package.json` 门禁 |
| `NFR-004` | **安全与访问控制** | Named Pipe 与数据目录 `%LOCALAPPDATA%\GravityBox` 必须设置仅限当前用户 SID（及 `SYSTEM`）访问的 Windows ACL；敏感凭据/错误日志经 `sanitizeError()` 脱敏（含 Windows 用户目录 `C:\Users\demo\...` 脱敏）；禁止在子进程命令行 `argv` 中传递 OAuth 密码或 SQL 令牌。 | 安全审计单测 + `check-privacy` 门禁 |
| `NFR-005` | **原子性与崩溃恢复** | 配置写入、账号索引更新、四阶段切号、缓存清理与快捷方式修改均具备写前日志（WAL/Journal）或原子临时文件替换（处理 Windows `EPERM`/`EBUSY` 短重试）。 | 故障注入单元测试 |
| `NFR-006` | **跨平台零回归** | `src/` 与 `runtime/` 修改后，macOS CI 全部既有门禁（`unit-and-dict`、`browser-e2e`、`native-swift`）必须保持 100% 通过。 | GitHub Actions 双平台矩阵 CI |
| `NFR-007` | **可访问性与本地化** | WPF 控件完整配置 `AutomationProperties.Name` 与键盘焦点导航；中英文切换无文字截断、数字统一等宽字体对齐（`TabularFigures`）。 | xUnit UI 契约测试 + 人工走查 |
| `NFR-008` | **路径与编码规范** | 全链路支持含中文字符、空格与长路径的 Windows 用户目录（`%USERPROFILE%` 含 CJK 字符与空格场景），所有 JSON/IPC/词典文件强制 UTF-8（无 BOM）。 | 含中文/空格路径隔离单测 |

---

## 6. 非目标（Out of Scope）与 Day-0 核验前置假设

### 6.1 明确非目标（Out of Scope）
- 不支持 Windows 7 / 8 / 8.1 及 32 位（`x86`）系统。
- 不支持或托管 `Antigravity - Insiders` 预览版（仅在切号与缓存写前检测其进程以防数据冲突）。
- 不提供任何修改/重打包 `app.asar` 的离线侵入式汉化补丁。
- 不内置官方 Google OAuth Client Secret，不架设任何中转代理或云同步服务器。
- 不替换或废弃现有 macOS `native/` Swift 代码与 macOS 发版流程。

### 6.2 Day-0 实机核验清单（进入编码前必须冻结的 5 项宿主事实）
由于上游仓库当前仅记录了 macOS 版 Antigravity 特征，在 SDD 执行阶段 **M0（Day-0）** 必须在安装了官方 Windows 版 Antigravity 的实机上核验并冻结以下 5 项事实，并回填至 `02-design.md` 常量表：

| 编号 | 核验项 | 待确认的具体值 | 影响的模块 |
| :--- | :--- | :--- | :--- |
| `CHK-01` | 官方安装目录与目录树 | 默认安装路径（如 `%LOCALAPPDATA%\Programs\Antigravity\`）、主程序名（`Antigravity.exe`）、`resources\app.asar` 相对路径、`language_server` 准确文件名（如 `language_server_windows_x64.exe` 或 `language_server.exe`） | `src/platform/win32.ts`、`application-target.ts`、`bundle-cache.ts` |
| `CHK-02` | Authenticode 代码签名主体 | `Get-AuthenticodeSignature` 返回的官方证书 `SignerCertificate.Subject`（预期 `O=Google LLC`）与有效签发链 | `src/discovery.ts` (`inspectBundle`) |
| `CHK-03` | 用户配置目录与 `state.vscdb` 路径 | `%APPDATA%\Antigravity\DevToolsActivePort` 与 `%APPDATA%\Antigravity\User\globalStorage\state.vscdb` 是否与 Electron 标准一致 | `application-target.ts`、`account-switch.ts`、`maintenance.ts` |
| `CHK-04` | Windows `go-keyring` 凭据条目名 | Go 端在 Windows Credential Manager（`cmdkey /list` 或 `CredEnumerateW`）中写入的精确 `TargetName`（如 `gemini:antigravity` 或 `antigravity` / `LegacyGeneric:target=...`）及编码格式是否仍为 `go-keyring-base64:<b64>` | `src/account-keychain.ts`、`Win32CredentialAdapter` |
| `CHK-05` | 2.34+ `language_server` CSRF 与 `main.js` 哈希 | Windows 端 `language_server` 是否同样通过命令行 `--csrf_token` 或环境变量 `ANTIGRAVITY_SERVER_CSRF_TOKEN` 传参；当前 Windows 正式版 `main.js` 的 SHA-256 指纹 | `src/account-probe.ts`、`src/bundle.ts` |
