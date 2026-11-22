import { readFile, mkdir } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { join, resolve } from 'node:path';
import { atomicJson } from '../files.js';
import { withProcessLease } from '../process-lock.js';
import { isInsidersPathForPlatform } from './paths.js';
import { sanitizeHostPaths } from './security.js';
import type { ShortcutEntry, ShortcutApplyResult, ShortcutRestoreResult, WinHelperClientLike } from './types.js';

export const SHORTCUT_BACKUPS_FILE = 'shortcut-backups.json';
export const SHORTCUT_LOCK_FILE = 'shortcuts.lock';
export const DEBUG_PORT_ARG = '--remote-debugging-port=0';
export const DEBUG_PORT_RE = /(?:^|\s)--remote-debugging-port(?:=\d+|\s+\d+)(?=\s|$)/i;

export interface ShortcutBackupEntry {
  path: string;
  location: 'desktop' | 'start-menu' | 'taskbar' | 'custom';
  targetPath: string;
  originalArguments: string;
  appliedArguments: string;
  originalSha256: string;
  appliedAt: string;
}

export interface ShortcutBackupDocument {
  schema: 1;
  updatedAt: string;
  entries: ShortcutBackupEntry[];
}

function normalizeWinPath(p: string): string {
  return String(p || '').trim().replace(/\//g, '\\').replace(/\\+$/, '').toLowerCase();
}

export function isOfficialShortcutTarget(
  shortcutPath: string,
  targetPath: string,
  officialExePath?: string,
): boolean {
  if (!shortcutPath || !targetPath) return false;
  if (isInsidersPathForPlatform('win32', shortcutPath) || isInsidersPathForPlatform('win32', targetPath)) {
    return false;
  }
  if (officialExePath && officialExePath.trim()) {
    return normalizeWinPath(targetPath) === normalizeWinPath(officialExePath);
  }
  return /(?:^|\\)antigravity\.exe$/i.test(targetPath.replace(/\//g, '\\'));
}

export function computeRestoredArguments(
  originalArguments: string,
  appliedArguments?: string,
  currentArguments?: string,
): string {
  const orig = String(originalArguments ?? '').trim();
  const applied = String(appliedArguments ?? '').trim();
  if (currentArguments === undefined || currentArguments === null) {
    return orig;
  }
  const current = String(currentArguments).trim();
  if (!current || (applied && current === applied)) {
    return orig;
  }
  // 用户在补参后手动新增或调整了其他参数：剥离 --remote-debugging-port=0，保留其余参数
  const stripped = current
    .replace(/(?:^|\s+)--remote-debugging-port(?:=0|\s+0)(?=\s|$)/gi, ' ')
    .replace(/\s+/g, ' ')
    .trim();
  return stripped;
}

async function readShortcutBackups(stateDir?: string): Promise<ShortcutBackupDocument> {
  if (!stateDir) {
    return { schema: 1, updatedAt: new Date().toISOString(), entries: [] };
  }
  try {
    const raw = JSON.parse(await readFile(join(stateDir, SHORTCUT_BACKUPS_FILE), 'utf8'));
    if (raw && Array.isArray(raw.entries)) {
      return {
        schema: 1,
        updatedAt: typeof raw.updatedAt === 'string' ? raw.updatedAt : new Date().toISOString(),
        entries: raw.entries,
      };
    }
  } catch {}
  return { schema: 1, updatedAt: new Date().toISOString(), entries: [] };
}

async function computeShortcutDigest(lnkPath: string, fallbackArgs: string): Promise<string> {
  try {
    const bytes = await readFile(lnkPath);
    return createHash('sha256').update(bytes).digest('hex');
  } catch {
    return createHash('sha256').update(`${lnkPath}\0${fallbackArgs}`, 'utf8').digest('hex');
  }
}

export async function scanShortcuts(options: {
  helper: WinHelperClientLike | null | undefined;
  officialExePath?: string;
  stateDir?: string;
}): Promise<ShortcutEntry[]> {
  const { helper, officialExePath = '', stateDir } = options;
  if (!helper) return [];
  const res = await helper.request<{
    shortcuts?: Array<Omit<ShortcutEntry, 'hasDebugPort' | 'hasBackup'> & { hasDebugPort?: boolean; hasBackup?: boolean }>;
  }>('shortcut_scan', { officialExePath }).catch(() => ({ shortcuts: [] }));

  const backupDoc = await readShortcutBackups(stateDir);
  const backupPaths = new Set(
    backupDoc.entries.map(e => normalizeWinPath(e?.path)).filter(Boolean),
  );

  const rawList = Array.isArray(res?.shortcuts) ? res.shortcuts : [];
  const out: ShortcutEntry[] = [];
  for (const item of rawList) {
    const lnkPath = String(item?.path || '');
    const targetPath = String(item?.targetPath || officialExePath || '');
    if (!isOfficialShortcutTarget(lnkPath, targetPath, officialExePath)) {
      continue;
    }
    const args = String(item?.arguments ?? '');
    out.push({
      path: lnkPath,
      location: item.location ?? 'custom',
      name: item.name ?? 'Antigravity',
      targetPath,
      arguments: args,
      ...(item.workingDirectory ? { workingDirectory: item.workingDirectory } : {}),
      hasDebugPort: typeof item.hasDebugPort === 'boolean' ? item.hasDebugPort : DEBUG_PORT_RE.test(args),
      hasBackup: typeof item.hasBackup === 'boolean' ? item.hasBackup : backupPaths.has(normalizeWinPath(lnkPath)),
    });
  }
  return out;
}

export async function applyShortcutDebugPort(options: {
  helper: WinHelperClientLike | null | undefined;
  stateDir: string;
  officialExePath?: string;
  paths?: string[];
}): Promise<ShortcutApplyResult> {
  const { helper, stateDir, officialExePath = '', paths } = options;
  if (!helper) return { updated: [], skipped: [], failed: [] };
  await mkdir(stateDir, { recursive: true });

  return withProcessLease(join(stateDir, SHORTCUT_LOCK_FILE), async () => {
    const scanned = await scanShortcuts({ helper, officialExePath, stateDir });
    const scannedByNorm = new Map<string, ShortcutEntry>();
    for (const item of scanned) {
      scannedByNorm.set(normalizeWinPath(resolve(item.path)), item);
      scannedByNorm.set(normalizeWinPath(item.path), item);
    }

    const updated: string[] = [];
    const skipped: string[] = [];
    const failed: Array<{ path: string; error: string }> = [];
    const targets: ShortcutEntry[] = [];

    if (Array.isArray(paths) && paths.length > 0) {
      for (const reqPath of paths) {
        const norm = normalizeWinPath(resolve(reqPath));
        const direct = scannedByNorm.get(norm) ?? scannedByNorm.get(normalizeWinPath(reqPath));
        if (direct) {
          targets.push(direct);
        } else {
          failed.push({
            path: reqPath,
            error: 'Rejected non-official or Insiders shortcut target',
          });
        }
      }
    } else {
      targets.push(...scanned);
    }

    const backupFile = join(stateDir, SHORTCUT_BACKUPS_FILE);
    const backupDoc = await readShortcutBackups(stateDir);

    for (const item of targets) {
      if (DEBUG_PORT_RE.test(item.arguments || '')) {
        skipped.push(item.path);
        continue;
      }
      const origArgs = String(item.arguments || '');
      const nextArgs = origArgs.trim()
        ? `${origArgs.trim()} ${DEBUG_PORT_ARG}`
        : DEBUG_PORT_ARG;

      const normPath = normalizeWinPath(item.path);
      const existingIdx = backupDoc.entries.findIndex(e => normalizeWinPath(e.path) === normPath);
      const originalSha256 = await computeShortcutDigest(item.path, origArgs);

      if (existingIdx < 0) {
        backupDoc.entries.push({
          path: item.path,
          location: item.location,
          targetPath: item.targetPath,
          originalArguments: origArgs,
          appliedArguments: nextArgs,
          originalSha256,
          appliedAt: new Date().toISOString(),
        });
      }

      try {
        backupDoc.updatedAt = new Date().toISOString();
        await atomicJson(backupFile, backupDoc);
        const res = await helper.request<{ ok: boolean }>('shortcut_set_args', {
          path: item.path,
          arguments: nextArgs,
        });
        if (res?.ok) {
          updated.push(item.path);
        } else {
          failed.push({ path: item.path, error: 'shortcut_set_args failed' });
        }
      } catch (err) {
        failed.push({
          path: item.path,
          error: sanitizeHostPaths((err as Error)?.message ?? String(err)),
        });
      }
    }

    return { updated, skipped, failed };
  });
}

export async function restoreShortcutDebugPort(options: {
  helper: WinHelperClientLike | null | undefined;
  stateDir: string;
  paths?: string[];
}): Promise<ShortcutRestoreResult> {
  const { helper, stateDir, paths } = options;
  if (!helper) return { restored: [], skipped: [], failed: [] };
  await mkdir(stateDir, { recursive: true });

  return withProcessLease(join(stateDir, SHORTCUT_LOCK_FILE), async () => {
    const backupFile = join(stateDir, SHORTCUT_BACKUPS_FILE);
    const backupDoc = await readShortcutBackups(stateDir);

    const filterSet = Array.isArray(paths) && paths.length > 0
      ? new Set(paths.flatMap(p => [normalizeWinPath(p), normalizeWinPath(resolve(p))]))
      : null;

    const entriesToRestore = filterSet
      ? backupDoc.entries.filter(e =>
          filterSet.has(normalizeWinPath(e.path)) || filterSet.has(normalizeWinPath(resolve(e.path))),
        )
      : [...backupDoc.entries];

    const restored: string[] = [];
    const skipped: string[] = [];
    const failed: Array<{ path: string; error: string }> = [];
    const restoredNorm = new Set<string>();

    if (filterSet) {
      const matchedReq = new Set(entriesToRestore.map(e => normalizeWinPath(e.path)));
      for (const reqPath of paths!) {
        if (!matchedReq.has(normalizeWinPath(reqPath)) && !matchedReq.has(normalizeWinPath(resolve(reqPath)))) {
          skipped.push(reqPath);
        }
      }
    }

    for (const entry of entriesToRestore) {
      const lnkPath = String(entry.path || '');
      if (!lnkPath) continue;
      let currentArguments: string | undefined;
      try {
        const inspected = await helper.request<{ arguments?: string }>('shortcut_inspect', { path: lnkPath });
        if (typeof inspected?.arguments === 'string') {
          currentArguments = inspected.arguments;
        }
      } catch {}

      const targetArguments = computeRestoredArguments(
        entry.originalArguments,
        entry.appliedArguments,
        currentArguments,
      );

      try {
        const res = await helper.request<{ ok: boolean }>('shortcut_set_args', {
          path: lnkPath,
          arguments: targetArguments,
        });
        if (res?.ok) {
          restored.push(lnkPath);
          restoredNorm.add(normalizeWinPath(lnkPath));
        } else {
          failed.push({ path: lnkPath, error: 'shortcut_set_args failed' });
        }
      } catch (err) {
        failed.push({
          path: lnkPath,
          error: sanitizeHostPaths((err as Error)?.message ?? String(err)),
        });
      }
    }

    if (restoredNorm.size > 0) {
      backupDoc.entries = backupDoc.entries.filter(e => !restoredNorm.has(normalizeWinPath(e.path)));
      backupDoc.updatedAt = new Date().toISOString();
      await atomicJson(backupFile, backupDoc);
    }

    return { restored, skipped, failed };
  });
}
