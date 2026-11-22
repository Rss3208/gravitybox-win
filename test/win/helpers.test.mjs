import test from 'node:test';
import assert from 'node:assert/strict';
import { existsSync } from 'node:fs';
import { createWinTestSandbox, createMockWinHelperClient, computeExpectedWinPipeName } from './helpers.mjs';

test('T-004: Windows 跨平台测试沙箱支持中文与空格目录及 Mock WinHelper', async t => {
  const sandbox = await createWinTestSandbox('gb-t004-');
  t.after(() => sandbox.cleanup());

  assert.ok(sandbox.homeDir.includes('测试 用户'));
  assert.ok(sandbox.stateDir.includes('GravityBox 沙箱'));
  assert.ok(existsSync(sandbox.stateDir));
  assert.ok(existsSync(sandbox.appRoot));

  const pipe1 = computeExpectedWinPipeName(sandbox.stateDir);
  const pipe2 = computeExpectedWinPipeName(sandbox.stateDir.toUpperCase());
  assert.equal(pipe1, pipe2, 'Windows Named Pipe 哈希应对大小写不敏感');
  assert.match(pipe1, /^\\\\\.\\pipe\\gravitybox-[0-9a-f]{24}$/);

  const helper = createMockWinHelperClient();
  await helper.request('cred_write', {
    target: 'gemini:antigravity',
    userName: 'antigravity',
    secret: '{"token":{"refresh_token":"r1"}}',
  });
  const readBack = await helper.request('cred_read', { target: 'gemini:antigravity' });
  assert.equal(readBack.secret, '{"token":{"refresh_token":"r1"}}');
  assert.equal(helper.calls.length, 2);
});
