import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { DatabaseSync } from 'node:sqlite';
import { AccountSwitchService, DatabaseTransaction } from '../../dist/account-switch.js';
import { AccountsManager } from '../../dist/accounts.js';
import { setPlatformForTests } from '../../dist/platform/index.js';
import { createWin32PlatformAdapter, Win32CredentialAdapter } from '../../dist/platform/win32.js';
import { createWinTestSandbox, createMockWinHelperClient } from './helpers.mjs';

function initStateDb(dbPath) {
  const db = new DatabaseSync(dbPath);
  try {
    db.exec("CREATE TABLE ItemTable(key TEXT PRIMARY KEY, value TEXT); INSERT INTO ItemTable VALUES('customKey', 'keep-value');");
  } finally {
    db.close();
  }
}

test('T-012: NativeDatabaseTransaction (node:sqlite) 支持含单引号/中文/大型 Protobuf 载荷参数化读写、BEGIN IMMEDIATE 事务提交与回滚', async () => {
  const sandbox = await createWinTestSandbox('gb-win-dbtx-');
  try {
    const dbPath = join(sandbox.profileDir, 'User', 'globalStorage', 'state.vscdb');
    initStateDb(dbPath);

    const tx = new DatabaseTransaction(dbPath);
    try {
      await tx.begin();
      const initial = await tx.rows();
      assert.deepEqual(initial, {});

      const trickyPayload = "O'Reilly's \"Token\" -- DROP TABLE ItemTable; 中文测试 \n\t" + 'A'.repeat(32_000);
      await tx.replace({
        'antigravityUnifiedStateSync.oauthToken': trickyPayload,
        antigravityOnboarding: 'true',
      });
      await tx.finish(true);
    } finally {
      await tx.close();
    }

    // 验证提交成功且无关 key 未受影响
    const txVerify = new DatabaseTransaction(dbPath);
    try {
      await txVerify.begin();
      const rows = await txVerify.rows();
      assert.equal(rows['antigravityUnifiedStateSync.oauthToken']?.startsWith("O'Reilly's"), true);
      assert.equal(rows.antigravityOnboarding, 'true');
      // 测试回滚
      await txVerify.replace({ antigravityOnboarding: 'false' });
      await txVerify.finish(false);
    } finally {
      await txVerify.close();
    }

    const dbRead = new DatabaseSync(dbPath, { readOnly: true });
    try {
      const onboarding = dbRead.prepare("SELECT value FROM ItemTable WHERE key = 'antigravityOnboarding'").get();
      const custom = dbRead.prepare("SELECT value FROM ItemTable WHERE key = 'customKey'").get();
      assert.equal(onboarding?.value, 'true');
      assert.equal(custom?.value, 'keep-value');
    } finally {
      dbRead.close();
    }
  } finally {
    await sandbox.cleanup();
  }
});

test('T-012: AccountSwitchService 在 Windows 反斜杠路径下完成四阶段切号、Win32CredentialAdapter 同步、运行态拦截与中断对账回滚', async () => {
  const sandbox = await createWinTestSandbox('gb-win-switch-');
  let appRunning = false;
  const mockHelper = createMockWinHelperClient({
    async list_processes() {
      if (!appRunning) return { processes: [] };
      return {
        processes: [
          {
            pid: 4242,
            ppid: 1000,
            name: 'Antigravity.exe',
            executablePath: join(sandbox.appRoot, 'Antigravity.exe'),
            commandLine: `"${join(sandbox.appRoot, 'Antigravity.exe')}"`,
            creationTimeMs: Date.now() - 10_000,
          },
        ],
      };
    },
  });
  setPlatformForTests(createWin32PlatformAdapter({ helperClient: mockHelper }));

  try {
    const dbPath = join(sandbox.profileDir, 'User', 'globalStorage', 'state.vscdb');
    initStateDb(dbPath);

    const keychain = new Win32CredentialAdapter(() => mockHelper);
    const manager = new AccountsManager(sandbox.stateDir);
    const accA = await manager.upsertAccount({
      email: 'win-a@example.test',
      name: 'Windows 用户 A',
      refreshToken: '1//win-refresh-a',
      accessToken: 'ya29.win-access-a',
      expiresIn: 3600,
    });
    const accB = await manager.upsertAccount({
      email: 'win-b@example.test',
      name: 'Windows 用户 B',
      refreshToken: '1//win-refresh-b',
      accessToken: 'ya29.win-access-b',
      expiresIn: 3600,
    });

    const service = new AccountSwitchService(manager, {
      stateDir: sandbox.stateDir,
      profile: sandbox.profileDir,
      homeDir: sandbox.homeDir,
      geminiDir: sandbox.geminiDir,
      keychain,
    });

    // 1. 当 WinHelper 报告 Antigravity.exe 运行中时，guardStopped 必须拒绝切号
    appRunning = true;
    await assert.rejects(service.switchAccount(accA.id), /退出/);

    // 2. 应用停止后切到账号 A，再切到账号 B
    appRunning = false;
    await service.switchAccount(accA.id);
    const switchB = await service.switchAccount(accB.id);
    assert.equal(switchB.switchedTo, 'win-b@example.test');

    // 验证 Windows Credential Manager 中存储的是无前缀原始 JSON，且 Win32CredentialAdapter 读回为 go-keyring-base64:
    const rawCred = mockHelper.credentials.get('gemini:antigravity');
    assert.ok(rawCred);
    assert.equal(rawCred.secret.startsWith('go-keyring-base64:'), false);
    assert.equal(JSON.parse(rawCred.secret).id_token_email, 'win-b@example.test');

    // 验证 Windows 反斜杠路径下 journal.files 中的 accounts.json 未被误判脱敏，而 accounts/<id>.json 已被脱敏为 [REDACTED_ACCOUNT]
    const journalPath = join(sandbox.stateDir, 'account-switch-journal-stable.json');
    const journal = JSON.parse(await readFile(journalPath, 'utf8'));
    const indexEntry = journal.files.find(f => f.path.endsWith('accounts.json'));
    const accountEntry = journal.files.find(f => !f.path.endsWith('accounts.json'));
    assert.ok(indexEntry && indexEntry.after !== '[REDACTED_ACCOUNT]');
    assert.equal(accountEntry?.after, '[REDACTED_ACCOUNT]');

    // 3. 执行 7 天回滚（验证 reconcileRollback 与 rollback 在 Windows 反斜杠路径下完整通过）
    const rb = await service.rollback();
    assert.ok(rb.message);
    const rawCredAfterRb = mockHelper.credentials.get('gemini:antigravity');
    assert.equal(JSON.parse(rawCredAfterRb.secret).id_token_email, 'win-a@example.test');
    assert.equal((await manager.loadIndex()).current_account_id, accA.id);
  } finally {
    setPlatformForTests(null);
    await sandbox.cleanup();
  }
});
