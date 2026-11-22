import { execFile, execFileSync } from 'node:child_process';
import { promisify } from 'node:util';
import * as net from 'node:net';
import { chmod, lstat, unlink } from 'node:fs/promises';
import { join } from 'node:path';
import { homedir } from 'node:os';
import { openAsarArchive } from '../asar.js';
import { SystemKeychainAdapter, type KeychainAdapter } from '../account-keychain.js';
import { bt } from '../i18n.js';
import {
  detectInstalledAppPathForPlatform,
  isInsidersPathForPlatform,
  resolveAppLayoutForPlatform,
  resolvePlatformPaths,
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
  TcpListenerEntry,
  VerifiedBundleInfo,
} from './types.js';

const execFileAsync = promisify(execFile);

export function createDarwinPlatformAdapter(): PlatformAdapter {
  return {
    id: 'darwin',
    getDefaultPaths(homeDir = homedir()): PlatformPaths {
      return resolvePlatformPaths('darwin', homeDir);
    },
    resolveAppLayout(appPath: string): AppBundleLayout {
      return resolveAppLayoutForPlatform('darwin', appPath);
    },
    detectInstalledAppPath(overrideDir?: string, homeDir = homedir()): string | null {
      return detectInstalledAppPathForPlatform('darwin', overrideDir, homeDir);
    },
    isInsidersPath(appOrProfilePath: string): boolean {
      return isInsidersPathForPlatform('darwin', appOrProfilePath);
    },
    getIpcEndpoint(stateDir: string): string {
      return join(stateDir, 'control.sock');
    },
    async createIpcServer(
      endpoint: string,
      onConnection: (socket: net.Socket) => void
    ): Promise<net.Server> {
      const server = net.createServer(onConnection);
      const mask = process.umask(0o077);
      try {
        await new Promise<void>((resolve, reject) => {
          server.once('error', reject);
          server.listen(endpoint, () => resolve());
        });
      } finally {
        process.umask(mask);
      }
      await chmod(endpoint, 0o600);
      return server;
    },
    async verifyIpcEndpoint(endpoint: string): Promise<void> {
      const info = await lstat(endpoint);
      const currentUid = typeof process.getuid === 'function' ? process.getuid() : undefined;
      if (!info.isSocket() || (currentUid !== undefined && info.uid !== currentUid)) {
        throw new Error(bt('main.unsafe_control_sock'));
      }
    },
    async cleanupIpcEndpoint(endpoint: string): Promise<void> {
      await unlink(endpoint).catch(() => {});
    },
    async assertOwnedPath(targetPath: string, expectedType: 'file' | 'dir' | 'any'): Promise<void> {
      await assertOwnedPathForPlatform('darwin', targetPath, expectedType);
    },
    assertOwnedPathSync(targetPath: string, expectedType: 'file' | 'dir' | 'any'): void {
      assertOwnedPathSyncForPlatform('darwin', targetPath, expectedType);
    },
    async hardenDirectoryPermissions(dirPath: string): Promise<void> {
      await hardenDirectoryPermissionsForPlatform('darwin', dirPath);
    },
    async hardenFilePermissions(filePath: string): Promise<void> {
      await hardenFilePermissionsForPlatform('darwin', filePath);
    },
    isStrictSubpath(parentDir: string, candidatePath: string): boolean {
      return isStrictSubpathForPlatform('darwin', parentDir, candidatePath);
    },
    sanitizeHostPaths(message: string, token = ''): string {
      return sanitizeHostPaths(message, token);
    },
    async listProcesses(): Promise<ProcessEntry[]> {
      try {
        const { stdout } = await execFileAsync(
          '/bin/ps',
          ['-ax', '-ww', '-o', 'pid=,ppid=,%cpu=,comm=,args='],
          { timeout: 3000, maxBuffer: 8 * 1024 * 1024 }
        );
        const list: ProcessEntry[] = [];
        for (const line of stdout.split('\n')) {
          const trimmed = line.trim();
          if (!trimmed) continue;
          const m = trimmed.match(/^(\d+)\s+(\d+)\s+([\d.]+)\s+(\S+)\s*(.*)$/);
          if (!m) continue;
          const pid = Number(m[1]);
          const parentPid = Number(m[2]);
          const cpuPercent = Number(m[3]);
          const comm = m[4] || '';
          const args = m[5] || comm;
          if (!Number.isSafeInteger(pid) || pid <= 0) continue;
          list.push({
            pid,
            parentPid: Number.isSafeInteger(parentPid) ? parentPid : 0,
            executablePath: comm,
            commandLine: args,
            cpuPercent: Number.isFinite(cpuPercent) ? cpuPercent : 0,
          });
        }
        return list;
      } catch {
        return [];
      }
    },
    async isProcessAlive(pid: number): Promise<boolean> {
      if (!Number.isSafeInteger(pid) || pid <= 0) return false;
      try {
        process.kill(pid, 0);
        return true;
      } catch (error) {
        return (error as NodeJS.ErrnoException).code === 'EPERM';
      }
    },
    async findTcpListenersByPort(port: number): Promise<TcpListenerEntry[]> {
      try {
        const { stdout } = await execFileAsync(
          '/usr/sbin/lsof',
          ['-nP', `-iTCP:${port}`, '-sTCP:LISTEN', '-Fpfn'],
          { timeout: 3000 }
        );
        const raw: { pid: number; addrs: string[] }[] = [];
        let current: { pid: number; addrs: string[] } | undefined;
        for (const line of stdout.split('\n')) {
          if (line.startsWith('p')) {
            current = { pid: Number(line.slice(1)), addrs: [] };
            raw.push(current);
          } else if (line.startsWith('n') && current) {
            current.addrs.push(line.slice(1));
          }
        }
        const entries: TcpListenerEntry[] = [];
        for (const item of raw) {
          if (!item.addrs.length) continue;
          let exe = '';
          try {
            const txtOut = (
              await execFileAsync('/usr/sbin/lsof', ['-nP', '-a', '-p', String(item.pid), '-d', 'txt', '-Fn'], {
                timeout: 3000,
              })
            ).stdout;
            const nLine = txtOut.split('\n').find(l => l.startsWith('n/'));
            if (nLine) exe = nLine.slice(1);
          } catch {}
          for (const addr of item.addrs) {
            const idx = addr.lastIndexOf(':');
            if (idx <= 0) continue;
            const host = addr.slice(0, idx).replace(/^\[|\]$/g, '');
            const p = Number(addr.slice(idx + 1));
            entries.push({
              pid: item.pid,
              localAddress: host,
              localPort: p,
              executablePath: exe,
            });
          }
        }
        return entries;
      } catch {
        return [];
      }
    },
    async findTcpListenersByPid(pid: number): Promise<TcpListenerEntry[]> {
      try {
        const [execRes, listenerRes] = await Promise.all([
          execFileAsync('/usr/sbin/lsof', ['-nP', '-a', '-p', String(pid), '-d', 'txt', '-Fn'], { timeout: 3000 }),
          execFileAsync('/usr/sbin/lsof', ['-nP', '-a', '-p', String(pid), '-iTCP', '-sTCP:LISTEN', '-Fn'], {
            timeout: 3000,
          }),
        ]);
        const exeLine = execRes.stdout.split('\n').find(l => l.startsWith('n/'));
        const exe = exeLine ? exeLine.slice(1) : '';
        const entries: TcpListenerEntry[] = [];
        for (const line of listenerRes.stdout.split('\n')) {
          if (!line.startsWith('n')) continue;
          const raw = line.slice(1);
          const idx = raw.lastIndexOf(':');
          if (idx <= 0) continue;
          const host = raw.slice(0, idx).replace(/^\[|\]$/g, '');
          const port = Number(raw.slice(idx + 1));
          if (!Number.isInteger(port)) continue;
          entries.push({ pid, localAddress: host, localPort: port, executablePath: exe });
        }
        return entries;
      } catch {
        return [];
      }
    },
    async readProcessEnvVar(pid: number, varName: string): Promise<string | null> {
      try {
        const { stdout } = await execFileAsync(
          '/bin/ps',
          ['-E', '-p', String(pid), '-ww', '-o', 'args='],
          { timeout: 3000 }
        );
        const re = new RegExp(`(?:^|\\s)${varName}=([^\\s]+)`);
        return stdout.match(re)?.[1] ?? null;
      } catch {
        return null;
      }
    },
    async verifyOfficialApp(appPath: string): Promise<VerifiedBundleInfo> {
      const plist = join(appPath, 'Contents', 'Info.plist');
      const asarPath = join(appPath, 'Contents', 'Resources', 'app.asar');
      const id = (await execFileAsync('/usr/libexec/PlistBuddy', ['-c', 'Print :CFBundleIdentifier', plist], { timeout: 3000 })).stdout.trim();
      if (id !== 'com.google.antigravity') throw new Error(bt('discovery.app_mismatch'));
      const version = (await execFileAsync('/usr/libexec/PlistBuddy', ['-c', 'Print :CFBundleShortVersionString', plist], { timeout: 3000 })).stdout.trim();
      const signature = await execFileAsync('/usr/bin/codesign', ['-dv', appPath], { timeout: 3000 });
      if (!signature.stderr.includes('TeamIdentifier=EQHXZ8M8AV')) throw new Error(bt('discovery.signature_failed'));
      await execFileAsync('/usr/bin/codesign', ['--verify', '--deep', '--strict', appPath], { timeout: 8000 });

      const archive = await openAsarArchive(asarPath, 16_000_000);
      try {
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
          productName: 'Antigravity',
          publisherVerified: true,
          signatureFingerprint: 'TeamIdentifier=EQHXZ8M8AV',
          legacyPatch,
        };
      } finally {
        await archive.close();
      }
    },
    async readAppVersionFast(appPath: string): Promise<string> {
      const plist = join(appPath, 'Contents', 'Info.plist');
      try {
        const { stdout } = await execFileAsync(
          '/usr/libexec/PlistBuddy',
          ['-c', 'Print :CFBundleShortVersionString', plist],
          { timeout: 2500 }
        );
        return stdout.trim();
      } catch {
        return '';
      }
    },
    detectOsPreferredLocales(): string[] {
      const out: string[] = [];
      try {
        const raw = execFileSync('/usr/bin/defaults', ['read', '-g', 'AppleLanguages'], {
          encoding: 'utf8',
          timeout: 1500,
          stdio: ['ignore', 'pipe', 'ignore'],
        });
        for (const m of raw.matchAll(/"?([A-Za-z]{2,3}(?:-[A-Za-z0-9]{2,8})*)"?/g)) {
          if (m[1]) out.push(m[1]);
        }
      } catch {}
      try {
        const intlLoc = Intl.DateTimeFormat().resolvedOptions().locale;
        if (intlLoc) out.push(intlLoc);
      } catch {}
      return out;
    },
    createKeychainAdapter(): KeychainAdapter {
      return new SystemKeychainAdapter();
    },
  };
}
