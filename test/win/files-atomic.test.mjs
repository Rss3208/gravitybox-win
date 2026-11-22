import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { createWinTestSandbox, createMockWinHelperClient } from './helpers.mjs';
import {
  atomicText,
  atomicJson,
  atomicRenameSyncWithRetry,
  commitFileSet,
  recoverFileSet,
  commitAccountAndIndex,
  recoverAccountCommit,
} from '../../dist/files.js';
import { createWin32PlatformAdapter, setPlatformForTests } from '../../dist/platform/index.js';

test('T-006: Windows 含中文/空格路径下 atomicText 与 atomicJson 可多次覆盖写入而不误判 uid', async t => {
  const sandbox = await createWinTestSandbox('gb-t006-atomic-');
  t.after(() => sandbox.cleanup());

  const target = join(sandbox.stateDir, '配置 文件.json');
  await atomicJson(target, { step: 1, lang: 'zh-Hans' });
  assert.deepEqual(JSON.parse(await readFile(target, 'utf8')), { step: 1, lang: 'zh-Hans' });

  // 第二次覆盖写入已存在文件（验证已消除 info.uid !== process.getuid?.() 在 Windows 下的 0 !== undefined 缺陷）
  await atomicJson(target, { step: 2, lang: 'en' });
  assert.deepEqual(JSON.parse(await readFile(target, 'utf8')), { step: 2, lang: 'en' });
});

test('T-006: atomicRenameSyncWithRetry 对 Windows 瞬态 EPERM/EBUSY/EACCES 自动退避重试并在超限后抛出', () => {
  let attempts = 0;
  atomicRenameSyncWithRetry('a.tmp', 'a.json', () => {
    attempts++;
    if (attempts <= 3) {
      const err = new Error('transient antivirus lock');
      err.code = attempts === 1 ? 'EPERM' : attempts === 2 ? 'EBUSY' : 'EACCES';
      throw err;
    }
  }, [1, 1, 1, 1, 1]);
  assert.equal(attempts, 4);

  // 非瞬态错误（如 ENOENT）不重试立即抛出
  let enoentAttempts = 0;
  assert.throws(() => {
    atomicRenameSyncWithRetry('a.tmp', 'a.json', () => {
      enoentAttempts++;
      const err = new Error('missing');
      err.code = 'ENOENT';
      throw err;
    }, [1, 1, 1]);
  }, /missing/);
  assert.equal(enoentAttempts, 1);
});

test('T-006: commitFileSet、commitAccountAndIndex 与 recoverAccountCommit 支持 Windows 路径与崩溃恢复', async t => {
  const sandbox = await createWinTestSandbox('gb-t006-commit-');
  t.after(() => sandbox.cleanup());

  const accountsDir = join(sandbox.stateDir, 'accounts');
  const indexPath = join(sandbox.stateDir, 'accounts.json');
  const accountPath = join(accountsDir, 'acc-1.json');

  await commitAccountAndIndex(accountPath, { id: 'acc-1', email: 'a@example.test' }, indexPath, {
    current_account_id: 'acc-1',
    accounts: [{ id: 'acc-1', email: 'a@example.test' }],
  });

  // 覆盖更新已存在的账号与索引
  await commitAccountAndIndex(accountPath, { id: 'acc-1', email: 'b@example.test' }, indexPath, {
    current_account_id: 'acc-1',
    accounts: [{ id: 'acc-1', email: 'b@example.test' }],
  });
  assert.equal(JSON.parse(await readFile(accountPath, 'utf8')).email, 'b@example.test');

  // 模拟中断留下的 .commit.json 写前日志，验证 recoverAccountCommit 前滚完成并清理日志
  const journalPath = `${indexPath}.commit.json`;
  const beforeAccount = await readFile(accountPath, 'utf8');
  const beforeIndex = await readFile(indexPath, 'utf8');
  const afterAccount = JSON.stringify({ id: 'acc-1', email: 'c@example.test' }, null, 2) + '\n';
  const afterIndex = JSON.stringify({ current_account_id: 'acc-1', accounts: [{ id: 'acc-1', email: 'c@example.test' }] }, null, 2) + '\n';
  await writeFile(
    journalPath,
    JSON.stringify({
      accountPath,
      indexPath,
      beforeAccount,
      beforeIndex,
      afterAccount,
      afterIndex,
    })
  );
  await recoverAccountCommit(indexPath);
  assert.equal(JSON.parse(await readFile(accountPath, 'utf8')).email, 'c@example.test');

  // 验证非法路径逃逸被 recoverAccountCommit 拒绝
  const evilAccountPath = join(sandbox.stateDir, 'accounts-evil', 'acc-1.json');
  await writeFile(
    journalPath,
    JSON.stringify({
      accountPath: evilAccountPath,
      indexPath,
      beforeAccount,
      beforeIndex,
      afterAccount,
      afterIndex,
    })
  );
  await assert.rejects(() => recoverAccountCommit(indexPath));
  await writeFile(journalPath, '');

  // 验证 commitFileSet 可在 Windows 下更新并删除已存在文件
  const deleteJournal = join(sandbox.stateDir, 'delete.journal.json');
  await commitFileSet(deleteJournal, [
    { path: accountPath, after: null },
    { path: indexPath, after: JSON.stringify({ current_account_id: null, accounts: [] }) + '\n' },
  ]);
  assert.deepEqual(JSON.parse(await readFile(indexPath, 'utf8')), { current_account_id: null, accounts: [] });
});

test('T-006: 当 WinHelper 检测到非当前用户或 ReparsePoint 时拒绝 atomicText 覆盖', async t => {
  const sandbox = await createWinTestSandbox('gb-t006-acl-');
  t.after(() => {
    setPlatformForTests(null);
    return sandbox.cleanup();
  });

  const target = join(sandbox.stateDir, 'protected.json');
  await writeFile(target, '{"ok":true}\n');

  const rejectHelper = createMockWinHelperClient({
    async acl_verify_owner() {
      return { ownedByCurrentUser: false, isReparsePoint: false, exists: true };
    },
  });
  setPlatformForTests(createWin32PlatformAdapter({ helperClient: rejectHelper }));

  await assert.rejects(() => atomicText(target, '{"ok":false}\n'));
});
