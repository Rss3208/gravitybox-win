import test from 'node:test';
import assert from 'node:assert/strict';
import { existsSync } from 'node:fs';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { createWinTestSandbox, createMockWinHelperClient } from './helpers.mjs';
import {
  CacheMaintenance,
  hasAntigravityCommand,
  hasBrowserProfileCommand,
  isStrictSubpath,
} from '../../dist/maintenance.js';
import {
  executeMoves,
  readMaintenanceManifest,
  reconcileMoves,
} from '../../dist/maintenance-journal.js';
import { createWin32PlatformAdapter, setPlatformForTests } from '../../dist/platform/index.js';

test('T-010: Windows 进程命令行匹配（Antigravity.exe / language_server.exe / browser_profile）与大小写不敏感子路径', () => {
  const appRoot = 'C:\\Users\\demo\\AppData\\Local\\Programs\\Antigravity';
  assert.equal(
    hasAntigravityCommand([`"${appRoot}\\Antigravity.exe" --remote-debugging-port=0`], appRoot),
    true
  );
  assert.equal(
    hasAntigravityCommand([`${appRoot.toLowerCase()}\\resources\\bin\\language_server.exe --standalone`], appRoot),
    true
  );
  assert.equal(
    hasAntigravityCommand(['C:\\Windows\\System32\\notepad.exe'], appRoot),
    false
  );

  const bpDir = 'C:\\Users\\demo\\.gemini\\antigravity\\browser_profile';
  assert.equal(
    hasBrowserProfileCommand([`"C:\\Program Files\\Chrome\\chrome.exe" --user-data-dir="${bpDir.toUpperCase()}"`], bpDir),
    true
  );

  assert.equal(
    isStrictSubpath('C:\\Users\\demo\\AppData\\Roaming\\Antigravity', 'c:\\users\\demo\\appdata\\roaming\\antigravity\\Cache\\data_0'),
    true
  );
});

test('T-010: executeMoves 与 reconcileMoves 支持 EXDEV 跨卷安全移动（复制-校验-原子切换-删除）与中途故障回滚', async t => {
  const sandbox = await createWinTestSandbox('gb-t010-exdev-');
  t.after(() => sandbox.cleanup());

  const srcDir = join(sandbox.profileDir, 'Cache');
  const srcSub = join(srcDir, 'Cache_Data');
  await mkdir(srcSub, { recursive: true });
  await writeFile(join(srcSub, 'data_0'), 'cache-payload-0');
  await writeFile(join(srcSub, 'data_1'), 'cache-payload-12345');

  const dstDir = join(sandbox.trashDir, 'GravityBox-缓存-1700000000', 'Cache');
  const manifestPath = join(sandbox.trashDir, 'GravityBox-缓存-1700000000', '恢复清单.json');
  await mkdir(join(sandbox.trashDir, 'GravityBox-缓存-1700000000'), { recursive: true });

  // 注入模拟跨卷 EXDEV 的 rename 函数（仅对同目录 __staging__ -> dstDir 放行同卷原子重命名）
  const exdevRename = async (from, to) => {
    if (String(from).endsWith('.__staging__')) {
      const { rename } = await import('node:fs/promises');
      return rename(from, to);
    }
    const err = new Error('EXDEV: cross-device link not permitted');
    err.code = 'EXDEV';
    throw err;
  };

  const manifest = {
    schema: 1,
    at: new Date().toISOString(),
    moved: [{ entryName: 'Cache', from: srcDir, to: dstDir }],
  };
  await executeMoves(
    manifestPath,
    manifest,
    [{ from: srcDir, to: dstDir }],
    async () => {},
    {},
    undefined,
    exdevRename
  );

  assert.equal(existsSync(srcDir), false, '跨卷移动完成后源目录应被移除');
  assert.equal(await readFile(join(dstDir, 'Cache_Data', 'data_0'), 'utf8'), 'cache-payload-0');
  assert.equal(await readFile(join(dstDir, 'Cache_Data', 'data_1'), 'utf8'), 'cache-payload-12345');

  const committedManifest = await readMaintenanceManifest(manifestPath);
  assert.equal(committedManifest.transaction.phase, 'committed');
  assert.equal(committedManifest.transaction.moves[0].strategy, 'cross-volume');
});

