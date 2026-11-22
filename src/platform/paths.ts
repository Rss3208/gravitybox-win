import { existsSync } from 'node:fs';
import { homedir } from 'node:os';
import { basename, join, win32 as win32Path, posix as posixPath } from 'node:path';
import type { AppBundleLayout, PlatformId, PlatformPaths } from './types.js';

/** Day-0 (T-001 CHK-01..CHK-05) 实机冻结常量 */
export const WIN_LS_REL_PATH = 'resources\\bin\\language_server.exe';
export const WIN_LS_EXE_REGEX = /^language_server.*\.exe$/i;
export const WIN_OFFICIAL_SIGNER_SUBJECT_RE = /(?:O|CN)=Google(?:\s+LLC)?\b/i;
export const WIN_OFFICIAL_COMPANY_RE = /^Google(?:\s+LLC)?$/i;
export const WIN_CRED_TARGET_NAME = 'gemini:antigravity';
export const WIN_CRED_LEGACY_TARGETS = ['antigravity', 'gemini/antigravity'] as const;
export const WIN_BUNDLE_SHA256_2_34_0 = 'd52ce739c15044c02a32e1b9df62bfe39dd8164c70a2ab783540e6ed5e070251';

export function resolvePlatformPaths(
  platformId: PlatformId,
  homeDir = homedir(),
  env: NodeJS.ProcessEnv = process.env
): PlatformPaths {
  if (platformId === 'win32') {
    const p = win32Path;
    const defaultHome = env.USERPROFILE || homeDir;
    const isCustomHome = Boolean(homeDir && homeDir !== homedir());
    const localAppData = isCustomHome
      ? p.join(homeDir, 'AppData', 'Local')
      : (env.LOCALAPPDATA || p.join(defaultHome, 'AppData', 'Local'));
    const roamingAppData = isCustomHome
      ? p.join(homeDir, 'AppData', 'Roaming')
      : (env.APPDATA || p.join(defaultHome, 'AppData', 'Roaming'));
    const programFiles = env.ProgramFiles || 'C:\\Program Files';
    const stateDir = (!isCustomHome && env.GRAVITYBOX_STATE_DIR)
      ? env.GRAVITYBOX_STATE_DIR
      : p.join(localAppData, 'GravityBox');
    return {
      stateDir,
      cacheDir: p.join(stateDir, 'cache'),
      logDir: p.join(stateDir, 'logs'),
      defaultTrashDir: p.join(stateDir, 'maintenance-trash'),
      appSupportDir: roamingAppData,
      defaultAntigravityProfile: p.join(roamingAppData, 'Antigravity'),
      defaultGeminiDir: p.join(isCustomHome ? homeDir : defaultHome, '.gemini'),
      defaultAppCandidates: [
        p.join(localAppData, 'Programs', 'Antigravity'),
        p.join(localAppData, 'Programs', 'antigravity'),
        p.join(programFiles, 'Antigravity'),
      ],
    };
  }

  const p = posixPath;
  const isCustomHome = Boolean(homeDir && homeDir !== homedir());
  const stateDir = (!isCustomHome && env.GRAVITYBOX_STATE_DIR)
    ? env.GRAVITYBOX_STATE_DIR
    : p.join(homeDir, 'Library', 'Application Support', 'GravityBox');
  return {
    stateDir,
    cacheDir: p.join(homeDir, 'Library', 'Caches', 'GravityBox'),
    logDir: p.join(homeDir, 'Library', 'Logs', 'GravityBox'),
    defaultTrashDir: p.join(homeDir, '.Trash'),
    appSupportDir: p.join(homeDir, 'Library', 'Application Support'),
    defaultAntigravityProfile: p.join(homeDir, 'Library', 'Application Support', 'Antigravity'),
    defaultGeminiDir: p.join(homeDir, '.gemini'),
    defaultAppCandidates: ['/Applications/Antigravity.app'],
  };
}

