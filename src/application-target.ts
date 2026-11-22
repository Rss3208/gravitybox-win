import { existsSync } from 'node:fs';
import { homedir } from 'node:os';
import { join, posix as posixPath } from 'node:path';
import { openAsarSync } from './asar.js';
import { bt } from './i18n.js';
import { getPlatform } from './platform/index.js';

export type ApplicationChannel = 'stable';
export type ApplicationArchitecture = 'legacy-hub' | 'wcv-hub';

export const ALLOWED_DATA_DIRECTORY_NAMES = ['antigravity'] as const;
export type DataDirectoryName = (typeof ALLOWED_DATA_DIRECTORY_NAMES)[number];

export interface InstalledChannelsInfo {
  channels: ApplicationChannel[];
  bothInstalled: boolean;
  effectiveChannel: ApplicationChannel;
}

export interface BundleCapabilities {
  architecture: ApplicationArchitecture;
  version: string;
  productName: string;
  dataDirectoryName: DataDirectoryName;
  supportsAccountSwitch: boolean;
}

const capabilityCache = new Map<string, { value: BundleCapabilities; expiresAt: number }>();

/** Inspect the supported stable bundle without changing the official application. */
export function probeBundleCapabilities(
  appPath: string,
  channel: ApplicationChannel = 'stable',
): BundleCapabilities {
  applicationChannel(channel);
  assertStablePaths(appPath);
  const now = Date.now();
  const cached = capabilityCache.get(appPath);
  if (cached && cached.expiresAt > now) return cached.value;

  const fallback: BundleCapabilities = {
    architecture: 'wcv-hub',
    version: '',
    productName: 'Antigravity',
    dataDirectoryName: 'antigravity',
    supportsAccountSwitch: true,
  };

  const layout = getPlatform().resolveAppLayout(appPath);
  const asarPath = layout.asarPath;
  let archive: ReturnType<typeof openAsarSync> | undefined;
  try {
    if (!existsSync(asarPath)) return fallback;
    archive = openAsarSync(asarPath, 16_000_000);
    const distFiles = archive.header.files?.dist?.files ?? {};
    const hasWcvArchitecture = Boolean(
      distFiles['wcvBrowserRegistry.js'] ||
      distFiles['wcvIpcHandlers.js'] ||
      distFiles['browserEngine'] ||
      distFiles['windowState.js'],
    );

    let version = '';
    let productName = fallback.productName;
    const pkgBuf = archive.readEntryBuffer(archive.header.files?.['package.json'], 256_000, 1);
    if (pkgBuf) {
      const pkg = JSON.parse(pkgBuf.toString('utf8'));
      if (typeof pkg?.version === 'string') version = pkg.version.trim();
      if (typeof pkg?.productName === 'string' && pkg.productName.trim()) productName = pkg.productName.trim();
    }

    const architecture: ApplicationArchitecture = hasWcvArchitecture
      ? 'wcv-hub'
      : 'legacy-hub';
    const dataDirectoryName: DataDirectoryName = 'antigravity';

    const resolved: BundleCapabilities = {
      architecture,
      version,
      productName,
      dataDirectoryName,
      supportsAccountSwitch: true,
    };
    capabilityCache.set(appPath, { value: resolved, expiresAt: now + 30_000 });
    return resolved;
  } catch {
    return fallback;
  } finally {
    archive?.close();
  }
}

export function applicationChannel(value: unknown): ApplicationChannel {
  if (value === undefined || value === 'stable') return 'stable';
  if (value === 'insiders') throw new Error(bt('app_target.stable_only'));
  throw new Error(bt('app_target.invalid_channel'));
}

/** Only persisted legacy configuration is migrated; explicit channel requests are rejected. */
export function savedApplicationChannel(value: unknown): ApplicationChannel {
  return value === 'insiders' ? 'stable' : applicationChannel(value);
}

function assertStablePaths(appPath: string, profile?: string): void {
  const platform = getPlatform();
  if (
    platform.isInsidersPath(appPath) ||
    (profile && platform.isInsidersPath(profile))
  ) {
    throw new Error(bt('app_target.stable_only'));
  }
}

export function detectInstalledChannels(
  preferredChannel: ApplicationChannel = 'stable',
  applicationsDir?: string,
): InstalledChannelsInfo {
  applicationChannel(preferredChannel);
  const platform = getPlatform();
  let detected: string | null = null;
  if (applicationsDir !== undefined) {
    if (existsSync(join(applicationsDir, 'Antigravity.app'))) {
      detected = join(applicationsDir, 'Antigravity.app');
    } else {
      detected = platform.detectInstalledAppPath(applicationsDir);
    }
  } else {
    detected = platform.detectInstalledAppPath();
  }
  return {
    channels: detected ? ['stable'] : [],
    bothInstalled: false,
    effectiveChannel: 'stable',
  };
}

