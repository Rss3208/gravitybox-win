import { open, lstat, readFile, unlink, link, rename } from 'node:fs/promises';
import { readFileSync, unlinkSync } from 'node:fs';
import { randomUUID } from 'node:crypto';
import { AsyncLocalStorage } from 'node:async_hooks';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { setTimeout as delay } from 'node:timers/promises';
import { bt } from './i18n.js';
import { getPlatform } from './platform/index.js';

const context = new AsyncLocalStorage<Map<string, string>>();
const active = new Map<string, string>();
export type ProcessLease = { release(): Promise<void>; releaseSync(): void };

async function tryCreateExclusiveLockFile(path: string, payload: string, nonce: string): Promise<void> {
  if (getPlatform().id === 'win32') {
    const file = await open(path, 'wx', 0o600);
    try {
      await file.writeFile(payload);
      await file.sync();
    } finally {
      await file.close().catch(() => {});
    }
    return;
  }
  const temporary = path + '.' + nonce + '.tmp';
  const file = await open(temporary, 'wx', 0o600);
  try {
    await file.writeFile(payload);
    await file.sync();
    await file.close();
    await link(temporary, path);
  } finally {
    await file.close().catch(() => {});
    await unlink(temporary).catch(() => {});
  }
}

function sameLockIdentity(a: { ino: number; dev: number; ctimeMs: number; size: number }, b: { ino: number; dev: number; ctimeMs: number; size: number } | null): boolean {
  if (!b) return false;
  if (a.ino !== 0 && b.ino !== 0) {
    return a.ino === b.ino && a.dev === b.dev;
  }
  return a.ctimeMs === b.ctimeMs && a.size === b.size;
}

/** Same-user exclusive lease. Only a provably dead owner may be reclaimed. */
export async function acquireProcessLease(path: string, timeoutMs = 15000, busyMessage = bt('lock.busy')): Promise<ProcessLease> {
  const deadline = Date.now() + timeoutMs, nonce = randomUUID();
  const platform = getPlatform();
  while (true) {
    try {
      const createdAt = Date.now();
      const payload = JSON.stringify({ pid: process.pid, nonce, createdAt });
      await tryCreateExclusiveLockFile(path, payload, nonce);
      const owns = (text: string) => { try { return JSON.parse(text).nonce === nonce; } catch { return false; } };
      return {
        async release() { try { if (owns(await readFile(path, 'utf8'))) await unlink(path); } catch {} },
        releaseSync() { try { if (owns(readFileSync(path, 'utf8'))) unlinkSync(path); } catch {} },
      };
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code !== 'EEXIST') throw error;
      const info = await lstat(path).catch(() => null);
      if (!info) continue;
      try {
        await platform.assertOwnedPath(path, 'file');
      } catch (err) {
        if ((err as NodeJS.ErrnoException)?.code === 'ENOENT') continue;
        throw new Error(bt('lock.identity_mismatch'));
      }
      const text = await readFile(path, 'utf8').catch(() => '');
      let pid = 0;
      let createdAt: number | undefined;
      try {
        const parsed = JSON.parse(text);
        pid = parsed.pid;
        if (typeof parsed.createdAt === 'number' && Number.isFinite(parsed.createdAt)) {
          createdAt = parsed.createdAt;
        }
      } catch {
        const legacyPid = Number(text.trim());
        if (Number.isSafeInteger(legacyPid) && legacyPid > 0) pid = legacyPid;
      }
      if ((!Number.isSafeInteger(pid) || pid <= 0) && Date.now() - info.mtimeMs > 15000) {
        // Legacy half-created locks cannot have an owner after their descriptor is closed.
        let closed = false;
        if (platform.id === 'win32') {
          closed = true;
        } else {
          try { await promisify(execFile)('/usr/sbin/lsof', ['-t', '--', path], { timeout: 1500 }); }
          catch (error) { closed = (error as any).code === 1 && !(error as any).stdout && !(error as any).stderr; }
        }
        const check = await lstat(path).catch(() => null);
        if (closed && sameLockIdentity(info, check) && await readFile(path, 'utf8').catch(() => null) === text) {
          await rename(path, path + '.orphan-' + Date.now() + '-' + nonce).catch(() => {});
          continue;
        }
      }
      if (Number.isSafeInteger(pid) && pid > 0) {
        const alive = await platform.isProcessAlive(pid, createdAt);
        if (!alive) {
          const reclaimPath = path + '.reclaim';
          let reclaim;
          try {
            reclaim = await open(reclaimPath, 'wx', 0o600);
            await reclaim.writeFile(JSON.stringify({ pid: process.pid, nonce, createdAt: Date.now() }));
            await reclaim.sync();
          } catch (failure) {
            if ((failure as NodeJS.ErrnoException).code !== 'EEXIST') throw failure;
            // Acquire/release the recovery lease through the same stale-owner protocol.
            const cleanup = await acquireProcessLease(reclaimPath, Math.max(1, deadline - Date.now()));
            await cleanup.release();
            continue;
          }
          if (reclaim) {
            try {
              const check = await lstat(path).catch(() => null);
              if (sameLockIdentity(info, check) && await readFile(path, 'utf8').catch(() => '') === text) {
                await unlink(path).catch(() => {});
              }
            } finally {
              await reclaim.close().catch(() => {});
              await unlink(reclaimPath).catch(() => {});
            }
            continue;
          }
        }
      }
      if (Date.now() >= deadline) throw new Error(busyMessage);
      await delay(30);
    }
  }
}

export async function withProcessLease<T>(
  path: string,
  action: () => Promise<T>,
  options?: { timeoutMs?: number; busyMessage?: string }
): Promise<T> {
  const inherited = context.getStore()?.get(path);
  if (inherited && active.get(path) === inherited) return action();
  const lease = await acquireProcessLease(path, options?.timeoutMs, options?.busyMessage), id = randomUUID();
  active.set(path, id);
  const next = new Map(context.getStore()); next.set(path, id);
  try { return await context.run(next, action); }
  finally { active.delete(path); await lease.release(); }
}