test('T-010: Windows 文件锁占用（EBUSY / EPERM）触发自动事务回滚，已移项完整归位 (FR-030)', async t => {
  const sandbox = await createWinTestSandbox('gb-t010-ebusy-');
  t.after(() => sandbox.cleanup());

  const cache1 = join(sandbox.profileDir, 'Cache');
  const cache2 = join(sandbox.profileDir, 'GPUCache');
  await mkdir(cache1, { recursive: true });
  await mkdir(cache2, { recursive: true });
  await writeFile(join(cache1, 'a.bin'), 'first-cache-content');
  await writeFile(join(cache2, 'b.bin'), 'second-locked-content');

  const snapDir = join(sandbox.trashDir, 'GravityBox-缓存-1700000001');
  await mkdir(snapDir, { recursive: true });
  const manifestPath = join(snapDir, '恢复清单.json');
  const dst1 = join(snapDir, 'Cache');
  const dst2 = join(snapDir, 'GPUCache');

  const { rename } = await import('node:fs/promises');
  let moveCallCount = 0;
  const lockingRename = async (from, to) => {
    moveCallCount++;
    // 第 1 项 Cache 移动成功，第 2 项 GPUCache 模拟被外部进程占用抛出 EBUSY
    if (moveCallCount === 2) {
      const err = new Error(`EBUSY: resource busy or locked, rename '${from}' -> '${to}'`);
      err.code = 'EBUSY';
      throw err;
    }
    return rename(from, to);
  };

  const manifest = {
    schema: 1,
    at: new Date().toISOString(),
    moved: [
      { entryName: 'Cache', from: cache1, to: dst1 },
      { entryName: 'GPUCache', from: cache2, to: dst2 },
    ],
  };

  await assert.rejects(
    () =>
      executeMoves(
        manifestPath,
        manifest,
        [
          { from: cache1, to: dst1 },
          { from: cache2, to: dst2 },
        ],
        async () => {},
        {},
        undefined,
        lockingRename
      ),
    /EBUSY|占用|锁定/
  );

  // 验证第 1 项已自动回滚回原路径，绝不留下半残状态
  assert.equal(existsSync(cache1), true, '第 1 项必须自动回滚归位');
  assert.equal(await readFile(join(cache1, 'a.bin'), 'utf8'), 'first-cache-content');
  assert.equal(existsSync(cache2), true);
  assert.equal(await readFile(join(cache2, 'b.bin'), 'utf8'), 'second-locked-content');
  const afterManifest = await readMaintenanceManifest(manifestPath);
  assert.equal(afterManifest.transaction.phase, 'rolled-back');
});

test('T-010: CacheMaintenance 在 Windows 含中文/空格路径下完成扫描、清理与一键原位还原闭环', async t => {
  const sandbox = await createWinTestSandbox('gb-t010-e2e-');
  t.after(() => {
    setPlatformForTests(null);
    return sandbox.cleanup();
  });

  const helper = createMockWinHelperClient();
  setPlatformForTests(createWin32PlatformAdapter({ helperClient: helper }));

  await mkdir(join(sandbox.profileDir, 'Cache'), { recursive: true });
  await writeFile(join(sandbox.profileDir, 'Cache', 'entry1.dat'), 'hello-cache-123');
  await mkdir(join(sandbox.profileDir, 'User', 'globalStorage'), { recursive: true });
  await writeFile(join(sandbox.profileDir, 'User', 'globalStorage', 'state.vscdb'), 'protected-db');

  const maint = new CacheMaintenance(
    sandbox.profileDir,
    sandbox.trashDir,
    async () => false,
    {
      appPath: sandbox.appRoot,
      stateDir: sandbox.stateDir,
      geminiDir: sandbox.geminiDir,
      browserRunning: async () => false,
    }
  );

  const scanRes = await maint.scan();
  assert.equal(scanRes.ok, true);
  assert.ok(scanRes.safeBytes >= 15);
  assert.ok(scanRes.protectedBytes >= 12);

  const clearRes = await maint.smartClear({ scopes: ['runtime_cache'] });
  assert.equal(clearRes.ok, true);
  assert.equal(existsSync(join(sandbox.profileDir, 'Cache')), false);
  assert.equal(existsSync(join(sandbox.profileDir, 'User', 'globalStorage', 'state.vscdb')), true, '受保护数据绝不可被清理');

  const restoreRes = await maint.restore(clearRes.snapshotId);
  assert.equal(restoreRes.ok, true);
  assert.equal(await readFile(join(sandbox.profileDir, 'Cache', 'entry1.dat'), 'utf8'), 'hello-cache-123');
});
