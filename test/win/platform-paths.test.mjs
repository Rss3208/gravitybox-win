import test from 'node:test';
import assert from 'node:assert/strict';
import { join } from 'node:path';
import { writeFile, symlink } from 'node:fs/promises';
import { createWinTestSandbox, createMockWinHelperClient } from './helpers.mjs';
import {
  resolvePlatformPaths,
  resolveAppLayoutForPlatform,
  isInsidersPathForPlatform,
} from '../../dist/platform/paths.js';
import {
  isStrictSubpathForPlatform,
  sanitizeHostPaths,
  assertOwnedPathForPlatform,
  assertOwnedPathSyncForPlatform,
} from '../../dist/platform/security.js';
import {
  getPlatform,
  setPlatformForTests,
  createWin32PlatformAdapter,
  createDarwinPlatformAdapter,
} from '../../dist/platform/index.js';

test('T-005: Windows 与 macOS 默认路径解析及 CJK/空格路径兼容', () => {
  const winHome = ['C:', 'Users', '测试 用户'].join('\\');
  const winEnv = {
    USERPROFILE: winHome,
    LOCALAPPDATA: `${winHome}\\AppData\\Local`,
    APPDATA: `${winHome}\\AppData\\Roaming`,
    ProgramFiles: 'C:\\Program Files',
  };
  const winPaths = resolvePlatformPaths('win32', winHome, winEnv);
  assert.equal(winPaths.stateDir, `${winHome}\\AppData\\Local\\GravityBox`);
  assert.equal(winPaths.cacheDir, `${winHome}\\AppData\\Local\\GravityBox\\cache`);
  assert.equal(winPaths.logDir, `${winHome}\\AppData\\Local\\GravityBox\\logs`);
  assert.equal(winPaths.defaultTrashDir, `${winHome}\\AppData\\Local\\GravityBox\\maintenance-trash`);
  assert.equal(winPaths.defaultAntigravityProfile, `${winHome}\\AppData\\Roaming\\Antigravity`);
  assert.equal(winPaths.defaultGeminiDir, `${winHome}\\.gemini`);
  assert.ok(winPaths.defaultAppCandidates.includes(`${winHome}\\AppData\\Local\\Programs\\Antigravity`));
  assert.ok(winPaths.defaultAppCandidates.includes('C:\\Program Files\\Antigravity'));

  const macHome = '/Users/demo';
  const macPaths = resolvePlatformPaths('darwin', macHome, {});
  assert.equal(macPaths.stateDir, '/Users/demo/Library/Application Support/GravityBox');
  assert.equal(macPaths.cacheDir, '/Users/demo/Library/Caches/GravityBox');
  assert.equal(macPaths.logDir, '/Users/demo/Library/Logs/GravityBox');
  assert.equal(macPaths.defaultTrashDir, '/Users/demo/.Trash');
  assert.equal(macPaths.defaultAntigravityProfile, '/Users/demo/Library/Application Support/Antigravity');
  assert.equal(macPaths.defaultGeminiDir, '/Users/demo/.gemini');
});

test('T-005: Windows 与 macOS 应用包目录布局解析与 Insiders 识别', () => {
  const winLayout = resolveAppLayoutForPlatform('win32', 'C:\\Users\\demo\\AppData\\Local\\Programs\\Antigravity');
  assert.equal(winLayout.executablePath, 'C:\\Users\\demo\\AppData\\Local\\Programs\\Antigravity\\Antigravity.exe');
  assert.equal(winLayout.asarPath, 'C:\\Users\\demo\\AppData\\Local\\Programs\\Antigravity\\resources\\app.asar');
  assert.equal(winLayout.binDir, 'C:\\Users\\demo\\AppData\\Local\\Programs\\Antigravity\\resources\\bin');
  assert.ok(winLayout.languageServerExePattern.test('language_server.exe'));
  assert.ok(winLayout.languageServerExePattern.test('language_server_windows_x64.exe'));

  const macLayout = resolveAppLayoutForPlatform('darwin', '/Applications/Antigravity.app');
  assert.equal(macLayout.executablePath, '/Applications/Antigravity.app/Contents/MacOS/Antigravity');
  assert.equal(macLayout.asarPath, '/Applications/Antigravity.app/Contents/Resources/app.asar');
  assert.equal(macLayout.binDir, '/Applications/Antigravity.app/Contents/Resources/bin');

  assert.equal(isInsidersPathForPlatform('win32', 'C:\\Users\\demo\\AppData\\Local\\Programs\\Antigravity - Insiders'), true);
  assert.equal(isInsidersPathForPlatform('win32', 'C:\\Users\\demo\\AppData\\Roaming\\Antigravity - Insiders'), true);
  assert.equal(isInsidersPathForPlatform('win32', 'C:\\Users\\demo\\AppData\\Local\\Programs\\Antigravity'), false);
  assert.equal(isInsidersPathForPlatform('darwin', '/Applications/Antigravity - Insiders.app'), true);
});

