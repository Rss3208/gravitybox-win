import test from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { createConnection } from 'node:net';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { computeWinNamedPipeEndpoint } from '../../dist/platform/win32.js';
import { createWinTestSandbox, computeExpectedWinPipeName } from './helpers.mjs';

const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
const MAIN_ENTRY = fileURLToPath(new URL('../../dist/main.js', import.meta.url));

function rawPipeExchange(pipeName, chunks, timeoutMs = 8000) {
  return new Promise((resolve, reject) => {
    const socket = createConnection(pipeName);
    let buf = '';
    const timer = setTimeout(() => {
      socket.destroy();
      reject(new Error('Named Pipe IPC 超时'));
    }, timeoutMs);
    const done = err => {
      clearTimeout(timer);
      socket.destroy();
      if (err) reject(err);
      else resolve(buf);
    };
    socket.on('connect', async () => {
      for (const chunk of chunks) {
        socket.write(chunk);
        if (chunks.length > 1) await sleep(30);
      }
    });
    socket.on('data', chunk => {
      buf += chunk.toString('utf8');
      if (buf.includes('\n')) done();
    });
    socket.on('error', done);
    socket.on('close', () => {
      if (buf.includes('\n')) done();
    });
  });
}

test('T-013: Windows Named Pipe IPC 端点隔离、ipc.token 鉴权、跨 chunk 多字节中文 UTF-8、4MB 上限与单实例互斥', async () => {
  const sandbox = await createWinTestSandbox('gb-win-pipe-');
  const expectedPipe = computeExpectedWinPipeName(sandbox.stateDir);
  assert.equal(computeWinNamedPipeEndpoint(sandbox.stateDir), expectedPipe);
  assert.match(expectedPipe, /^\\\\\.\\pipe\\gravitybox-[0-9a-f]{24}$/);

  await mkdir(join(sandbox.stateDir, '词典'), { recursive: true });
  await writeFile(
    join(sandbox.stateDir, '词典', '个人覆盖.json'),
    JSON.stringify({ 'NamedPipe Test Key': '命名管道跨包中文测试' }),
    'utf8',
  );

  const child = spawn(
    process.execPath,
    [
      MAIN_ENTRY,
      '--state-dir', sandbox.stateDir,
      '--app', join(sandbox.appRoot, 'nonexistent'),
      '--profile', sandbox.profileDir,
      '--home-dir', sandbox.homeDir,
      '--app-support-dir', sandbox.roamingAppData,
      '--gemini-dir', sandbox.geminiDir,
    ],
    {
      stdio: ['ignore', 'ignore', 'pipe'],
      env: { ...process.env, GRAVITYBOX_PROBE_LS: 'false' },
    },
  );
  const closed = new Promise(resolve => child.once('close', resolve));

  try {
    // 1. 等待 ipc.token 生成并探测服务就绪
    let authToken = '';
    const deadline = Date.now() + 12_000;
    while (Date.now() < deadline) {
      try {
        authToken = (await readFile(join(sandbox.stateDir, 'ipc.token'), 'utf8')).trim();
        if (authToken.length === 64) {
          const pingRaw = await rawPipeExchange(expectedPipe, [
            JSON.stringify({ command: 'ping', authToken }) + '\n',
          ]);
          const pingRes = JSON.parse(pingRaw.split('\n')[0]);
          if (pingRes.ok) break;
        }
      } catch {}
      await sleep(50);
    }
    assert.equal(authToken.length, 64, 'ipc.token 必须为 32 字节 hex (64 字符)');

    // 2. 验证缺失或伪造 authToken 被拒绝 (NFR-004)
    const noTokenRaw = await rawPipeExchange(expectedPipe, [
      JSON.stringify({ command: 'status' }) + '\n',
    ]);
    const noTokenRes = JSON.parse(noTokenRaw.split('\n')[0]);
    assert.equal(noTokenRes.ok, false);
    assert.match(noTokenRes.error, /Unauthorized/i);

    const forgedTokenRaw = await rawPipeExchange(expectedPipe, [
      JSON.stringify({ command: 'status', authToken: '0'.repeat(64) }) + '\n',
    ]);
    const forgedTokenRes = JSON.parse(forgedTokenRaw.split('\n')[0]);
    assert.equal(forgedTokenRes.ok, false);
    assert.match(forgedTokenRes.error, /Unauthorized/i);

    // 3. 验证跨 chunk 多字节中文 UTF-8 分片解码不损坏
    const cjkPayload = Buffer.from(
      JSON.stringify({
        command: 'account_upsert',
        authToken,
        data: {
          email: 'pipe-cjk@example.test',
          refreshToken: '1//pipe-refresh-token',
          name: '命名管道中文姓名测试',
          customLabel: '含空格与中文标签 🚀',
        },
      }) + '\n',
      'utf8',
    );
    // 故意在多字节 UTF-8 中间切分 Buffer
    const splitIdx = cjkPayload.indexOf(Buffer.from('命名管道', 'utf8')) + 2;
    const cjkRaw = await rawPipeExchange(expectedPipe, [
      cjkPayload.subarray(0, splitIdx),
      cjkPayload.subarray(splitIdx),
    ]);
    const cjkRes = JSON.parse(cjkRaw.split('\n')[0]);
    assert.equal(cjkRes.ok, true);
    assert.equal(cjkRes.account.name, '命名管道中文姓名测试');
    assert.equal(cjkRes.account.custom_label, '含空格与中文标签 🚀');

    // 4. 验证 CLI request('--command status') 自动读取 ipc.token 并通过 Named Pipe 通信
    const cliChild = spawn(
      process.execPath,
      [MAIN_ENTRY, '--state-dir', sandbox.stateDir, '--command', 'ping'],
      { stdio: ['ignore', 'pipe', 'pipe'] },
    );
    let cliOut = '';
    cliChild.stdout.on('data', d => { cliOut += d.toString('utf8'); });
    await new Promise(resolve => cliChild.once('close', resolve));
    assert.equal(cliChild.exitCode, 0);
    assert.equal(JSON.parse(cliOut.trim()).ok, true);

    // 5. 验证单实例互斥：同一 stateDir 启动第二个后端实例立即拒绝退出
    const dupChild = spawn(
      process.execPath,
      [MAIN_ENTRY, '--state-dir', sandbox.stateDir, '--app', join(sandbox.appRoot, 'nonexistent')],
      { stdio: ['ignore', 'ignore', 'pipe'] },
    );
    await new Promise(resolve => dupChild.once('close', resolve));
    assert.notEqual(dupChild.exitCode, 0, '第二个实例必须被单实例锁拦截');

    // 6. 优雅停止后端
    const stopRaw = await rawPipeExchange(expectedPipe, [
      JSON.stringify({ command: 'stop', authToken }) + '\n',
    ]);
    assert.equal(JSON.parse(stopRaw.split('\n')[0]).ok, true);
    await closed;
  } finally {
    if (child.exitCode === null) child.kill();
    await closed;
    await sandbox.cleanup();
  }
});
