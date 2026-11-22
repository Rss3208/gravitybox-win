import { createDarwinPlatformAdapter } from './darwin.js';
import { createWin32PlatformAdapter, type Win32PlatformOptions } from './win32.js';
import type { PlatformAdapter } from './types.js';

export * from './types.js';
export * from './paths.js';
export * from './security.js';
export { createDarwinPlatformAdapter } from './darwin.js';
export { createWin32PlatformAdapter, computeWinNamedPipeEndpoint, Win32CredentialAdapter } from './win32.js';

let activePlatform: PlatformAdapter | null = null;
let defaultWinOptions: Win32PlatformOptions = {};

export function configureWin32Platform(options: Win32PlatformOptions): void {
  defaultWinOptions = { ...defaultWinOptions, ...options };
  if (activePlatform?.id === 'win32') {
    activePlatform = createWin32PlatformAdapter(defaultWinOptions);
  }
}

export function getPlatform(): PlatformAdapter {
  if (!activePlatform) {
    activePlatform = process.platform === 'win32'
      ? createWin32PlatformAdapter(defaultWinOptions)
      : createDarwinPlatformAdapter();
  }
  return activePlatform;
}

export function setPlatformForTests(adapter: PlatformAdapter | null): void {
  activePlatform = adapter;
}