export function resolveAppLayoutForPlatform(platformId: PlatformId, appPath: string): AppBundleLayout {
  if (platformId === 'win32' && !appPath.toLowerCase().endsWith('.app')) {
    const p = win32Path;
    const isExe = appPath.toLowerCase().endsWith('.exe');
    const appRoot = isExe ? p.dirname(appPath) : appPath;
    const executablePath = isExe ? appPath : p.join(appRoot, 'Antigravity.exe');
    const resourcesDir = p.join(appRoot, 'resources');
    const asarPath = p.join(resourcesDir, 'app.asar');
    const asarUnpackedPath = p.join(resourcesDir, 'app.asar.unpacked');
    const binDir = p.join(resourcesDir, 'bin');
    const languageServerPath = p.join(binDir, 'language_server.exe');
    return {
      appRoot,
      executablePath,
      resourcesDir,
      asarPath,
      asarUnpackedPath,
      binDir,
      languageServerPath,
      languageServerExePattern: WIN_LS_EXE_REGEX,
      versionSourcePath: asarPath,
    };
  }

  const resourcesDir = posixPath.join(appPath, 'Contents', 'Resources');
  const binDir = posixPath.join(resourcesDir, 'bin');
  return {
    appRoot: appPath,
    executablePath: posixPath.join(appPath, 'Contents', 'MacOS', 'Antigravity'),
    resourcesDir,
    asarPath: posixPath.join(resourcesDir, 'app.asar'),
    asarUnpackedPath: posixPath.join(resourcesDir, 'app.asar.unpacked'),
    binDir,
    languageServerPath: posixPath.join(binDir, 'language_server'),
    languageServerExePattern: /^language_server$/,
    versionSourcePath: posixPath.join(appPath, 'Contents', 'Info.plist'),
  };
}

export function isInsidersPathForPlatform(platformId: PlatformId, appOrProfilePath: string): boolean {
  if (!appOrProfilePath) return false;
  const norm = appOrProfilePath.replace(/\\/g, '/').replace(/\/+$/, '');
  const base = norm.slice(norm.lastIndexOf('/') + 1);
  if (platformId === 'win32') {
    const lower = base.toLowerCase();
    return (
      lower === 'antigravity - insiders' ||
      lower === 'antigravity - insiders.app' ||
      lower === 'antigravity - insiders.exe' ||
      /antigravity\s*-\s*insiders/i.test(norm)
    );
  }
  return base === 'Antigravity - Insiders.app' || base === 'Antigravity - Insiders';
}

export function detectInstalledAppPathForPlatform(
  platformId: PlatformId,
  overrideDir?: string,
  homeDir = homedir(),
  env: NodeJS.ProcessEnv = process.env
): string | null {
  if (platformId === 'win32') {
    if (overrideDir) {
      if (isInsidersPathForPlatform('win32', overrideDir)) return null;
      const directLayout = resolveAppLayoutForPlatform('win32', overrideDir);
      if (existsSync(directLayout.executablePath) || existsSync(directLayout.asarPath)) {
        return directLayout.appRoot;
      }
      const nested = join(overrideDir, 'Antigravity');
      const nestedLayout = resolveAppLayoutForPlatform('win32', nested);
      if (existsSync(nestedLayout.executablePath) || existsSync(nestedLayout.asarPath)) {
        return nestedLayout.appRoot;
      }
      // 兼容 macOS 风格的测试沙箱传入 applicationsDir + Antigravity.app
      const macBundleInSandbox = join(overrideDir, 'Antigravity.app');
      if (existsSync(macBundleInSandbox)) return macBundleInSandbox;
      return null;
    }
    const candidates = resolvePlatformPaths('win32', homeDir, env).defaultAppCandidates;
    for (const candidate of candidates) {
      const layout = resolveAppLayoutForPlatform('win32', candidate);
      if (existsSync(layout.executablePath) || existsSync(layout.asarPath)) {
        return layout.appRoot;
      }
    }
    return null;
  }

  const baseDir = overrideDir ?? '/Applications';
  const candidate = baseDir.endsWith('.app') && basename(baseDir) === 'Antigravity.app'
    ? baseDir
    : join(baseDir, 'Antigravity.app');
  return existsSync(candidate) ? candidate : null;
}
