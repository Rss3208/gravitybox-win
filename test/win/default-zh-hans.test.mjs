import test from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { createConnection } from 'node:net';
import { readFile } from 'node:fs/promises';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { detectSystemLocales, resolveInitialRuntimeLanguage, translate } from '../../dist/i18n.js';
import { getPlatform } from '../../dist/platform/index.js';
import { createWinTestSandbox } from './helpers.mjs';

const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
const MAIN_ENTRY = fileURLToPath(new URL('../../dist/main.js', import.meta.url));

async function rpc(stateDir, payload) {
  const platform = getPlatform();
  const endpoint = platform.getIpcEndpoint(stateDir);
  const authToken = await platform.readIpcAuthToken?.(stateDir);
  const body = authToken && !('authToken' in payload) ? { ...payload, authToken } : payload;
  return new Promise((resolve, reject) => {
    const socket = createConnection(endpoint);
    let buf = '';
    socket.setTimeout(8000, () => {
      socket.destroy();
      reject(new Error('IPC timeout'));
    });
    socket.on('connect', () => socket.write(JSON.stringify(body) + '\n'));
    socket.on('error', reject);
    socket.on('data', chunk => {
      buf += chunk.toString('utf8');
      if (buf.includes('\n')) {
        socket.end();
        resolve(JSON.parse(buf.split('\n')[0]));
      }
    });
  });
}

test('T-022: Windows 开箱默认简体中文策略 (FR-001 / INV-06) — 即使系统语言为 en-US，全新安装首启仍默认 zh-Hans + 开启汉化 + 开启 HUD，并支持热切 en / system / zh-Hans', async () => {
  // 1. 验证 resolveInitialRuntimeLanguage 支持 defaultFreshLanguage: 'zh-Hans'
  assert.deepEqual(
    resolveInitialRuntimeLanguage({
      systemLocales: ['en-US'],
      defaultFreshLanguage: 'zh-Hans',
    }),
    { configuredLanguage: 'zh-Hans', resolvedLocale: 'zh-Hans' },
  );

  // 2. 验证 Windows 专属 UI 文案键双语完整就绪
  assert.equal(translate('zh-Hans', 'about.reveal_data_win', undefined, { namespace: 'app' }), '在文件资源管理器中显示');
  assert.equal(translate('en', 'about.reveal_data_win', undefined, { namespace: 'app' }), 'Show in File Explorer');
  assert.equal(translate('zh-Hans', 'settings.login.subtitle_win', undefined, { namespace: 'app' }), '登录 Windows 时自动在系统托盘驻留');
  assert.equal(translate('zh-Hans', 'settings.shortcut.btn_apply', undefined, { namespace: 'app' }), '一键开启无感启动');

  // 3. 验证 detectSystemLocales() 在 Windows 上能读取系统首选语言
  const sysLocales = detectSystemLocales();
  assert.ok(Array.isArray(sysLocales) && sysLocales.length > 0);

  // 4. 在模拟 en-US 系统语言的全新沙箱目录启动后端（无 config.json、无 runtime-preferences.json、无 --locale）
  const sandbox = await createWinTestSandbox('gb-win-default-zh-');
  const env = { ...process.env, GRAVITYBOX_PROBE_LS: 'false', GRAVITYBOX_SYSTEM_LOCALES: 'en-US' };
  delete env.LANG;
  delete env.LC_ALL;
  delete env.LC_MESSAGES;

  const child = spawn(
    process.execPath,
    [
      MAIN_ENTRY,
      '--state-dir', sandbox.stateDir,
      '--system-locales', 'en-US',
      '--app', join(sandbox.appRoot, 'nonexistent'),
      '--profile', sandbox.profileDir,
      '--home-dir', sandbox.homeDir,
      '--app-support-dir', sandbox.roamingAppData,
      '--gemini-dir', sandbox.geminiDir,
    ],
    { stdio: ['ignore', 'ignore', 'pipe'], env },
  );
  const closed = new Promise(resolve => child.once('close', resolve));

  try {
    let initialStatus;
    const deadline = Date.now() + 12_000;
    while (Date.now() < deadline) {
      try {
        const res = await rpc(sandbox.stateDir, { command: 'status' });
        if (res?.ok) {
          initialStatus = res.status;
          break;
        }
      } catch {}
      await sleep(40);
    }

    assert.ok(initialStatus, '后端必须在时限内启动');
    assert.equal(initialStatus.configuredLanguage, 'zh-Hans', '全新安装即使传入 --system-locales en-US 也必须默认 configuredLanguage=zh-Hans');
    assert.equal(initialStatus.locale, 'zh-Hans', '全新安装默认 locale 必须为 zh-Hans');
    assert.equal(initialStatus.translationEnabled, true, '全新安装默认开启 IDE 汉化');
    assert.equal(initialStatus.hudEnabled, true, '全新安装默认开启 HUD');
    assert.ok(initialStatus.entries > 0, '全新安装必须默认加载简体中文词典');

    // 5. 用户显式切换到 English ('en')
    const toEn = await rpc(sandbox.stateDir, { command: 'language_set', language: 'en' });
    assert.equal(toEn.ok, true);
    assert.equal(toEn.status.configuredLanguage, 'en');
    assert.equal(toEn.status.locale, 'en');
    assert.equal(toEn.status.translationEnabled, false);

    // 6. 用户显式切换到「跟随系统」('system') 且系统为 en-US -> 解析为 'en'
    const toSysEn = await rpc(sandbox.stateDir, {
      command: 'language_set',
      language: 'system',
      systemLocales: ['en-US'],
    });
    assert.equal(toSysEn.ok, true);
    assert.equal(toSysEn.status.configuredLanguage, 'system');
    assert.equal(toSysEn.status.locale, 'en');
    assert.equal(toSysEn.status.translationEnabled, false);

    // 7. 用户显式切回简体中文 ('zh-Hans')
    const toZh = await rpc(sandbox.stateDir, { command: 'language_set', language: 'zh-Hans' });
    assert.equal(toZh.ok, true);
    assert.equal(toZh.status.configuredLanguage, 'zh-Hans');
    assert.equal(toZh.status.locale, 'zh-Hans');
    assert.equal(toZh.status.translationEnabled, true);
    assert.ok(toZh.status.entries > 0);

    const savedPrefs = JSON.parse(await readFile(join(sandbox.stateDir, 'runtime-preferences.json'), 'utf8'));
    assert.equal(savedPrefs.language, 'zh-Hans');
    assert.equal(savedPrefs.translationEnabled, true);

    await rpc(sandbox.stateDir, { command: 'stop' });
    await closed;
  } finally {
    if (child.exitCode === null) child.kill();
    await closed;
    await sandbox.cleanup();
  }
});
