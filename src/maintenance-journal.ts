import { cp, lstat, mkdir, open, readdir, readFile, rename, rm } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import { atomicJson } from './files.js';
import { bt } from './i18n.js';
import { getPlatform } from './platform/index.js';

export interface JournalMove {
  from: string;
  to: string;
  identity?: string;
  strategy?: 'rename' | 'cross-volume';
}

interface MoveTransaction {
  phase: 'prepared' | 'committed' | 'rolled-back' | 'recovery-required';
  purpose: 'clear' | 'restore';
  moves: JournalMove[];
}

export type MaintenanceManifest = Record<string, any> & {
  transaction?: MoveTransaction;
};

async function identity(path: string): Promise<string | null> {
  try {
    const stat = await lstat(path);
    if (stat.isSymbolicLink()) throw Error(bt('maintenance.journal.err.symlink'));
    return `${stat.dev}:${stat.ino}`;
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === 'ENOENT') return null;
    throw error;
  }
}

async function measureTree(rootPath: string): Promise<{ files: number; bytes: number }> {
  const st = await lstat(rootPath);
  if (st.isSymbolicLink()) throw Error(bt('maintenance.journal.err.symlink'));
  if (st.isFile()) return { files: 1, bytes: st.size };
  if (!st.isDirectory()) return { files: 0, bytes: 0 };

  let files = 0;
  let bytes = 0;
  const queue = [rootPath];
  while (queue.length > 0) {
    const current = queue.pop()!;
    const entries = await readdir(current);
    for (const name of entries) {
      const child = join(current, name);
      const cst = await lstat(child);
      if (cst.isSymbolicLink()) throw Error(bt('maintenance.journal.err.symlink'));
      if (cst.isDirectory()) {
        queue.push(child);
      } else if (cst.isFile()) {
        files += 1;
        bytes += cst.size;
      }
    }
  }
  return { files, bytes };
}

async function movePathWithFallback(
  from: string,
  to: string,
  renameFn: (oldPath: string, newPath: string) => Promise<void> = rename
): Promise<{ strategy: 'rename' | 'cross-volume'; newIdentity?: string | null }> {
  try {
    await renameFn(from, to);
    return { strategy: 'rename' };
  } catch (error) {
    const code = (error as NodeJS.ErrnoException)?.code;
    if (code !== 'EXDEV') {
      throw error;
    }
    const staging = `${to}.__staging__`;
    await rm(staging, { recursive: true, force: true }).catch(() => {});
    try {
      const expectedStats = await measureTree(from);
      await cp(from, staging, { recursive: true, force: true, verbatimSymlinks: true });
      const actualStats = await measureTree(staging);
      if (actualStats.files !== expectedStats.files || actualStats.bytes !== expectedStats.bytes) {
        throw new Error('Cross-volume copy verification mismatch');
      }
      await renameFn(staging, to);
      await rm(from, { recursive: true, force: true });
      return {
        strategy: 'cross-volume',
        newIdentity: await identity(to),
      };
    } catch (copyError) {
      await rm(staging, { recursive: true, force: true }).catch(() => {});
      throw copyError;
    }
  }
}

async function persistDirectories(...paths: string[]) {
  const ignoredCodes = new Set(['EINVAL', 'ENOTSUP', 'EPERM', 'EACCES', 'EISDIR', 'EBADF', 'ENOENT']);
  for (const path of new Set(paths.map(dirname))) {
    let handle;
    try {
      handle = await open(path, 'r');
      await handle.sync();
    } catch (error) {
      if (!ignoredCodes.has((error as NodeJS.ErrnoException).code ?? '')) throw error;
    } finally {
      await handle?.close().catch(() => {});
    }
  }
}

