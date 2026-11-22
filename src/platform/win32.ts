import { createHash, randomBytes } from 'node:crypto';
import { existsSync, readFileSync } from 'node:fs';
import { mkdir, readFile, unlink, writeFile } from 'node:fs/promises';
import * as net from 'node:net';
import { homedir } from 'node:os';
import { join, resolve } from 'node:path';
import { openAsarArchive, openAsarSync } from '../asar.js';
import type { KeychainAdapter } from '../account-keychain.js';
import { bt } from '../i18n.js';
import {
  detectInstalledAppPathForPlatform,
  isInsidersPathForPlatform,
  resolveAppLayoutForPlatform,
  resolvePlatformPaths,
  WIN_CRED_LEGACY_TARGETS,
  WIN_CRED_TARGET_NAME,
  WIN_OFFICIAL_COMPANY_RE,
  WIN_OFFICIAL_SIGNER_SUBJECT_RE,
} from './paths.js';
import {
  assertOwnedPathForPlatform,
  assertOwnedPathSyncForPlatform,
  hardenDirectoryPermissionsForPlatform,
  hardenFilePermissionsForPlatform,
  isStrictSubpathForPlatform,
  sanitizeHostPaths,
} from './security.js';
import type {
  AppBundleLayout,
  PlatformAdapter,
  PlatformPaths,
  ProcessEntry,
  ShortcutApplyResult,
  ShortcutEntry,
  ShortcutRestoreResult,
  TcpListenerEntry,
  VerifiedBundleInfo,
  WinHelperClientLike,
} from './types.js';

export class Win32CredentialAdapter implements KeychainAdapter {
  constructor(private getHelper: () => WinHelperClientLike | null) {}

  private targetName(service: string, account: string): string {
    return `${service}:${account}`;
  }

  async readPassword(service: string, account: string): Promise<string | null> {
    const helper = this.getHelper();
    if (!helper) return null;
    const primaryTarget = this.targetName(service, account);
    const targets = primaryTarget === WIN_CRED_TARGET_NAME
      ? [primaryTarget, ...WIN_CRED_LEGACY_TARGETS]
      : [primaryTarget];

    for (const target of targets) {
      const res = await helper.request<{ secret: string | null }>('cred_read', { target }).catch(() => null);
      if (res && typeof res.secret === 'string' && res.secret.length > 0) {
        if (res.secret.startsWith('go-keyring-base64:')) {
          return res.secret;
        }
        // CHK-04: Windows go-keyring (wincred) stores raw UTF-8 JSON bytes in CredentialBlob.
        // Normalize to go-keyring-base64:<b64> so upper layers and digest comparisons remain 100% identical.
        return `go-keyring-base64:${Buffer.from(res.secret, 'utf8').toString('base64')}`;
      }
    }
    return null;
  }

  async writePassword(service: string, account: string, password: string): Promise<void> {
    const helper = this.getHelper();
    if (!helper) throw new Error(bt('keychain.err.write'));
    const target = this.targetName(service, account);
    let rawSecret = password;
    if (password.startsWith('go-keyring-base64:')) {
      rawSecret = Buffer.from(password.slice('go-keyring-base64:'.length), 'base64').toString('utf8');
    }
    const byteLen = Buffer.byteLength(rawSecret, 'utf8');
    if (byteLen > 2560) {
      throw new Error(bt('keychain.err.write'));
    }
    const res = await helper.request<{ ok: boolean }>('cred_write', {
      target,
      userName: account,
      secret: rawSecret,
    });
    if (!res?.ok) throw new Error(bt('keychain.err.write'));
  }

  async deletePassword(service: string, account: string): Promise<void> {
    const helper = this.getHelper();
    if (!helper) return;
    const target = this.targetName(service, account);
    await helper.request('cred_delete', { target });
  }
}

export interface Win32PlatformOptions {
  helperClient?: WinHelperClientLike | null;
  helperFactory?: () => WinHelperClientLike | null;
}

export function computeWinNamedPipeEndpoint(stateDir: string): string {
  const normalized = resolve(stateDir).toLowerCase();
  const hash = createHash('sha256').update(normalized).digest('hex').slice(0, 24);
  return `\\\\.\\pipe\\gravitybox-${hash}`;
}

