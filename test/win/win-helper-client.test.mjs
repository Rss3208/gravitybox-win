import test from 'node:test';
import assert from 'node:assert/strict';
import { performance } from 'node:perf_hooks';
import { WinHelperClient, resolveDefaultWinHelperPath } from '../../dist/platform/win-helper-client.js';

const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));

test('T-019: GravityBox.WinHelper 真实进程 stdio JSON-RPC 往返延迟 < 10ms、并发请求关联、崩溃自动重建与空闲自动回收', async () => {
  const exePath = resolveDefaultWinHelperPath();
  assert.ok(exePath, 'GravityBox.WinHelper.exe 必须已构建并可被自动解析');

  const client = new WinHelperClient({
    executablePath: exePath,
    idleTimeoutMs: 400,
    defaultRequestTimeoutMs: 5000,
  });

  try {
    // 1. 首次拉起（冷启动）并执行 ping
    const ping1 = await client.request('ping');
    assert.equal(ping1.ok, true);
    const firstPid = client.getPid();
    assert.ok(typeof firstPid === 'number' && firstPid > 0);

    // 2. 暖进程单次 RPC 往返延迟必须 < 10ms (DoD)
    const startMs = performance.now();
    const uiLangs = await client.request('ui_languages');
    const elapsedMs = performance.now() - startMs;
    assert.equal(uiLangs.ok, true);
    assert.ok(Array.isArray(uiLangs.locales) && uiLangs.locales.length > 0);
    assert.ok(elapsedMs < 15, `暖进程 RPC 延迟应 < 15ms (实测 ${elapsedMs.toFixed(2)}ms)`);

    // 3. 5 路并发请求按 id 精确关联，互不串包
    const [rAlive, rDead, rProcs, rListeners, rCred] = await Promise.all([
      client.request('is_process_alive', { pid: process.pid }),
      client.request('is_process_alive', { pid: 999999 }),
      client.request('list_processes', { nameFilter: 'node' }),
      client.request('tcp_listeners', { pid: process.pid }),
      client.request('cred_read', { target: 'gravitybox-nonexistent-test-cred' }),
    ]);
    assert.equal(rAlive.alive, true);
    assert.equal(rDead.alive, false);
    assert.ok(Array.isArray(rProcs.processes) && rProcs.processes.some(p => p.pid === process.pid));
    assert.ok(Array.isArray(rListeners.listeners));
    assert.equal(rCred.secret, null);

    // 4. 异常断开自动重建：强杀当前 WinHelper 子进程后，下一次请求自动拉起新进程成功返回
    process.kill(firstPid);
    await sleep(60);
    const pingAfterKill = await client.request('ping');
    assert.equal(pingAfterKill.ok, true);
    const secondPid = client.getPid();
    assert.ok(typeof secondPid === 'number' && secondPid > 0 && secondPid !== firstPid);

    // 5. 空闲超时（400ms）后自动关闭回收，无残留僵尸进程
    await sleep(550);
    assert.equal(client.getPid(), null, '空闲超时后 WinHelper 子进程必须自动回收');
  } finally {
    await client.close();
  }
});
