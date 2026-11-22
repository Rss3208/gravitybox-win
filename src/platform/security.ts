import { lstatSync, chmodSync } from 'node:fs';
import { lstat, chmod } from 'node:fs/promises';
import { posix as posixPath, win32 as win32Path } from 'node:path';
import { bt } from '../i18n.js';
import type { PlatformId, WinHelperClientLike } from './types.js';

export function isStrictSubpathForPlatform(
  platformId: PlatformId,
  parentDir: string,
  candidatePath: string
): boolean {
  if (!parentDir || !candidatePath) return false;
  if (platformId === 'win32') {
    const p = win32Path;
    const normParent = p.resolve(parentDir).toLowerCase();
    const normCandidate = p.resolve(candidatePath).toLowerCase();
    if (normParent === normCandidate) return false;
    const rel = p.relative(normParent, normCandidate);
    return Boolean(rel) && !rel.startsWith('..') && !p.isAbsolute(rel);
  }
  const p = posixPath;
  const normParent = p.resolve(parentDir);
  const normCandidate = p.resolve(candidatePath);
  if (normParent === normCandidate) return false;
  const rel = p.relative(normParent, normCandidate);
  return Boolean(rel) && !rel.startsWith('..') && !p.isAbsolute(rel);
}

export function sanitizeHostPaths(message: string, token = ''): string {
  let clean = String(message ?? '');
  if (token) {
    clean = clean.replaceAll(token, '<redacted>');
  }
  return clean
    .replace(/(?:Bearer\s+|token[=:]\s*)\S+/gi, '<redacted>')
    // Windows 路径: C:\Users\demo 或 C:/Users/demo 或其他盘符绝对路径
    .replace(/[A-Za-z]:(?:\\+|\/+)(?:Users|Program Files(?: \(x86\))?|ProgramData|Windows|Temp)(?:\\+|\/+)[^\s"'`<>|)+,;]+/gi, '<path>')
    .replace(/[A-Za-z]:(?:\\+|\/+)[^\s"'`<>|)+,;]+/g, '<path>')
    // POSIX 路径: /Users/demo /home/demo /var/... /tmp/... /private/... /Applications/...
    .replace(/\/(?:Users|var|tmp|private|Applications|home)\/\S+/g, '<path>');
}

export function assertOwnedPathSyncForPlatform(
  platformId: PlatformId,
  targetPath: string,
  expectedType: 'file' | 'dir' | 'any' = 'any',
  errorMessage?: string
): void {
  const info = lstatSync(targetPath);
  const fail = () => {
    throw new Error(errorMessage ?? bt('files.unsafe_target'));
  };
  if (info.isSymbolicLink()) fail();
  if (expectedType === 'file' && !info.isFile()) fail();
  if (expectedType === 'dir' && !info.isDirectory()) fail();
  if (platformId === 'darwin') {
    const currentUid = typeof process.getuid === 'function' ? process.getuid() : undefined;
    if (currentUid !== undefined && info.uid !== currentUid) fail();
  }
}

export async function assertOwnedPathForPlatform(
  platformId: PlatformId,
  targetPath: string,
  expectedType: 'file' | 'dir' | 'any' = 'any',
  helperClient?: WinHelperClientLike | null,
  errorMessage?: string
): Promise<void> {
  const info = await lstat(targetPath);
  const fail = () => {
    throw new Error(errorMessage ?? bt('files.unsafe_target'));
  };
  if (info.isSymbolicLink()) fail();
  if (expectedType === 'file' && !info.isFile()) fail();
  if (expectedType === 'dir' && !info.isDirectory()) fail();
  if (platformId === 'darwin') {
    const currentUid = typeof process.getuid === 'function' ? process.getuid() : undefined;
    if (currentUid !== undefined && info.uid !== currentUid) fail();
    return;
  }
  if (helperClient) {
    const verify = await helperClient.request<{
      ownedByCurrentUser: boolean;
      isReparsePoint: boolean;
      exists: boolean;
    }>('acl_verify_owner', { path: targetPath });
    if (!verify?.exists || verify.isReparsePoint || !verify.ownedByCurrentUser) {
      fail();
    }
  }
}

export async function hardenDirectoryPermissionsForPlatform(
  platformId: PlatformId,
  dirPath: string,
  helperClient?: WinHelperClientLike | null
): Promise<void> {
  if (platformId === 'darwin') {
    await chmod(dirPath, 0o700).catch(() => {});
    return;
  }
  if (helperClient) {
    await helperClient.request('acl_harden', { path: dirPath, isDir: true }).catch(() => {});
  }
}

export async function hardenFilePermissionsForPlatform(
  platformId: PlatformId,
  filePath: string,
  helperClient?: WinHelperClientLike | null
): Promise<void> {
  if (platformId === 'darwin') {
    try {
      chmodSync(filePath, 0o600);
    } catch {}
    return;
  }
  if (helperClient) {
    await helperClient.request('acl_harden', { path: filePath, isDir: false }).catch(() => {});
  }
}
