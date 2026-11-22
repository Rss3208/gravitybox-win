import { createRequire } from 'node:module';
import type { DatabaseSync as DatabaseSyncType } from 'node:sqlite';
import { bt } from './i18n.js';

const requireBuiltin = createRequire(import.meta.url);
let cachedDatabaseSyncCtor: typeof DatabaseSyncType | null = null;

function getDatabaseSyncCtor(): typeof DatabaseSyncType {
  if (cachedDatabaseSyncCtor) return cachedDatabaseSyncCtor;
  const origEmitWarning = process.emitWarning;
  process.emitWarning = ((warning: string | Error, ...args: any[]) => {
    const msg = typeof warning === 'string' ? warning : warning?.message ?? '';
    const type = typeof args[0] === 'string' ? args[0] : (args[0] as { type?: string } | undefined)?.type;
    if (type === 'ExperimentalWarning' || /SQLite is an experimental feature/i.test(msg)) {
      return;
    }
    return (origEmitWarning as any).call(process, warning, ...args);
  }) as typeof process.emitWarning;
  try {
    const mod = requireBuiltin('node:sqlite') as { DatabaseSync: typeof DatabaseSyncType };
    cachedDatabaseSyncCtor = mod.DatabaseSync;
    return cachedDatabaseSyncCtor;
  } finally {
    process.emitWarning = origEmitWarning;
  }
}

export const ACCOUNT_SWITCH_TOKEN_KEY = 'antigravityUnifiedStateSync.oauthToken';
export const ACCOUNT_SWITCH_USER_KEY = 'antigravityUnifiedStateSync.userStatus';
export const ACCOUNT_SWITCH_INIT_KEY = 'jetskiStateSync.agentManagerInitState';
export const ACCOUNT_SWITCH_PROJECT_KEY = 'antigravityUnifiedStateSync.enterprisePreferences';
export const ACCOUNT_SWITCH_ONBOARDING_KEY = 'antigravityOnboarding';

export const ACCOUNT_SWITCH_DB_KEYS = [
  ACCOUNT_SWITCH_TOKEN_KEY,
  ACCOUNT_SWITCH_USER_KEY,
  ACCOUNT_SWITCH_INIT_KEY,
  ACCOUNT_SWITCH_PROJECT_KEY,
  ACCOUNT_SWITCH_ONBOARDING_KEY,
] as const;

export type AccountSwitchDbRows = Record<string, string>;

/**
 * 零外部依赖 SQLite 切号事务（ADR-007 / SDD-DES-WIN-001 §4.6.1）。
 * 基于 Node 22 内置 `node:sqlite` (`DatabaseSync`)，使用 `BEGIN IMMEDIATE` 与参数化预编译 SQL，
 * 在四阶段切号提交期间持有写事务，不依赖 `/usr/bin/sqlite3` 外部子进程。
 */
export class NativeDatabaseTransaction {
  private db: DatabaseSyncType | null = null;
  private inTransaction = false;
  private closed = false;

  constructor(private readonly dbPath: string) {}

  private ensureOpen(): DatabaseSyncType {
    if (this.closed) {
      throw new Error(bt('switch.err.db_closed'));
    }
    if (!this.db) {
      try {
        const DatabaseSyncCtor = getDatabaseSyncCtor();
        this.db = new DatabaseSyncCtor(this.dbPath);
        this.db.exec('PRAGMA busy_timeout = 3000;');
      } catch (error) {
        const msg = (error as Error)?.message ?? '';
        if (/locked|busy/i.test(msg)) {
          throw new Error(bt('switch.err.db_busy'));
        }
        throw new Error(bt('switch.err.db_tx_failed'));
      }
    }
    return this.db;
  }

  async begin(): Promise<void> {
    const db = this.ensureOpen();
    try {
      db.exec('BEGIN IMMEDIATE;');
      this.inTransaction = true;
    } catch (error) {
      const msg = (error as Error)?.message ?? '';
      if (/locked|busy/i.test(msg)) {
        throw new Error(bt('switch.err.db_busy'));
      }
      throw new Error(bt('switch.err.db_tx_failed'));
    }
  }

  async rows(): Promise<AccountSwitchDbRows> {
    const db = this.ensureOpen();
    try {
      const placeholders = ACCOUNT_SWITCH_DB_KEYS.map(() => '?').join(', ');
      const stmt = db.prepare(`SELECT key, value FROM ItemTable WHERE key IN (${placeholders})`);
      const records = stmt.all(...ACCOUNT_SWITCH_DB_KEYS) as Array<{ key?: unknown; value?: unknown }>;
      const out: AccountSwitchDbRows = {};
      for (const row of records) {
        if (typeof row.key === 'string' && typeof row.value === 'string') {
          out[row.key] = row.value;
        } else if (typeof row.key === 'string' && row.value !== null && row.value !== undefined) {
          out[row.key] = String(row.value);
        }
      }
      return out;
    } catch (error) {
      const msg = (error as Error)?.message ?? '';
      if (/locked|busy/i.test(msg)) {
        throw new Error(bt('switch.err.db_busy'));
      }
      throw new Error(bt('switch.err.db_tx_failed'));
    }
  }

  async replace(rows: AccountSwitchDbRows): Promise<void> {
    const db = this.ensureOpen();
    try {
      const placeholders = ACCOUNT_SWITCH_DB_KEYS.map(() => '?').join(', ');
      const delStmt = db.prepare(`DELETE FROM ItemTable WHERE key IN (${placeholders})`);
      delStmt.run(...ACCOUNT_SWITCH_DB_KEYS);

      const insStmt = db.prepare('INSERT INTO ItemTable(key, value) VALUES(?, ?)');
      for (const [k, v] of Object.entries(rows || {})) {
        insStmt.run(k, v);
      }
    } catch (error) {
      const msg = (error as Error)?.message ?? '';
      if (/locked|busy/i.test(msg)) {
        throw new Error(bt('switch.err.db_busy'));
      }
      throw new Error(bt('switch.err.db_tx_failed'));
    }
  }

  async finish(commit: boolean): Promise<void> {
    if (this.closed || !this.db) return;
    try {
      if (this.inTransaction) {
        this.db.exec(commit ? 'COMMIT;' : 'ROLLBACK;');
        this.inTransaction = false;
      }
    } catch (error) {
      this.inTransaction = false;
      const msg = (error as Error)?.message ?? '';
      if (/locked|busy/i.test(msg)) {
        throw new Error(bt('switch.err.db_busy'));
      }
      throw new Error(bt('switch.err.db_tx_failed'));
    }
  }

  async close(): Promise<void> {
    if (this.closed) return;
    this.closed = true;
    const db = this.db;
    this.db = null;
    if (!db) return;
    if (this.inTransaction) {
      this.inTransaction = false;
      try {
        db.exec('ROLLBACK;');
      } catch {}
    }
    try {
      db.close();
    } catch {}
  }
}

export { NativeDatabaseTransaction as DatabaseTransaction };
