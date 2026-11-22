import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { join } from 'node:path';
import {
  scanShortcuts,
  applyShortcutDebugPort,
  restoreShortcutDebugPort,
  computeRestoredArguments,
} from '../../dist/platform/shortcuts.js';
import { WinHelperClient } from '../../dist/platform/win-helper-client.js';
import { createWinTestSandbox } from './helpers.mjs';

function createMockShortcutHelper(initialShortcuts, officialExePath) {
  const store = new Map();
  for (const item of initialShortcuts) {
    store.set(item.path.toLowerCase(), { ...item });
  }

  return {
    store,
    async request(method, params = {}) {
      if (method === 'shortcut_scan') {
        const list = [...store.values()].map(s => ({ ...s }));
        return { officialExePath, shortcuts: list };
      }
      if (method === 'shortcut_inspect') {
        const key = String(params.path || '').toLowerCase();
        const found = store.get(key);
        if (!found) throw new Error('Shortcut not found');
        return { ...found };
      }
      if (method === 'shortcut_set_args') {
        const key = String(params.path || '').toLowerCase();
        const found = store.get(key);
        if (!found) return { ok: false };
        found.arguments = String(params.arguments ?? '');
        return { ok: true, path: found.path, arguments: found.arguments };
      }
      throw new Error(`Unexpected method: ${method}`);
    },
  };
}

test('T-024: 快捷方式服务覆盖空参数、已有自定义参数、重复应用幂等、用户后续加参还原与非官方/Insiders 拒改 5 类场景', async (t) => {
  const sandbox = await createWinTestSandbox('gb-shortcuts-');
  t.after(() => sandbox.cleanup());

  const officialExe = join(sandbox.appRoot, 'Antigravity.exe');
  const desktopEmptyLnk = join(sandbox.homeDir, 'Desktop', 'Antigravity.lnk');
  const startMenuCustomLnk = join(sandbox.roamingAppData, 'Microsoft', 'Windows', 'Start Menu', 'Programs', 'Antigravity.lnk');
  const insidersLnk = join(sandbox.homeDir, 'Desktop', 'Antigravity - Insiders.lnk');
  const thirdPartyLnk = join(sandbox.homeDir, 'Desktop', 'Notepad.lnk');

  const helper = createMockShortcutHelper(
    [
      {
        path: desktopEmptyLnk,
        location: 'desktop',
        name: 'Antigravity',
        targetPath: officialExe,
        arguments: '',
      },
      {
        path: startMenuCustomLnk,
        location: 'start-menu',
        name: 'Antigravity',
        targetPath: officialExe,
        arguments: '--disable-gpu-sandbox --locale=zh-CN',
      },
      {
        path: insidersLnk,
        location: 'desktop',
        name: 'Antigravity - Insiders',
        targetPath: join(sandbox.localAppData, 'Programs', 'Antigravity - Insiders', 'Antigravity - Insiders.exe'),
        arguments: '',
      },
      {
        path: thirdPartyLnk,
        location: 'desktop',
        name: 'Notepad',
        targetPath: 'C:\\Windows\\System32\\notepad.exe',
        arguments: '',
      },
    ],
    officialExe
  );

  // 1. 扫描：严格过滤掉 Insiders 与非官方目标，仅返回官方正式版快捷方式
  const scanned1 = await scanShortcuts({
    helper,
    officialExePath: officialExe,
    stateDir: sandbox.stateDir,
  });
  assert.equal(scanned1.length, 2, '应仅扫描出 2 个官方正式版快捷方式');
  assert.deepEqual(
    scanned1.map(s => s.path),
    [desktopEmptyLnk, startMenuCustomLnk]
  );
  assert.equal(scanned1[0].hasDebugPort, false);
  assert.equal(scanned1[0].hasBackup, false);

  // 2. 尝试显式传入 Insiders / 非官方 .lnk 应用补参：必须拒改并计入 failed
  const rejectRes = await applyShortcutDebugPort({
    helper,
    officialExePath: officialExe,
    stateDir: sandbox.stateDir,
    paths: [insidersLnk, thirdPartyLnk],
  });
  assert.equal(rejectRes.updated.length, 0);
  assert.equal(rejectRes.failed.length, 2, '非官方或 Insiders 快捷方式必须拒绝修改');
  assert.equal(helper.store.get(insidersLnk.toLowerCase()).arguments, '');
  assert.equal(helper.store.get(thirdPartyLnk.toLowerCase()).arguments, '');

  // 3. 正常应用补参（覆盖场景①空参数 与 场景②已有自定义参数）
  const applyRes1 = await applyShortcutDebugPort({
    helper,
    officialExePath: officialExe,
    stateDir: sandbox.stateDir,
  });
  assert.deepEqual(applyRes1.updated, [desktopEmptyLnk, startMenuCustomLnk]);
  assert.deepEqual(applyRes1.skipped, []);
  assert.deepEqual(applyRes1.failed, []);
  assert.equal(
    helper.store.get(desktopEmptyLnk.toLowerCase()).arguments,
    '--remote-debugging-port=0'
  );
  assert.equal(
    helper.store.get(startMenuCustomLnk.toLowerCase()).arguments,
    '--disable-gpu-sandbox --locale=zh-CN --remote-debugging-port=0'
  );

  // 核对 shortcut-backups.json 持久化结构
  const backupDoc1 = JSON.parse(
    await readFile(join(sandbox.stateDir, 'shortcut-backups.json'), 'utf8')
  );
  assert.equal(backupDoc1.schema, 1);
  assert.equal(backupDoc1.entries.length, 2);
  assert.equal(backupDoc1.entries[0].originalArguments, '');
  assert.equal(backupDoc1.entries[0].appliedArguments, '--remote-debugging-port=0');
  assert.equal(backupDoc1.entries[1].originalArguments, '--disable-gpu-sandbox --locale=zh-CN');

  // 4. 场景③：重复应用幂等跳过，不重复追加 --remote-debugging-port=0，也不破坏原始备份
  const applyRes2 = await applyShortcutDebugPort({
    helper,
    officialExePath: officialExe,
    stateDir: sandbox.stateDir,
  });
  assert.deepEqual(applyRes2.updated, []);
  assert.deepEqual(applyRes2.skipped, [desktopEmptyLnk, startMenuCustomLnk]);
  assert.equal(
    helper.store.get(desktopEmptyLnk.toLowerCase()).arguments,
    '--remote-debugging-port=0'
  );

  // 5. 场景④：用户在补参后又手动向桌面快捷方式追加了自定义参数 `--max-old-space-size=4096`
  helper.store.get(desktopEmptyLnk.toLowerCase()).arguments =
    '--remote-debugging-port=0 --max-old-space-size=4096';

  const restoreRes = await restoreShortcutDebugPort({
    helper,
    stateDir: sandbox.stateDir,
  });
  assert.deepEqual(restoreRes.restored, [desktopEmptyLnk, startMenuCustomLnk]);
  assert.deepEqual(restoreRes.failed, []);

  // 桌面快捷方式保留了用户后续新增的 `--max-old-space-size=4096`，同时移除了 `--remote-debugging-port=0`
  assert.equal(
    helper.store.get(desktopEmptyLnk.toLowerCase()).arguments,
    '--max-old-space-size=4096'
  );
  // 开始菜单快捷方式无损恢复为最初的 `--disable-gpu-sandbox --locale=zh-CN`
  assert.equal(
    helper.store.get(startMenuCustomLnk.toLowerCase()).arguments,
    '--disable-gpu-sandbox --locale=zh-CN'
  );

  // 还原后备份记录已清空
  const backupDocAfter = JSON.parse(
    await readFile(join(sandbox.stateDir, 'shortcut-backups.json'), 'utf8')
  );
  assert.deepEqual(backupDocAfter.entries, []);
  assert.equal(
    computeRestoredArguments('', '--remote-debugging-port=0', '--foo --remote-debugging-port=0 --bar'),
    '--foo --bar'
  );
});