/** Roll back only exact objects moved by this transaction; never overwrite a regenerated path. */
export async function reconcileMoves(
  path: string,
  manifest: MaintenanceManifest,
  guard: (move: JournalMove) => Promise<void>,
  renameFn: (oldPath: string, newPath: string) => Promise<void> = rename
): Promise<void> {
  const transaction = manifest.transaction;
  if (!transaction || ['committed', 'rolled-back'].includes(transaction.phase)) return;
  let complete = true;
  for (const move of [...transaction.moves].reverse()) {
    try {
      await guard(move);
      const [from, to] = await Promise.all([identity(move.from), identity(move.to)]);
      if (from === move.identity) continue;
      if (from !== null || to !== move.identity) {
        complete = false;
        continue;
      }
      await mkdir(dirname(move.from), { recursive: true });
      const res = await movePathWithFallback(move.to, move.from, renameFn);
      if (res.strategy === 'cross-volume' && res.newIdentity) {
        move.identity = res.newIdentity;
      }
      await persistDirectories(move.to, move.from);
    } catch {
      complete = false;
    }
  }
  transaction.phase = complete ? 'rolled-back' : 'recovery-required';
  await atomicJson(path, manifest);
  if (!complete) throw Error(bt('maintenance.journal.err.reconcile_blocked'));
}

export async function executeMoves(
  path: string,
  manifest: MaintenanceManifest,
  moves: JournalMove[],
  guard: (move: JournalMove) => Promise<void>,
  committedPatch: Record<string, unknown> = {},
  persist = atomicJson,
  renameFn: (oldPath: string, newPath: string) => Promise<void> = rename
): Promise<void> {
  const simulated = new Map<string, string | null>();
  for (const move of moves) {
    for (const targetPath of [move.from, move.to]) {
      if (!simulated.has(targetPath)) simulated.set(targetPath, await identity(targetPath));
    }
  }
  for (const move of moves) {
    await guard(move);
    const source = simulated.get(move.from);
    if (!source || simulated.get(move.to) !== null) throw Error(bt('maintenance.journal.err.path_changed_before'));
    move.identity = source;
    simulated.set(move.from, null);
    simulated.set(move.to, source);
  }
  manifest.transaction = { phase: 'prepared', purpose: committedPatch.restoredAt ? 'restore' : 'clear', moves };
  // Durable write-ahead plan covers crashes after rename but before the next status write.
  await persist(path, manifest);
  try {
    let anyCrossVolume = false;
    for (const move of moves) {
      await guard(move);
      if ((await identity(move.from)) !== move.identity || (await identity(move.to)) !== null) {
        throw Error(bt('maintenance.journal.err.path_changed_during'));
      }
      await mkdir(dirname(move.to), { recursive: true, mode: 0o700 });
      const res = await movePathWithFallback(move.from, move.to, renameFn);
      move.strategy = res.strategy;
      if (res.strategy === 'cross-volume' && res.newIdentity) {
        move.identity = res.newIdentity;
        anyCrossVolume = true;
      }
      await persistDirectories(move.from, move.to);
    }
    if (anyCrossVolume) {
      // Ensure moved entries reflect final cross-volume identities
      await persist(path, manifest);
    }
    await persist(path, { ...manifest, ...committedPatch, transaction: { ...manifest.transaction, phase: 'committed' } });
  } catch (error) {
    try {
      await reconcileMoves(path, manifest, guard, renameFn);
    } catch {
      throw Error(bt('maintenance.journal.err.incomplete_recovery'));
    }
    throw error;
  }
}

export async function readMaintenanceManifest(path: string): Promise<MaintenanceManifest> {
  try {
    await getPlatform().assertOwnedPath(path, 'file');
  } catch (error) {
    if ((error as NodeJS.ErrnoException)?.code === 'ENOENT') throw error;
    throw Error(bt('maintenance.journal.err.manifest_identity'));
  }
  const manifest = JSON.parse(await readFile(path, 'utf8')) as MaintenanceManifest;
  if (
    manifest.transaction &&
    (!Array.isArray(manifest.transaction.moves) ||
      !['prepared', 'committed', 'rolled-back', 'recovery-required'].includes(manifest.transaction.phase) ||
      manifest.transaction.moves.some(
        move => typeof move.from !== 'string' || typeof move.to !== 'string' || !/^\d+:\d+$/.test(move.identity ?? '')
      ))
  ) {
    throw Error(bt('maintenance.journal.err.manifest_invalid'));
  }
  return manifest;
}
