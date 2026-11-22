#!/usr/bin/env node
import { createServer } from 'node:http';
import { spawn } from 'node:child_process';
import { existsSync } from 'node:fs';
import { mkdtemp, mkdir, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { CDP } from '../../dist/cdp.js';

const __dirname = dirname(fileURLToPath(import.meta.url));
const root = join(__dirname, '..', '..');
const defaultOutDir = join(root, 'assets', 'screenshots', 'win');

function resolveWindowsBrowser() {
  const candidates = [
    process.env.CHROME_BIN,
    process.env.CHROME_PATH,
    'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe',
    'C:\\Program Files (x86)\\Google\\Chrome\\Application\\chrome.exe',
    process.env.LOCALAPPDATA ? join(process.env.LOCALAPPDATA, 'Google', 'Chrome', 'Application', 'chrome.exe') : '',
    'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe',
    'C:\\Program Files\\Microsoft\\Edge\\Application\\msedge.exe',
  ].filter(Boolean);
  for (const candidate of candidates) {
    if (existsSync(candidate)) return candidate;
  }
  throw new Error('未找到 Chrome 或 Edge 可执行文件，请设置 CHROME_BIN');
}

function themePalette(dark) {
  return dark
    ? {
        bgGradient: 'linear-gradient(155deg, #0d1118 0%, #111827 55%, #0b0f17 100%)',
        winBg: '#161921',
        sidebarBg: '#12151c',
        cardBg: '#1c202a',
        subCardBg: '#222734',
        border: 'rgba(255, 255, 255, 0.10)',
        borderSoft: 'rgba(255, 255, 255, 0.06)',
        shadow: '0 24px 60px rgba(0, 0, 0, 0.65), 0 2px 10px rgba(0, 0, 0, 0.40)',
        textPrimary: '#f1f5f9',
        textSecondary: '#94a3b8',
        textMuted: '#64748b',
        accent: '#3b82f6',
        accentSoft: 'rgba(59, 130, 246, 0.16)',
        accentText: '#60a5fa',
        greenSoft: 'rgba(16, 185, 129, 0.16)',
        greenText: '#34d399',
        codeBg: '#0f131b',
        codeFg: '#93c5fd',
        bubbleBg: '#242a38',
        composerBg: '#1b1f29',
        pillBg: 'rgba(255, 255, 255, 0.06)',
      }
    : {
        bgGradient: 'linear-gradient(155deg, #edf3fb 0%, #f4f7fb 55%, #e6edf8 100%)',
        winBg: '#ffffff',
        sidebarBg: '#f6f8fb',
        cardBg: '#ffffff',
        subCardBg: '#f3f5f9',
        border: 'rgba(15, 23, 42, 0.10)',
        borderSoft: 'rgba(15, 23, 42, 0.06)',
        shadow: '0 22px 54px rgba(15, 23, 42, 0.12), 0 2px 10px rgba(15, 23, 42, 0.06)',
        textPrimary: '#0f172a',
        textSecondary: '#475569',
        textMuted: '#64748b',
        accent: '#2563eb',
        accentSoft: 'rgba(37, 99, 235, 0.10)',
        accentText: '#2563eb',
        greenSoft: 'rgba(16, 185, 129, 0.12)',
        greenText: '#059669',
        codeBg: '#f1f5f9',
        codeFg: '#1d4ed8',
        bubbleBg: '#eef2f7',
        composerBg: '#ffffff',
        pillBg: '#f1f5f9',
      };
}

function buildWorkbenchCss(p) {
  return `
    * { box-sizing: border-box; }
    .win-shell {
      width: 100%;
      height: 100%;
      background: ${p.winBg};
      border: 1px solid ${p.border};
      border-radius: 12px;
      box-shadow: ${p.shadow};
      display: flex;
      flex-direction: column;
      overflow: hidden;
      font-family: "Segoe UI Variable", "Microsoft YaHei UI", "Segoe UI", sans-serif;
      color: ${p.textPrimary};
    }
    .win-titlebar {
      height: 36px;
      padding: 0 14px;
      display: flex;
      align-items: center;
      justify-content: space-between;
      border-bottom: 1px solid ${p.borderSoft};
      background: ${p.sidebarBg};
      flex-shrink: 0;
    }
    .win-title-left {
      display: flex;
      align-items: center;
      gap: 8px;
      font-size: 12px;
      font-weight: 600;
      color: ${p.textPrimary};
    }
    .brand-dot {
      width: 9px;
      height: 9px;
      border-radius: 50%;
      background: ${p.accent};
    }
    .win-title-pills {
      display: flex;
      align-items: center;
      gap: 8px;
    }
    .tag-pill {
      font-size: 11px;
      padding: 2px 9px;
      border-radius: 999px;
      border: 1px solid ${p.border};
      background: ${p.pillBg};
      color: ${p.textSecondary};
      font-weight: 500;
    }
    .tag-pill.live {
      background: ${p.greenSoft};
      color: ${p.greenText};
      border-color: transparent;
    }
    .win-controls {
      display: flex;
      align-items: center;
      gap: 14px;
      font-size: 11px;
      color: ${p.textSecondary};
      margin-left: 10px;
    }
    .ide-body {
      flex: 1;
      display: flex;
      min-height: 0;
    }
    .ide-sidebar {
      width: 196px;
      background: ${p.sidebarBg};
      border-right: 1px solid ${p.borderSoft};
      padding: 12px 10px;
      display: flex;
      flex-direction: column;
      gap: 4px;
      flex-shrink: 0;
    }
    .side-btn-primary {
      background: ${p.accent};
      color: #ffffff;
      border: none;
      border-radius: 8px;
      padding: 7px 10px;
      font-size: 12.5px;
      font-weight: 600;
      text-align: center;
      margin-bottom: 8px;
    }
    .side-section {
      font-size: 10.5px;
      font-weight: 600;
      color: ${p.textMuted};
      padding: 8px 8px 4px;
      letter-spacing: 0.02em;
    }
    .side-item {
      padding: 6px 9px;
      border-radius: 7px;
      font-size: 12.5px;
      color: ${p.textSecondary};
      display: flex;
      align-items: center;
      justify-content: space-between;
      background: transparent;
      border: none;
      width: 100%;
      text-align: left;
      font-family: inherit;
    }
    .side-item.active {
      background: ${p.accentSoft};
      color: ${p.accentText};
      font-weight: 600;
    }
    .ide-main {
      flex: 1;
      display: flex;
      flex-direction: column;
      min-width: 0;
      background: ${p.winBg};
    }
    .ide-topbar {
      height: 40px;
      padding: 0 16px;
      border-bottom: 1px solid ${p.borderSoft};
      display: flex;
      align-items: center;
      justify-content: space-between;
      font-size: 12.5px;
      flex-shrink: 0;
    }
    .tab-row {
      display: flex;
      align-items: center;
      gap: 14px;
    }
    .tab-item {
      color: ${p.textSecondary};
      font-weight: 500;
      background: transparent;
      border: none;
      padding: 0;
      font-size: inherit;
      font-family: inherit;
    }
    .tab-item.active {
      color: ${p.textPrimary};
      font-weight: 600;
    }
    .action-row {
      display: flex;
      align-items: center;
      gap: 8px;
    }
    .mini-btn {
      font-size: 11.5px;
      padding: 4px 10px;
      border-radius: 7px;
      border: 1px solid ${p.border};
      background: ${p.subCardBg};
      color: ${p.textPrimary};
      font-weight: 500;
    }
    .mini-btn.accent {
      background: ${p.accentSoft};
      color: ${p.accentText};
      border-color: transparent;
    }
    .conv-area {
      flex: 1;
      display: flex;
      flex-direction: column;
      justify-content: space-between;
      padding: 16px 22px 14px;
      min-height: 0;
    }
    .messages {
      display: flex;
      flex-direction: column;
      gap: 12px;
    }
    .policy-strip {
      display: grid;
      grid-template-columns: repeat(3, 1fr);
      gap: 8px;
    }
    .policy-card {
      background: ${p.subCardBg};
      border: 1px solid ${p.borderSoft};
      border-radius: 9px;
      padding: 8px 10px;
    }
    .policy-label {
      display: block;
      font-size: 11px;
      color: ${p.textSecondary};
      margin-bottom: 3px;
    }
    .policy-val {
      font-size: 12px;
      font-weight: 600;
      color: ${p.textPrimary};
      margin: 0;
    }
    .composer-placeholder-input {
      width: 100%;
      border: none;
      outline: none;
      background: transparent;
      font-family: inherit;
      font-size: 12.5px;
      color: ${p.textPrimary};
      padding: 0;
    }
    .composer-placeholder-input::placeholder {
      color: ${p.textMuted};
      opacity: 1;
    }
    .user-row {
      display: flex;
      justify-content: flex-end;
    }
    .user-bubble {
      background: ${p.bubbleBg};
      color: ${p.textPrimary};
      padding: 8px 13px;
      border-radius: 12px;
      font-size: 12.5px;
      line-height: 1.5;
      max-width: 78%;
    }
    .assistant-box {
      display: flex;
      flex-direction: column;
      gap: 6px;
    }
    .assistant-head {
      display: flex;
      align-items: center;
      gap: 8px;
      font-size: 11.5px;
      color: ${p.textSecondary};
    }
    .assistant-text {
      font-size: 12.5px;
      line-height: 1.6;
      color: ${p.textPrimary};
    }
    .code-chip {
      font-family: "Cascadia Code", Consolas, monospace;
      font-size: 11.5px;
      background: ${p.codeBg};
      color: ${p.codeFg};
      padding: 2px 6px;
      border-radius: 5px;
      border: 1px solid ${p.borderSoft};
    }
    .composer-column {
      display: flex;
      flex-direction: column;
      width: 100%;
    }
    .surface-composer {
      background: ${p.composerBg};
      border: 1px solid ${p.border};
      border-radius: 12px;
      padding: 10px 12px 8px;
      display: flex;
      flex-direction: column;
      gap: 8px;
    }
    .composer-input {
      font-size: 12.5px;
      color: ${p.textMuted};
      min-height: 20px;
    }
    .composer-footer {
      display: flex;
      align-items: center;
      justify-content: space-between;
    }
    .composer-left {
      display: flex;
      align-items: center;
      gap: 8px;
    }
    .model-pill {
      font-size: 11.5px;
      padding: 4px 10px;
      border-radius: 999px;
      border: 1px solid ${p.border};
      background: ${p.pillBg};
      color: ${p.textPrimary};
      font-weight: 500;
    }
    .send-btn {
      background: ${p.accent};
      color: #ffffff;
      border: none;
      border-radius: 999px;
      padding: 4px 12px;
      font-size: 11.5px;
      font-weight: 600;
    }
  `;
}

function buildHudShowcaseHtml({ dark }) {
  const p = themePalette(dark);
  return `<!doctype html>
<html lang="zh-CN">
<head>
<meta charset="utf-8" />
<style>
  html, body {
    margin: 0;
    width: 100%;
    height: 100%;
    padding: 20px 24px;
    background: ${p.bgGradient};
    display: flex;
    align-items: stretch;
    justify-content: center;
  }
  ${buildWorkbenchCss(p)}
</style>
</head>
<body>
  <div class="win-shell">
    <div class="win-titlebar">
      <div class="win-title-left">
        <span class="brand-dot"></span>
        <span>Antigravity</span>
        <span style="color:${p.textMuted};font-weight:400;">—</span>
        <span role="heading" aria-level="1">Agent Manager</span>
      </div>
      <div class="win-title-pills">
        <span class="tag-pill live" data-gravitybox-ignore="1">GravityBox · 默认中文 + HUD</span>
        <div class="win-controls" data-gravitybox-ignore="1"><span>─</span><span>□</span><span>✕</span></div>
      </div>
    </div>
    <div class="ide-body">
      <nav class="ide-sidebar">
        <button type="button" class="side-btn-primary">New Conversation</button>
        <button type="button" role="menuitem" class="side-item active">Conversations</button>
        <button type="button" role="menuitem" class="side-item">Workspaces</button>
        <button type="button" role="menuitem" class="side-item">Knowledge</button>
        <button type="button" role="menuitem" class="side-item">Browser</button>
        <button type="button" role="menuitem" class="side-item">Automations</button>
        <label class="side-section">Customization</label>
        <button type="button" role="menuitem" class="side-item">MCP Servers</button>
        <button type="button" role="menuitem" class="side-item">Account</button>
        <button type="button" role="menuitem" class="side-item">Usage</button>
        <button type="button" role="menuitem" class="side-item">Settings</button>
      </nav>
      <main class="ide-main" data-testid="conversation-view" data-conversation-id="win-showcase-hud-v113">
        <header class="ide-topbar">
          <div class="tab-row" role="tablist">
            <button type="button" role="tab" class="tab-item active">Conversations</button>
            <button type="button" role="tab" class="tab-item">Changes</button>
            <button type="button" role="tab" class="tab-item">Artifacts</button>
            <button type="button" role="tab" class="tab-item">Files</button>
            <button type="button" role="tab" class="tab-item">Terminal</button>
          </div>
          <div class="action-row">
            <button type="button" class="mini-btn">Open Editor</button>
            <button type="button" class="mini-btn accent">Review Changes</button>
          </div>
        </header>
        <div class="conv-area">
          <div class="messages">
            <div class="policy-strip">
              <div class="policy-card">
                <label class="policy-label">Artifact Review Policy</label>
                <div class="policy-val" role="option">Agent Decides</div>
              </div>
              <div class="policy-card">
                <label class="policy-label">Terminal Command Auto Execution</label>
                <div class="policy-val" role="option">Request Review</div>
              </div>
              <div class="policy-card">
                <label class="policy-label">Interface Layout</label>
                <div class="policy-val" role="option">Software Engineering</div>
              </div>
            </div>
            <div class="user-row">
              <div class="user-bubble" data-gravitybox-ignore="1">帮我验证 Windows Named Pipe 与 DPAPI 凭据隔离测试并输出覆盖报告</div>
            </div>
            <div class="assistant-box">
              <div class="assistant-head">
                <button type="button" class="tag-pill">Planning</button>
                <button type="button" class="tag-pill">Review Policy</button>
                <span style="margin-left:auto;"></span>
                <button type="button" class="mini-btn">Reject All</button>
                <button type="button" class="mini-btn accent">Accept All</button>
              </div>
              <div class="assistant-text" data-gravitybox-ignore="1">
                已完成 <code class="code-chip">src/platform/win32.ts</code> 与 <code class="code-chip">GravityBox.Win32</code> 的 SID 权限校验，34 项 .NET 测试与全部 Windows 平台契约测试均已通过。
              </div>
            </div>
          </div>

          <div class="composer-column" data-testid="agent-input-box">
            <div class="surface-composer">
              <div style="position:relative;min-height:22px;">
                <input type="text" readonly class="composer-placeholder-input" placeholder="Ask anything, @ to mention, / for actions" />
                <div contenteditable="true" aria-label="Message input" style="position:absolute;inset:0;opacity:0;"></div>
              </div>
              <div class="composer-footer">
                <div class="composer-left">
                  <button type="button" class="model-pill" data-testid="model-selector-trigger">Gemini 4 Hydrogen</button>
                  <button type="button" class="tag-pill">Planning</button>
                </div>
                <button type="button" class="send-btn" aria-label="Send">Send</button>
              </div>
            </div>
          </div>
        </div>
      </main>
    </div>
  </div>
</body>
</html>`;
}

function buildCompareInnerCardHtml({ dark, badgeLabel, isAfter }) {
  const p = themePalette(dark);
  return `
  <div class="win-shell">
    <div class="win-titlebar">
      <div class="win-title-left">
        <span class="brand-dot" style="background:${isAfter ? p.accent : p.textMuted};"></span>
        <span>Antigravity</span>
        <span style="color:${p.textMuted};font-weight:400;">·</span>
        <span role="heading" aria-level="1">Agent Manager</span>
      </div>
      <div class="win-title-pills">
        <span class="tag-pill ${isAfter ? 'live' : ''}" data-gravitybox-ignore="1">${badgeLabel}</span>
        <div class="win-controls" data-gravitybox-ignore="1"><span>─</span><span>□</span><span>✕</span></div>
      </div>
    </div>
    <div class="ide-body">
      <nav class="ide-sidebar" style="width:136px;padding:10px 8px;">
        <button type="button" class="side-btn-primary" style="font-size:12px;padding:6px 8px;">New Conversation</button>
        <button type="button" role="menuitem" class="side-item active">Conversations</button>
        <button type="button" role="menuitem" class="side-item">Workspaces</button>
        <button type="button" role="menuitem" class="side-item">Knowledge</button>
        <button type="button" role="menuitem" class="side-item">Browser</button>
        <button type="button" role="menuitem" class="side-item">Automations</button>
        <button type="button" role="menuitem" class="side-item">MCP Servers</button>
        <button type="button" role="menuitem" class="side-item">Usage</button>
        <button type="button" role="menuitem" class="side-item">Settings</button>
      </nav>
      <main class="ide-main" ${isAfter ? 'data-testid="conversation-view" data-conversation-id="win-showcase-hud-v113"' : ''}>
        <header class="ide-topbar" style="padding:0 12px;">
          <div class="tab-row" role="tablist" style="gap:10px;font-size:12px;">
            <button type="button" role="tab" class="tab-item active">Settings</button>
            <button type="button" role="tab" class="tab-item">Changes</button>
            <button type="button" role="tab" class="tab-item">Artifacts</button>
            <button type="button" role="tab" class="tab-item">Terminal</button>
          </div>
          <div class="action-row">
            <button type="button" class="mini-btn accent">Review Changes</button>
          </div>
        </header>
        <div class="conv-area" style="padding:12px 14px 10px;">
          <div class="messages" style="gap:10px;">
            <div class="policy-card">
              <label class="policy-label">Artifact Review Policy</label>
              <div class="policy-val" style="display:flex;justify-content:space-between;align-items:center;">
                <span role="option">Agent Decides</span>
                <button type="button" class="tag-pill">Review Policy</button>
              </div>
            </div>
            <div class="policy-card">
              <label class="policy-label">Terminal Command Auto Execution</label>
              <div class="policy-val" style="display:flex;justify-content:space-between;align-items:center;">
                <span role="option">Request Review</span>
                <button type="button" class="tag-pill">Always Proceed</button>
              </div>
            </div>
            <div class="policy-card">
              <label class="policy-label">Browser Javascript Execution Policy</label>
              <div class="policy-val" style="display:flex;justify-content:space-between;align-items:center;">
                <span role="option">Block all browser JavaScript execution.</span>
              </div>
            </div>
            <div class="policy-card" style="background:${p.codeBg};">
              <div class="policy-label" data-gravitybox-ignore="1">Protected Code (代码原文受白名单隔离，绝不被汉化篡改)</div>
              <pre style="margin:4px 0 0;font-family:'Cascadia Code',Consolas,monospace;font-size:11px;color:${p.codeFg};line-height:1.45;">const pipe = "\\\\.\\pipe\\gravitybox-ipc"; // untouched</pre>
            </div>
          </div>

          <div class="composer-column" ${isAfter ? 'data-testid="agent-input-box"' : ''}>
            <div class="surface-composer" style="padding:8px 10px 7px;">
              <div style="position:relative;min-height:19px;">
                <input type="text" readonly class="composer-placeholder-input" style="font-size:12px;" placeholder="Ask anything, @ to mention, / for actions" />
                ${isAfter ? '<div contenteditable="true" aria-label="Message input" style="position:absolute;inset:0;opacity:0;"></div>' : ''}
              </div>
              <div class="composer-footer">
                <div class="composer-left">
                  <button type="button" class="model-pill" ${isAfter ? 'data-testid="model-selector-trigger"' : ''}>Gemini 4 Hydrogen</button>
                  <button type="button" class="mini-btn">Accept All</button>
                </div>
                <button type="button" class="send-btn" aria-label="Send">Send</button>
              </div>
            </div>
          </div>
        </div>
      </main>
    </div>
  </div>`;
}

function buildComparePageHtml({ dark }) {
  const p = themePalette(dark);
  const beforeIframeDoc = `<!doctype html><html><head><meta charset="utf-8" /><style>html,body{margin:0;width:100%;height:100%;background:transparent;overflow:hidden;}${buildWorkbenchCss(p)}</style></head><body>${buildCompareInnerCardHtml({ dark, badgeLabel: '原版英文界面 (Before)', isAfter: false })}</body></html>`;
  const escapedSrcdoc = beforeIframeDoc
    .replaceAll('&', '&amp;')
    .replaceAll('"', '&quot;');

  return `<!doctype html>
<html lang="zh-CN">
<head>
<meta charset="utf-8" />
<style>
  html, body {
    margin: 0;
    width: 100%;
    height: 100%;
    padding: 18px 20px;
    background: ${p.bgGradient};
    display: flex;
    flex-direction: column;
    gap: 12px;
    font-family: "Segoe UI Variable", "Microsoft YaHei UI", "Segoe UI", sans-serif;
    color: ${p.textPrimary};
  }
  .compare-grid {
    flex: 1;
    display: grid;
    grid-template-columns: 1fr 28px 1fr;
    align-items: stretch;
    min-height: 0;
  }
  .arrow-col {
    display: flex;
    align-items: center;
    justify-content: center;
    font-size: 20px;
    font-weight: 700;
    color: ${p.accent};
  }
  iframe.before-frame {
    width: 100%;
    height: 100%;
    border: none;
    background: transparent;
  }
  ${buildWorkbenchCss(p)}
</style>
</head>
<body>
  <div class="compare-grid">
    <iframe class="before-frame" srcdoc="${escapedSrcdoc}"></iframe>
    <div class="arrow-col" data-gravitybox-ignore="1">→</div>
    <div id="after-host" style="height:100%;">
      ${buildCompareInnerCardHtml({ dark, badgeLabel: 'GravityBox 零改包默认中文 + HUD (After)', isAfter: true })}
    </div>
  </div>
</body>
</html>`;
}

function buildLiveHudPayload() {
  return {
    conversationId: 'win-showcase-hud-v113',
    sessionRevision: 1,
    metricsSequence: 9999999,
    updatedAt: Date.now(),
    availability: 'ready',
    isStreaming: false,
    activityPhase: 'idle',
    isSubagentRunning: false,
    runningSubagentCount: 0,
    session: {
      turns: 6,
      steps: 338,
      compactionCount: 1,
      runningSubagentCount: 0,
      subagentCount: 2,
      subagentSteps: 42,
      modelDurationSeconds: 1941,
      modelDurationFormatted: '32分21秒',
      modelDurationFormattedEn: '32m21s',
      toolDurationSeconds: 1333,
      toolDurationFormatted: '22分13秒',
      toolDurationFormattedEn: '22m13s',
      averageTtftSeconds: 10.8,
      averageTtftFormatted: '10.8 秒',
      averageTtftFormattedEn: '10.8s',
      outputTps: 69.6,
      outputTpsFormatted: '69.6 tok/s',
      latestStepTtftSeconds: 16.6,
      latestStepTtftFormatted: '16.6 秒',
      latestStepTtftFormattedEn: '16.6s',
      latestStepOutputTps: 21.1,
      latestStepOutputTpsFormatted: '21.1 tok/s',
      currentTurnDurationSeconds: 1251,
      currentTurnDurationCompactZh: '本轮 20m',
      currentTurnDurationCompactEn: 'Turn 20m',
      currentTurnDurationFormatted: '20分51秒',
      currentTurnDurationFormattedEn: '20m51s',
    },
    tokens: {
      totalTokens: 17840000,
      totalTokensCompact: '17.8M tok',
      totalTokensFormatted: '17.8M tok',
      cacheHitRate: 0.964,
      cacheHitRateFormatted: '96%',
      uncachedInputTokens: 642000,
      uncachedInputFormatted: '642K tok',
      cachedReadTokens: 17110000,
      cachedReadFormatted: '17.1M tok',
      cacheWriteTokens: 48000,
      cacheWriteFormatted: '48K tok',
      outputTokens: 40000,
      outputFormatted: '40K tok',
      reasoningTokens: 16200,
      candidateTokens: 23800,
      estimatedCostUsd: 0.42,
      estimatedCostFormatted: '~$0.42',
      cacheSavedUsd: 2.15,
      cacheSavedFormatted: '~$2.15',
    },
    context: {
      hasUsageData: true,
      modelId: 'gemini-4-hydrogen',
      modelDisplayName: 'Gemini 4 Hydrogen',
      modelShortLabel: 'Hydrogen',
      modelCompactLabel: 'H',
      isMultiModel: false,
      isOrchestrator: false,
      usedTokens: 298000,
      usedTokensCompact: '298K',
      peakContextTokens: 342000,
      capacityTokens: 512000,
      capacityTokensCompact: '512K',
      isFallbackCapacity: false,
      usedRatio: 0.582,
      usedRatioFormatted: '58%',
      breakdown: {
        cachedContextTokens: 295000,
        cachedContextRatio: 57.6,
        uncachedContextTokens: 3000,
        uncachedContextRatio: 0.59,
        latestOutputTokens: 512,
      },
      modelStages: [],
    },
  };
}

async function until(fn, timeout = 10000, label = 'condition') {
  const deadline = Date.now() + timeout;
  let lastError;
  while (Date.now() < deadline) {
    try {
      const value = await fn();
      if (value) return value;
    } catch (error) {
      lastError = error;
    }
    await new Promise((resolve) => setTimeout(resolve, 40));
  }
  throw new Error(`Timed out waiting for ${label}${lastError ? `: ${lastError.message}` : ''}`);
}

export async function captureWinIdeScreenshots({ outDir = defaultOutDir } = {}) {
  await mkdir(outDir, { recursive: true });
  const engineCode = await readFile(join(root, 'dist', 'engine.js'), 'utf8');
  const dictEntries = JSON.parse(await readFile(join(root, 'dict', 'base.json'), 'utf8'));
  const chromeBin = resolveWindowsBrowser();
  const profileDir = await mkdtemp(join(tmpdir(), 'gb-win-ide-shot-'));

  let currentHtml = '<html><body>loading</body></html>';
  const server = createServer((_req, res) => {
    res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8' });
    res.end(currentHtml);
  });
  await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
  const origin = `http://127.0.0.1:${server.address().port}`;

  let chrome;
  let cdp;
  try {
    chrome = spawn(
      chromeBin,
      [
        '--headless=new',
        '--disable-gpu',
        `--user-data-dir=${profileDir}`,
        '--remote-debugging-port=0',
        '--no-first-run',
        '--no-default-browser-check',
        '--disable-background-networking',
        '--disable-extensions',
        `${origin}/`,
      ],
      { stdio: ['ignore', 'ignore', 'pipe'] }
    );

    const port = await until(async () => {
      const text = await readFile(join(profileDir, 'DevToolsActivePort'), 'utf8');
      return Number(text.split('\n')[0]);
    }, 15000, 'DevToolsActivePort');

    const targets = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
    const pageTarget = targets.find((item) => item.type === 'page') ?? targets[0];
    cdp = new CDP(pageTarget.webSocketDebuggerUrl);
    await cdp.connect();
    await cdp.send('Page.enable');
    await cdp.send('Runtime.enable');

    const specs = [
      { key: 'win-i18n-hud', width: 1140, height: 660, builder: buildHudShowcaseHtml, openSessionCard: true },
      { key: 'win-i18n-compare', width: 1380, height: 610, builder: buildComparePageHtml, openSessionCard: false },
    ];

    for (const dark of [false, true]) {
      for (const spec of specs) {
        const fileName = `${spec.key}${dark ? '-dark' : ''}.png`;
        currentHtml = spec.builder({ dark });

        await cdp.send('Emulation.setDeviceMetricsOverride', {
          width: spec.width,
          height: spec.height,
          deviceScaleFactor: 2,
          mobile: false,
        });
        await cdp.send('Emulation.setEmulatedMedia', {
          features: [
            { name: 'prefers-color-scheme', value: dark ? 'dark' : 'light' },
            { name: 'prefers-reduced-motion', value: 'reduce' },
          ],
        });

        await cdp.send('Page.navigate', { url: `${origin}/?shot=${encodeURIComponent(fileName)}` });
        await until(
          () => cdp.evaluate(`document.readyState === 'complete' && !!document.querySelector('[data-testid="conversation-view"]')`),
          10000,
          `${fileName} ready`
        );

        const bootPayload = {
          origin,
          path: '/',
          dictionary: { version: '2.34.0', entries: dictEntries },
          conservative: true,
          runtimeId: `win-ide-shot-${fileName}`,
          shortcutEnabled: false,
          hudEnabled: true,
          translationEnabled: true,
          languageMode: 'zh',
          locale: 'zh-Hans',
          shellValidated: true,
        };

        await cdp.evaluate(`(() => {
          delete globalThis.__GRAVITYBOX_ATTACH_DEEP_SCAN__;
          delete globalThis.__GRAVITYBOX_ATTACH_HUD__;
          globalThis.__GRAVITYBOX_BOOT__ = ${JSON.stringify(bootPayload)};
        })()`);
        await cdp.evaluate(engineCode);
        await cdp.evaluate(`globalThis.__GRAVITYBOX_HUD_V1__?.reconcileMounts?.()`);

        await until(
          () => cdp.evaluate(`Boolean(document.querySelector('#__gravitybox_hud_host__')?.shadowRoot)`),
          10000,
          `${fileName} hud host`
        );

        const metrics = buildLiveHudPayload();
        await cdp.evaluate(`(() => {
          const srHost = document.querySelector('#__gravitybox_hud_host__');
          if (${dark ? 'true' : 'false'}) srHost?.setAttribute('data-theme', 'dark');
          const mgr = globalThis.__GRAVITYBOX_HUD_V1__?.manager;
          const inst = mgr ? [...mgr.instances.values()][0] : null;
          if (!inst) return;
          const payload = ${JSON.stringify(metrics)};
          payload.sessionRevision = inst.getSessionRevision();
          mgr.dispatchMetrics(payload);
          ${spec.openSessionCard ? `srHost.shadowRoot.querySelector('.capsule-session')?.click();` : ''}
        })()`);

        await until(async () => {
          return await cdp.evaluate(`(() => {
            const sr = document.querySelector('#__gravitybox_hud_host__')?.shadowRoot;
            if (!sr) return false;
            const pct = sr.querySelector('#hud-txt-context')?.textContent?.trim();
            if (!pct || pct === '—') return false;
            ${spec.openSessionCard
              ? `const pop = sr.querySelector('#hud-popover-session');
                 return Boolean(pop && !pop.hasAttribute('hidden') && pop.getBoundingClientRect().height > 40);`
              : 'return true;'}
          })()`);
        }, 6000, `${fileName} settle`).catch(async (err) => {
          const diag = await cdp.evaluate(`(() => {
            const sr = document.querySelector('#__gravitybox_hud_host__')?.shadowRoot;
            const mgr = globalThis.__GRAVITYBOX_HUD_V1__?.manager;
            const pop = sr?.querySelector('#hud-popover-session');
            return {
              hasHost: Boolean(sr),
              instCount: mgr ? mgr.instances.size : -1,
              ctx: sr?.querySelector('#hud-txt-context')?.textContent,
              sess: sr?.querySelector('#hud-txt-session')?.textContent,
              popHidden: pop?.hasAttribute('hidden'),
              popH: pop?.getBoundingClientRect()?.height,
            };
          })()`);
          throw new Error(`${err.message} diag=${JSON.stringify(diag)}`);
        });

        await new Promise((resolve) => setTimeout(resolve, 180));
        const shot = await cdp.send('Page.captureScreenshot', { format: 'png' });
        const dest = join(outDir, fileName);
        await writeFile(dest, Buffer.from(shot.data, 'base64'));
        console.log(`✓ 已生成 Windows IDE 截图 ${fileName} (${spec.width * 2}x${spec.height * 2})`);
      }
    }
  } finally {
    try { cdp?.close(); } catch {}
    try { chrome?.kill('SIGTERM'); } catch {}
    try { server.close(); } catch {}
    await rm(profileDir, { recursive: true, force: true }).catch(() => {});
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  captureWinIdeScreenshots().catch((error) => {
    console.error('[capture-win-ide-screenshots]', error);
    process.exitCode = 1;
  });
}