test('T-005: isStrictSubpath 跨平台边界（Windows 大小写不敏感、防前缀逃逸与跨盘符）', () => {
  const parentWin = 'C:\\Users\\demo\\测试 用户\\AppData\\Local\\GravityBox\\accounts';
  assert.equal(
    isStrictSubpathForPlatform('win32', parentWin, 'c:\\users\\demo\\测试 用户\\appdata\\local\\gravitybox\\accounts\\acc-1.json'),
    true
  );
  assert.equal(
    isStrictSubpathForPlatform('win32', parentWin, 'C:\\Users\\demo\\测试 用户\\AppData\\Local\\GravityBox\\accounts'),
    false,
    '父目录自身不得被判为严格子路径'
  );
  assert.equal(
    isStrictSubpathForPlatform('win32', parentWin, 'C:\\Users\\demo\\测试 用户\\AppData\\Local\\GravityBox\\accounts-evil\\acc.json'),
    false,
    '同前缀兄弟目录必须被拦截'
  );
  assert.equal(
    isStrictSubpathForPlatform('win32', parentWin, 'C:\\Users\\demo\\测试 用户\\AppData\\Local\\GravityBox\\accounts\\..\\config.json'),
    false,
    '父级目录逃逸必须被拦截'
  );
  assert.equal(
    isStrictSubpathForPlatform('win32', parentWin, 'D:\\Users\\demo\\测试 用户\\AppData\\Local\\GravityBox\\accounts\\acc.json'),
    false,
    '跨盘符路径必须被拦截'
  );
});

test('T-005: sanitizeHostPaths 同时脱敏 Windows 与 POSIX 本机用户路径', () => {
  const cjkWinPath = ['c:', 'Users', '测试用户', 'Desktop', 'a.lnk'].join('/');
  const raw = `Failed at C:\\Users\\alice\\AppData\\Local\\GravityBox\\config.json and ${cjkWinPath} and /Users/bob/Library/foo with token secret-tok-123`;
  const clean = sanitizeHostPaths(raw, 'secret-tok-123');
  assert.ok(!clean.includes('alice'));
  assert.ok(!clean.includes('测试用户'));
  assert.ok(!clean.includes('bob'));
  assert.ok(!clean.includes('secret-tok-123'));
  assert.ok(clean.includes('<path>'));
  assert.ok(clean.includes('<redacted>'));
});

test('T-005: assertOwnedPath 与 ReparsePoint / 符号链接 / 非当前用户拦截', async t => {
  const sandbox = await createWinTestSandbox('gb-t005-sec-');
  t.after(() => sandbox.cleanup());

  const file = join(sandbox.stateDir, 'config.json');
  await writeFile(file, '{"schema":2}\n');

  const helper = createMockWinHelperClient();
  await assert.doesNotReject(() => assertOwnedPathForPlatform('win32', file, 'file', helper));
  assert.doesNotThrow(() => assertOwnedPathSyncForPlatform('win32', file, 'file'));

  // 类型不匹配（目录被当作 file 校验）
  await assert.rejects(() => assertOwnedPathForPlatform('win32', sandbox.stateDir, 'file', helper));
  assert.throws(() => assertOwnedPathSyncForPlatform('win32', sandbox.stateDir, 'file'));

  // WinHelper 报告 ReparsePoint 或非当前用户拥有时必须拒绝
  const reparseHelper = createMockWinHelperClient({
    async acl_verify_owner() {
      return { ownedByCurrentUser: true, isReparsePoint: true, exists: true };
    },
  });
  await assert.rejects(() => assertOwnedPathForPlatform('win32', file, 'file', reparseHelper));

  const foreignOwnerHelper = createMockWinHelperClient({
    async acl_verify_owner() {
      return { ownedByCurrentUser: false, isReparsePoint: false, exists: true };
    },
  });
  await assert.rejects(() => assertOwnedPathForPlatform('win32', file, 'file', foreignOwnerHelper));

  // 测试平台单例注入与恢复
  const customWin = createWin32PlatformAdapter({ helperClient: helper });
  setPlatformForTests(customWin);
  assert.equal(getPlatform().id, 'win32');
  const customMac = createDarwinPlatformAdapter();
  setPlatformForTests(customMac);
  assert.equal(getPlatform().id, 'darwin');
  setPlatformForTests(null);
});
