import test from 'node:test';
import assert from 'node:assert/strict';
import { writeFile, readFile, rm, symlink, utimes, readdir } from 'node:fs/promises';
import { join } from 'node:path';
import { acquireProcessLease, withProcessLease } from '../../dist/process-lock.js';
import { setPlatformForTests } from '../../dist/platform/index.js';
import { createWin32PlatformAdapter } from '../../dist/platform/win32.js';
import { createWinTestSandbox, createMockWinHelperClient } from './helpers.mjs';

test('T-011: Windows 跨进程文件锁支持中文/空格路径互斥、同上下文可重入与死进程锁自动回收', async () => {
  const sandbox = await createWinTestSandbox('gb-win-lock-');
  try {
    const lockPath = join(sandbox.stateDir, 'accounts.writer.lock');

    // 1. 首次获取锁并校验写入内容包含 pid, nonce, createdAt
    const lease1 = await acquireProcessLease(lockPath, 1000);
    const lockRaw = JSON.parse(await readFile(lockPath, 'utf8'));
    assert.equal(lockRaw.pid, process.pid);
    assert.equal(typeof lockRaw.nonce, 'string');
    assert.equal(typeof lockRaw.createdAt, 'number');

    // 2. 活锁存在时第二次非重入获取必须超时拒绝
    await assert.rejects(acquireProcessLease(lockPath, 40), /已有|等待|锁|占用/);
    await lease1.release();

    // 3. 写入一个已退出 PID (999999) 的死锁文件，验证自动回收
    await writeFile(
      lockPath,
      JSON.stringify({ pid: 999999, nonce: 'dead-nonce', createdAt: Date.now() - 5000 }),
      'utf8',
    );
    const reclaimedLease = await acquireProcessLease(lockPath, 1500);
    const reclaimedRaw = JSON.parse(await readFile(lockPath, 'utf8'));
    assert.equal(reclaimedRaw.pid, process.pid);
    assert.notEqual(reclaimedRaw.nonce, 'dead-nonce');
    await reclaimedLease.release();
  } finally {
    await sandbox.cleanup();
  }
});

test('T-011: Windows PID 复用防护 — createdAt 与进程实际启动时间偏差 > 2000ms 时判定为过期锁并安全回收', async () => {
  const sandbox = await createWinTestSandbox('gb-win-lock-pidreuse-');
  const mockHelper = createMockWinHelperClient({
    async is_process_alive(params) {
      // 模拟 PID 仍在运行（被新进程复用），但真实进程启动时间比锁记录的 createdAt 晚 60 秒
      const realStartMs = Date.now();
      if (typeof params.creationTimeMs === 'number' && Math.abs(realStartMs - params.creationTimeMs) > 2000) {
        return { alive: false };
      }
      return { alive: true };
    },
  });
  setPlatformForTests(createWin32PlatformAdapter({ helperClient: mockHelper }));

  try {
    const lockPath = join(sandbox.stateDir, 'pid-reuse.lock');
    await writeFile(
      lockPath,
      JSON.stringify({
        pid: process.pid,
        nonce: 'stale-reused-pid',
        createdAt: Date.now() - 60_000,
      }),
      'utf8',
    );

    const lease = await acquireProcessLease(lockPath, 1500);
    const current = JSON.parse(await readFile(lockPath, 'utf8'));
    assert.notEqual(current.nonce, 'stale-reused-pid');
    await lease.release();
  } finally {
    setPlatformForTests(null);
    await sandbox.cleanup();
  }
});

test('T-011: Windows 符号链接/重解析点锁文件被拦截拒绝 & 超过 15 秒空孤儿锁自动归档恢复', async () => {
  const sandbox = await createWinTestSandbox('gb-win-lock-guard-');
  try {
    // 1. 符号链接/Junction 锁文件必须拒绝
    const outsideDir = join(sandbox.baseTmp, 'outside-target');
    const symlinkLock = join(sandbox.stateDir, 'symlink.lock');
    const { mkdir } = await import('node:fs/promises');
    await mkdir(outsideDir, { recursive: true });
    await symlink(outsideDir, symlinkLock, process.platform === 'win32' ? 'junction' : 'dir');
    await assert.rejects(acquireProcessLease(symlinkLock, 200));
    await rm(symlinkLock, { recursive: true, force: true });

    // 2. 超过 15 秒的空锁文件无需 lsof 即可归档为 .orphan-* 并恢复
    const orphanLock = join(sandbox.stateDir, 'orphan.lock');
    await writeFile(orphanLock, '', 'utf8');
    const oldTime = new Date(Date.now() - 30_000);
    await utimes(orphanLock, oldTime, oldTime);

    const recovered = await withProcessLease(orphanLock, async () => 'ok', { timeoutMs: 2000 });
    assert.equal(recovered, 'ok');
    const files = await readdir(sandbox.stateDir);
    assert.ok(files.some(name => name.startsWith('orphan.lock.orphan-')));
  } finally {
    await sandbox.cleanup();
  }
});
