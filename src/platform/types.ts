import type * as net from 'node:net';
import type { KeychainAdapter } from '../account-keychain.js';

export type PlatformId = 'darwin' | 'win32';

export interface PlatformPaths {
  stateDir: string;
  cacheDir: string;
  logDir: string;
  defaultTrashDir: string;
  appSupportDir: string;
  defaultAntigravityProfile: string;
  defaultGeminiDir: string;
  defaultAppCandidates: string[];
}

export interface AppBundleLayout {
  appRoot: string;
  executablePath: string;
  resourcesDir: string;
  asarPath: string;
  asarUnpackedPath: string;
  binDir: string;
  languageServerPath: string;
  languageServerExePattern: RegExp;
  versionSourcePath: string;
}

export interface ProcessEntry {
  pid: number;
  parentPid: number;
  executablePath: string;
  commandLine: string;
  cpuPercent?: number;
  creationTimeMs?: number;
}

export interface TcpListenerEntry {
  pid: number;
  localAddress: string;
  localPort: number;
  executablePath: string;
}

export interface VerifiedBundleInfo {
  version: string;
  productName: string;
  publisherVerified: boolean;
  signatureFingerprint: string;
  legacyPatch: boolean;
}

export interface ShortcutEntry {
  path: string;
  location: 'desktop' | 'start-menu' | 'taskbar' | 'custom';
  name: string;
  targetPath: string;
  arguments: string;
  workingDirectory?: string;
  hasDebugPort: boolean;
  hasBackup: boolean;
}

export interface ShortcutApplyResult {
  updated: string[];
  skipped: string[];
  failed: { path: string; error: string }[];
}

export interface ShortcutRestoreResult {
  restored: string[];
  skipped: string[];
  failed: { path: string; error: string }[];
}

export interface WinHelperClientLike {
  request<T = any>(method: string, params?: Record<string, unknown>): Promise<T>;
  close?(): Promise<void>;
}

export interface PlatformAdapter {
  readonly id: PlatformId;
  getDefaultPaths(homeDir?: string): PlatformPaths;
  resolveAppLayout(appPath: string): AppBundleLayout;
  detectInstalledAppPath(overrideDir?: string, homeDir?: string): string | null;
  isInsidersPath(appOrProfilePath: string): boolean;

  // IPC
  getIpcEndpoint(stateDir: string): string;
  createIpcServer(
    endpoint: string,
    onConnection: (socket: net.Socket) => void,
    options?: { stateDir?: string }
  ): Promise<net.Server>;
  verifyIpcEndpoint(endpoint: string): Promise<void>;
  cleanupIpcEndpoint(endpoint: string): Promise<void>;
  readIpcAuthToken?(stateDir: string): Promise<string | null>;
  ensureIpcAuthToken?(stateDir: string): Promise<string | null>;

  // 安全与文件系统
  assertOwnedPath(targetPath: string, expectedType: 'file' | 'dir' | 'any'): Promise<void>;
  assertOwnedPathSync(targetPath: string, expectedType: 'file' | 'dir' | 'any'): void;
  hardenDirectoryPermissions(dirPath: string): Promise<void>;
  hardenFilePermissions(filePath: string): Promise<void>;
  isStrictSubpath(parentDir: string, candidatePath: string): boolean;
  sanitizeHostPaths(message: string, token?: string): string;

  // 进程、网络、验真与环境变量
  listProcesses(nameFilter?: string): Promise<ProcessEntry[]>;
  isProcessAlive(pid: number, expectedCreationTimeMs?: number): Promise<boolean>;
  findTcpListenersByPort(port: number): Promise<TcpListenerEntry[]>;
  findTcpListenersByPid(pid: number): Promise<TcpListenerEntry[]>;
  readProcessEnvVar(pid: number, varName: string): Promise<string | null>;
  verifyOfficialApp(appPath: string): Promise<VerifiedBundleInfo>;
  readAppVersionFast(appPath: string): Promise<string>;

  // 系统语言与凭据
  detectOsPreferredLocales(): string[];
  createKeychainAdapter(): KeychainAdapter;

  // Windows 扩展
  scanAntigravityShortcuts?(officialExePath?: string, stateDir?: string): Promise<ShortcutEntry[]>;
  applyShortcutDebugPort?(paths: string[] | undefined, stateDir: string, officialExePath?: string): Promise<ShortcutApplyResult>;
  restoreShortcutDebugPort?(stateDir: string, paths?: string[]): Promise<ShortcutRestoreResult>;
}