/** Keep identity, executable, profile, and conversation storage bound to the target while unifying user-facing label and full feature support. */
export function applicationTarget(
  channel: ApplicationChannel = 'stable',
  home = homedir(),
  options?: { appPath?: string; profile?: string; applicationsDir?: string },
) {
  applicationChannel(channel);
  const name = 'Antigravity';
  const platform = getPlatform();
  const isPosixSyntheticHome = home.startsWith('/');
  let appPath: string;
  if (options?.appPath) {
    appPath = options.appPath;
  } else if (options?.applicationsDir) {
    if (platform.id === 'win32' && !options.applicationsDir.startsWith('/')) {
      appPath = platform.detectInstalledAppPath(options.applicationsDir, home) ?? join(options.applicationsDir, name);
    } else {
      appPath = join(options.applicationsDir, `${name}.app`);
    }
  } else if (platform.id === 'win32' && !isPosixSyntheticHome) {
    appPath = platform.detectInstalledAppPath(undefined, home) ?? platform.getDefaultPaths(home).defaultAppCandidates[0]!;
  } else {
    appPath = `/Applications/${name}.app`;
  }

  let defaultProfile: string;
  if (isPosixSyntheticHome) {
    defaultProfile = posixPath.join(home, 'Library', 'Application Support', name);
  } else {
    defaultProfile = platform.getDefaultPaths(home).defaultAntigravityProfile;
  }

  assertStablePaths(appPath, options?.profile);
  const capabilities = probeBundleCapabilities(appPath, channel);
  return {
    channel,
    name,
    label: 'Antigravity',
    bundleId: 'com.google.antigravity',
    appPath,
    executable: platform.id === 'win32' && !appPath.endsWith('.app') ? `${name}.exe` : name,
    profile: options?.profile ?? defaultProfile,
    dataDirectoryName: capabilities.dataDirectoryName,
    architecture: capabilities.architecture,
    supportsAccountSwitch: true,
  };
}

export type ApplicationTarget = ReturnType<typeof applicationTarget>;

export function allApplicationTargets(home = homedir(), applicationsDir?: string): ApplicationTarget[] {
  return [
    applicationTarget('stable', home, applicationsDir ? { applicationsDir } : undefined),
  ];
}

export function targetForApp(appPath: string, home = homedir()): ApplicationTarget {
  return applicationTarget('stable', home, { appPath });
}

export function commandMatchesTargetLanguageServer(
  command: string,
  candidate: ApplicationTarget,
): boolean {
  if (!command) return false;
  if (
    command.startsWith(candidate.appPath + '/Contents/Resources/bin/language_server ') ||
    (!candidate.appPath.startsWith('/') && command.startsWith('/Applications/Antigravity.app/Contents/Resources/bin/language_server '))
  ) {
    return true;
  }
  const trimmed = command.trimStart();
  let exeToken = '';
  if (trimmed.startsWith('"')) {
    const closeIdx = trimmed.indexOf('"', 1);
    if (closeIdx <= 1) return false;
    const afterQuote = trimmed.slice(closeIdx + 1);
    if (afterQuote.length > 0 && !/^\s/.test(afterQuote)) return false;
    exeToken = trimmed.slice(1, closeIdx);
  } else {
    const wsMatch = trimmed.match(/\s/);
    if (!wsMatch || wsMatch.index === undefined) return false;
    exeToken = trimmed.slice(0, wsMatch.index);
  }
  const normExe = exeToken.replace(/\\/g, '/').toLowerCase();
  const normApp = candidate.appPath.replace(/\\/g, '/').replace(/\/+$/, '').toLowerCase();
  if (normExe === `${normApp}/contents/resources/bin/language_server`) return true;
  if (
    normExe.startsWith(`${normApp}/resources/bin/language_server`) &&
    (normExe.endsWith('.exe') || normExe === `${normApp}/resources/bin/language_server`)
  ) {
    return true;
  }
  return false;
}

export function matchingTargetForLanguageServerCommand(
  command: string,
  preferredTarget?: ApplicationTarget,
  home = homedir(),
): ApplicationTarget | null {
  const candidates = preferredTarget
    ? [preferredTarget, ...allApplicationTargets(home).filter(t => t.appPath !== preferredTarget.appPath)]
    : allApplicationTargets(home);
  for (const candidate of candidates) {
    if (!commandMatchesTargetLanguageServer(command, candidate)) continue;
    const dataDirectory = command.match(/(?:^|\s)--app_data_dir(?:=|\s+)([^\s"]+)/)?.[1];
    if (dataDirectory && dataDirectory !== candidate.dataDirectoryName) continue;
    return candidate;
  }
  return null;
}

export function channelStateDirectory(stateDir: string, _channel?: ApplicationChannel): string {
  return stateDir;
}