export function createWin32PlatformAdapter(options: Win32PlatformOptions = {}): PlatformAdapter {
  let cachedHelper: WinHelperClientLike | null | undefined = options.helperClient;
  const getHelper = (): WinHelperClientLike | null => {
    if (cachedHelper !== undefined) return cachedHelper;
    if (options.helperFactory) {
      cachedHelper = options.helperFactory();
      return cachedHelper;
    }
    return null;
  };

  return {
    id: 'win32',
    getDefaultPaths(homeDir = homedir()): PlatformPaths {
      return resolvePlatformPaths('win32', homeDir);
    },
    resolveAppLayout(appPath: string): AppBundleLayout {
      return resolveAppLayoutForPlatform('win32', appPath);
    },
    detectInstalledAppPath(overrideDir?: string, homeDir = homedir()): string | null {
      return detectInstalledAppPathForPlatform('win32', overrideDir, homeDir);
    },
    isInsidersPath(appOrProfilePath: string): boolean {
      return isInsidersPathForPlatform('win32', appOrProfilePath);
    },
    getIpcEndpoint(stateDir: string): string {
      return computeWinNamedPipeEndpoint(stateDir);
    },
    async ensureIpcAuthToken(stateDir: string): Promise<string | null> {
      await mkdir(stateDir, { recursive: true });
      const tokenPath = join(stateDir, 'ipc.token');
      const token = randomBytes(32).toString('hex');
      await writeFile(tokenPath, `${token}\n`, 'utf8');
      await hardenFilePermissionsForPlatform('win32', tokenPath, getHelper());
      return token;
    },
    async readIpcAuthToken(stateDir: string): Promise<string | null> {
      try {
        const raw = await readFile(join(stateDir, 'ipc.token'), 'utf8');
        return raw.trim() || null;
      } catch {
        return null;
      }
    },
    async createIpcServer(
      endpoint: string,
      onConnection: (socket: net.Socket) => void
    ): Promise<net.Server> {
      const server = net.createServer(onConnection);
      await new Promise<void>((resolveListen, rejectListen) => {
        server.once('error', rejectListen);
        server.listen(endpoint, () => resolveListen());
      });
      return server;
    },
    async verifyIpcEndpoint(_endpoint: string): Promise<void> {
      // Named Pipes live in \\.\pipe\ kernel namespace, not on disk.
    },
    async cleanupIpcEndpoint(endpoint: string): Promise<void> {
      // If endpoint was not a named pipe in a synthetic test, clean up file fallback.
      if (!endpoint.startsWith('\\\\.\\pipe\\')) {
        await unlink(endpoint).catch(() => {});
      }
    },
    async assertOwnedPath(targetPath: string, expectedType: 'file' | 'dir' | 'any'): Promise<void> {
      await assertOwnedPathForPlatform('win32', targetPath, expectedType, getHelper());
    },
    assertOwnedPathSync(targetPath: string, expectedType: 'file' | 'dir' | 'any'): void {
      assertOwnedPathSyncForPlatform('win32', targetPath, expectedType);
    },
    async hardenDirectoryPermissions(dirPath: string): Promise<void> {
      await hardenDirectoryPermissionsForPlatform('win32', dirPath, getHelper());
    },
    async hardenFilePermissions(filePath: string): Promise<void> {
      await hardenFilePermissionsForPlatform('win32', filePath, getHelper());
    },
    isStrictSubpath(parentDir: string, candidatePath: string): boolean {
      return isStrictSubpathForPlatform('win32', parentDir, candidatePath);
    },
    sanitizeHostPaths(message: string, token = ''): string {
      return sanitizeHostPaths(message, token);
    },
    async listProcesses(nameFilter?: string): Promise<ProcessEntry[]> {
      const helper = getHelper();
      if (!helper) return [];
      try {
        const res = await helper.request<{ processes: ProcessEntry[] }>('list_processes', nameFilter ? { nameFilter } : {});
        return Array.isArray(res?.processes) ? res.processes : [];
      } catch {
        return [];
      }
    },
    async isProcessAlive(pid: number, expectedCreationTimeMs?: number): Promise<boolean> {
      if (!Number.isSafeInteger(pid) || pid <= 0) return false;
      const helper = getHelper();
      if (helper) {
        try {
          const res = await helper.request<{ alive: boolean }>('is_process_alive', {
            pid,
            ...(expectedCreationTimeMs !== undefined ? { creationTimeMs: expectedCreationTimeMs } : {}),
          });
          if (typeof res?.alive === 'boolean') return res.alive;
        } catch {}
      }
      try {
        process.kill(pid, 0);
        return true;
      } catch (error) {
        return (error as NodeJS.ErrnoException).code === 'EPERM';
      }
    },
    async findTcpListenersByPort(port: number): Promise<TcpListenerEntry[]> {
      const helper = getHelper();
      if (!helper) return [];
      try {
        const res = await helper.request<{ listeners: TcpListenerEntry[] }>('tcp_listeners', { port });
        return Array.isArray(res?.listeners) ? res.listeners : [];
      } catch {
        return [];
      }
    },
    async findTcpListenersByPid(pid: number): Promise<TcpListenerEntry[]> {
      const helper = getHelper();
      if (!helper) return [];
      try {
        const res = await helper.request<{ listeners: TcpListenerEntry[] }>('tcp_listeners', { pid });
        return Array.isArray(res?.listeners) ? res.listeners : [];
      } catch {
        return [];
      }
    },
    async readProcessEnvVar(pid: number, varName: string): Promise<string | null> {
      const helper = getHelper();
      if (!helper) return null;
      try {
        const res = await helper.request<{ value: string | null }>('read_process_env', { pid, name: varName });
        return typeof res?.value === 'string' && res.value.length > 0 ? res.value : null;
      } catch {
        return null;
      }
    },
    async verifyOfficialApp(appPath: string): Promise<VerifiedBundleInfo> {
      const layout = resolveAppLayoutForPlatform('win32', appPath);
      if (!existsSync(layout.asarPath)) {
        throw new Error(bt('discovery.app_mismatch'));
      }
      const helper = getHelper();
      let publisherVerified = false;
      let signatureFingerprint = '';
      let peVersion = '';

      if (helper) {
        const auth = await helper.request<{
          valid: boolean;
          status?: string;
          subject?: string;
          thumbprint?: string;
          fileVersion?: string;
          productName?: string;
          companyName?: string;
        }>('verify_authenticode', { path: layout.executablePath });
        peVersion = auth?.fileVersion?.trim() || '';
        if (auth?.valid) {
          if (!WIN_OFFICIAL_SIGNER_SUBJECT_RE.test(auth.subject || '')) {
            throw new Error(bt('discovery.signature_failed'));
          }
          publisherVerified = true;
          signatureFingerprint = auth.thumbprint || auth.subject || 'Authenticode:Google';
        } else if (auth?.status === 'NotSigned') {
          // CHK-02: Official Windows Antigravity 2.34.0 ships NotSigned PE with Google VersionInfo + app.asar
          const prodOk = auth.productName === 'Antigravity';
          const companyOk = WIN_OFFICIAL_COMPANY_RE.test(auth.companyName || '');
          if (!prodOk || !companyOk || !existsSync(layout.languageServerPath)) {
            throw new Error(bt('discovery.signature_failed'));
          }
          publisherVerified = true;
          signatureFingerprint = `PE:Google:Antigravity:${peVersion}`;
        } else {
          throw new Error(bt('discovery.signature_failed'));
        }
      }

      const archive = await openAsarArchive(layout.asarPath, 16_000_000);
      try {
        let version = peVersion;
        let productName = 'Antigravity';
        const pkgEntry = archive.header.files?.['package.json'];
        if (pkgEntry && !pkgEntry.unpacked) {
          const pkgBuf = await archive.readEntryBuffer(pkgEntry, 256_000, 1);
          if (pkgBuf) {
            const pkg = JSON.parse(pkgBuf.toString('utf8'));
            if (typeof pkg?.version === 'string' && pkg.version.trim()) version = pkg.version.trim();
            if (typeof pkg?.productName === 'string' && pkg.productName.trim()) productName = pkg.productName.trim();
          }
        }
        if (productName !== 'Antigravity') {
          throw new Error(bt('discovery.app_mismatch'));
        }

        let legacyPatch = false;
        for (const name of ['preload.js', 'main.js', 'utils.js']) {
          const entry = archive.header.files?.dist?.files?.[name];
          if (!entry || entry.unpacked) continue;
          const bytes = await archive.readEntryBuffer(entry, 3_000_000, 1);
          if (!bytes) continue;
          const text = bytes.toString();
          legacyPatch ||= /ANTIGRAVITY CHINESE LOCALIZATION START|injectAntigravityI18n|__ANTIGRAVITY_I18N_DATA__|createI18nEngine|__ag_hanhua_engine__/.test(text);
        }

        return {
          version,
          productName,
          publisherVerified,
          signatureFingerprint: signatureFingerprint || `ASAR:${version}`,
          legacyPatch,
        };
      } finally {
        await archive.close();
      }
    },
    async readAppVersionFast(appPath: string): Promise<string> {
      const layout = resolveAppLayoutForPlatform('win32', appPath);
      if (existsSync(layout.asarPath)) {
        let archive: ReturnType<typeof openAsarSync> | undefined;
        try {
          archive = openAsarSync(layout.asarPath, 16_000_000);
          const pkgBuf = archive.readEntryBuffer(archive.header.files?.['package.json'], 256_000, 1);
          if (pkgBuf) {
            const pkg = JSON.parse(pkgBuf.toString('utf8'));
            if (typeof pkg?.version === 'string' && pkg.version.trim()) {
              return pkg.version.trim();
            }
          }
        } catch {} finally {
          archive?.close();
        }
      }
      // Fallback: macOS bundle Info.plist inside cross-platform unit test fixtures
      const plistPath = join(appPath, 'Contents', 'Info.plist');
      if (existsSync(plistPath)) {
        try {
          const raw = readFileSync(plistPath, 'utf8');
          const m = raw.match(/<key>CFBundleShortVersionString<\/key>\s*<string>([^<]+)<\/string>/);
          if (m?.[1]) return m[1].trim();
        } catch {}
      }
      return '';
    },
    detectOsPreferredLocales(): string[] {
      const out: string[] = [];
      try {
        const intlLoc = Intl.DateTimeFormat().resolvedOptions().locale;
        if (intlLoc) out.push(intlLoc);
      } catch {}
      return out;
    },
    createKeychainAdapter(): KeychainAdapter {
      return new Win32CredentialAdapter(getHelper);
    },
    async scanAntigravityShortcuts(officialExePath?: string, stateDir?: string): Promise<ShortcutEntry[]> {
      const helper = getHelper();
      if (!helper) return [];
      const exePath = officialExePath || '';
      const res = await helper.request<{ shortcuts: Array<Omit<ShortcutEntry, 'hasDebugPort' | 'hasBackup'> & { hasDebugPort?: boolean; hasBackup?: boolean }> }>(
        'shortcut_scan',
        { officialExePath: exePath },
      ).catch(() => ({ shortcuts: [] }));
      let backupPaths = new Set<string>();
      if (stateDir) {
        try {
          const raw = JSON.parse(await readFile(join(stateDir, 'shortcut-backups.json'), 'utf8'));
          if (Array.isArray(raw?.entries)) {
            backupPaths = new Set(raw.entries.map((e: any) => String(e?.path || '').toLowerCase()).filter(Boolean));
          }
        } catch {}
      }
      const debugRe = /(?:^|\s)--remote-debugging-port(?:=\d+|\s+\d+)(?=\s|$)/i;
      return (Array.isArray(res?.shortcuts) ? res.shortcuts : []).map(item => ({
        path: item.path,
        location: item.location ?? 'custom',
        name: item.name ?? '',
        targetPath: item.targetPath ?? exePath,
        arguments: item.arguments ?? '',
        ...(item.workingDirectory ? { workingDirectory: item.workingDirectory } : {}),
        hasDebugPort: typeof item.hasDebugPort === 'boolean' ? item.hasDebugPort : debugRe.test(item.arguments || ''),
        hasBackup: typeof item.hasBackup === 'boolean' ? item.hasBackup : backupPaths.has(String(item.path || '').toLowerCase()),
      }));
    },
    async applyShortcutDebugPort(paths: string[] | undefined, stateDir: string, officialExePath?: string): Promise<ShortcutApplyResult> {
      const helper = getHelper();
      if (!helper) return { updated: [], skipped: [], failed: [] };
      const scanned = await this.scanAntigravityShortcuts!(officialExePath, stateDir);
      const filterSet = Array.isArray(paths) && paths.length > 0
        ? new Set(paths.map(p => resolve(p).toLowerCase()))
        : null;
      const targets = filterSet
        ? scanned.filter(s => filterSet.has(resolve(s.path).toLowerCase()))
        : scanned;
      const backupFile = join(stateDir, 'shortcut-backups.json');
      let backupDoc: { schema: number; updatedAt: string; entries: Array<Record<string, any>> } = {
        schema: 1,
        updatedAt: new Date().toISOString(),
        entries: [],
      };
      try {
        const parsed = JSON.parse(await readFile(backupFile, 'utf8'));
        if (parsed && Array.isArray(parsed.entries)) backupDoc = parsed;
      } catch {}

      const updated: string[] = [];
      const skipped: string[] = [];
      const failed: Array<{ path: string; error: string }> = [];
      const debugRe = /(?:^|\s)--remote-debugging-port(?:=\d+|\s+\d+)(?=\s|$)/i;

      for (const item of targets) {
        if (debugRe.test(item.arguments || '')) {
          skipped.push(item.path);
          continue;
        }
        const nextArgs = item.arguments?.trim()
          ? `${item.arguments.trim()} --remote-debugging-port=0`
          : '--remote-debugging-port=0';
        const existingIdx = backupDoc.entries.findIndex(e => String(e.path || '').toLowerCase() === item.path.toLowerCase());
        if (existingIdx < 0) {
          backupDoc.entries.push({
            path: item.path,
            location: item.location,
            targetPath: item.targetPath,
            originalArguments: item.arguments || '',
            appliedArguments: nextArgs,
            appliedAt: new Date().toISOString(),
          });
        }
        try {
          backupDoc.updatedAt = new Date().toISOString();
          await mkdir(stateDir, { recursive: true });
          await writeFile(backupFile, JSON.stringify(backupDoc, null, 2) + '\n', 'utf8');
          const res = await helper.request<{ ok: boolean }>('shortcut_set_args', { path: item.path, arguments: nextArgs });
          if (res?.ok) updated.push(item.path);
          else failed.push({ path: item.path, error: 'shortcut_set_args failed' });
        } catch (err) {
          failed.push({ path: item.path, error: sanitizeHostPaths((err as Error)?.message ?? String(err)) });
        }
      }
      return { updated, skipped, failed };
    },
    async restoreShortcutDebugPort(stateDir: string, paths?: string[]): Promise<ShortcutRestoreResult> {
      const helper = getHelper();
      if (!helper) return { restored: [], skipped: [], failed: [] };
      const backupFile = join(stateDir, 'shortcut-backups.json');
      let backupDoc: { schema: number; updatedAt: string; entries: Array<Record<string, any>> } = {
        schema: 1,
        updatedAt: new Date().toISOString(),
        entries: [],
      };
      try {
        const parsed = JSON.parse(await readFile(backupFile, 'utf8'));
        if (parsed && Array.isArray(parsed.entries)) backupDoc = parsed;
      } catch {}

      const filterSet = Array.isArray(paths) && paths.length > 0
        ? new Set(paths.map(p => resolve(p).toLowerCase()))
        : null;
      const entriesToRestore = filterSet
        ? backupDoc.entries.filter(e => filterSet.has(resolve(String(e.path || '')).toLowerCase()))
        : [...backupDoc.entries];

      const restored: string[] = [];
      const skipped: string[] = [];
      const failed: Array<{ path: string; error: string }> = [];
      const restoredLower = new Set<string>();

      for (const entry of entriesToRestore) {
        const lnkPath = String(entry.path || '');
        if (!lnkPath) continue;
        try {
          const res = await helper.request<{ ok: boolean }>('shortcut_set_args', {
            path: lnkPath,
            arguments: String(entry.originalArguments ?? ''),
          });
          if (res?.ok) {
            restored.push(lnkPath);
            restoredLower.add(lnkPath.toLowerCase());
          } else {
            failed.push({ path: lnkPath, error: 'shortcut_set_args failed' });
          }
        } catch (err) {
          failed.push({ path: lnkPath, error: sanitizeHostPaths((err as Error)?.message ?? String(err)) });
        }
      }

      if (restoredLower.size > 0) {
        backupDoc.entries = backupDoc.entries.filter(e => !restoredLower.has(String(e.path || '').toLowerCase()));
        backupDoc.updatedAt = new Date().toISOString();
        await writeFile(backupFile, JSON.stringify(backupDoc, null, 2) + '\n', 'utf8').catch(() => {});
      }
      return { restored, skipped, failed };
    },
  };
}