test('T-024: Windows 实机 WinHelper COM IShellLinkW .lnk 创建、补参与无损还原闭环', { skip: process.platform !== 'win32' }, async (t) => {
  const sandbox = await createWinTestSandbox('gb-shortcuts-real-');
  const client = new WinHelperClient();
  t.after(async () => {
    await client.close();
    await sandbox.cleanup();
  });

  const officialExe = join(sandbox.appRoot, 'Antigravity.exe');
  const lnkPath = join(sandbox.stateDir, 'Antigravity.lnk');
  await client.request('shortcut_create', {
    path: lnkPath,
    targetPath: officialExe,
    arguments: '--user-arg=1',
    workingDirectory: sandbox.appRoot,
  });

  const mockScanWrap = {
    async request(method, params) {
      if (method === 'shortcut_scan') {
        const info = await client.request('shortcut_inspect', { path: lnkPath });
        return {
          officialExePath: officialExe,
          shortcuts: [{ ...info, location: 'desktop', name: 'Antigravity' }],
        };
      }
      return client.request(method, params);
    },
  };

  const applied = await applyShortcutDebugPort({
    helper: mockScanWrap,
    officialExePath: officialExe,
    stateDir: sandbox.stateDir,
  });
  assert.deepEqual(applied.updated, [lnkPath]);

  const inspectedDuring = await client.request('shortcut_inspect', { path: lnkPath });
  assert.equal(inspectedDuring.arguments, '--user-arg=1 --remote-debugging-port=0');
  assert.equal(inspectedDuring.hasDebugPort, true);

  const restored = await restoreShortcutDebugPort({
    helper: mockScanWrap,
    stateDir: sandbox.stateDir,
  });
  assert.deepEqual(restored.restored, [lnkPath]);

  const inspectedAfter = await client.request('shortcut_inspect', { path: lnkPath });
  assert.equal(inspectedAfter.arguments, '--user-arg=1');
  assert.equal(inspectedAfter.hasDebugPort, false);
});
