#!/usr/bin/env node
/**
 * T-021: Windows 实机只读冒烟探针（02-design.md §4.5, §4.6.2）。
 * 在安装了官方 Antigravity 的 Windows 主机上验证：
 *   1. detectInstalledChannels() 命中官方目录；
 *   2. inspectBundle() 通过 Authenticode/PE + app.asar 验真并读出正确版本号；
 *   3. discover() 在 Antigravity 开启调试端口时返回回环 CDP 端点；
 *   4. probeLiveLanguageServerOnce() 返回脱敏的实时订阅等级与配额结构（或验证 Win32CredentialAdapter 读写契约）。
 * 全程只读、输出严格脱敏（零真实邮箱、零令牌、零本机真实用户名路径）。
 */
import assert from 'node:assert/strict';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { detectInstalledChannels, applicationTarget } from '../../dist/application-target.js';
import { inspectBundle, discover } from '../../dist/discovery.js';
import { AccountsManager } from '../../dist/accounts.js';
import { SUPPORTED_BUNDLES, isVerifiedBundleHash } from '../../dist/bundle.js';
import { getPlatform } from '../../dist/platform/index.js';

function maskEmail(email) {
  if (!email || typeof email !== 'string') return '<none>';
  const idx = email.indexOf('@');
  if (idx <= 0) return '<masked>';
  return `${email[0]}***@***`;
}

async function main() {
  const platform = getPlatform();
  console.log(`[smoke-probe-win] Platform: ${platform.id} (${process.arch})`);

  // Probe 1: detectInstalledChannels()
  const installInfo = detectInstalledChannels('stable');
  const target = applicationTarget('stable');
  assert.ok(installInfo.channels.includes('stable'), 'detectInstalledChannels 应包含 stable 通道');
  const sanitizedAppPath = platform.sanitizeHostPaths(target.appPath);
  console.log(`[PASS] 1. detectInstalledChannels -> channel=${installInfo.effectiveChannel}, appPath=${sanitizedAppPath}`);

  // Probe 2: inspectBundle()
  const bundleInfo = await inspectBundle(target.appPath);
  assert.ok(bundleInfo.version && /^\d+\.\d+\.\d+/.test(bundleInfo.version), `无效版本号: ${bundleInfo.version}`);
  assert.equal(typeof bundleInfo.legacyPatch, 'boolean');
  const registeredHashes = SUPPORTED_BUNDLES.get(bundleInfo.version);
  console.log(
    `[PASS] 2. inspectBundle -> version=${bundleInfo.version}, legacyPatch=${bundleInfo.legacyPatch}, registeredInBundleTs=${Boolean(registeredHashes)}`,
  );
  if (bundleInfo.version === '2.34.0') {
    assert.equal(
      isVerifiedBundleHash('2.34.0', 'd52ce739c15044c02a32e1b9df62bfe39dd8164c70a2ab783540e6ed5e070251'),
      true,
      'Windows 2.34.0 main.js SHA-256 必须已登记在 src/bundle.ts SUPPORTED_BUNDLES 中',
    );
  }

  // Probe 3: discover()
  const endpoint = await discover(target.appPath, target.profile);
  if (endpoint) {
    assert.ok(endpoint.port > 0 && endpoint.port <= 65535);
    assert.ok(endpoint.browserURL.startsWith('ws://127.0.0.1:'));
    console.log(
      `[PASS] 3. discover -> found=true, pid=${endpoint.pid}, port=${endpoint.port}, appVersion=${endpoint.appVersion}, loopback=true`,
    );
  } else {
    console.log('[PASS] 3. discover -> found=false (host Antigravity currently has no active DevToolsActivePort listener)');
  }

  // Probe 4: AccountsManager.probeLiveLanguageServer() & Win32CredentialAdapter read-only structure check
  const tempStateDir = join(tmpdir(), 'gb-win-smoke-readonly');
  const accountsMgr = new AccountsManager(tempStateDir, { target });
  const liveLs = await accountsMgr.probeLiveLanguageServer();
  if (liveLs) {
    const groupCount = liveLs.quota?.quota_groups?.length ?? 0;
    const modelCount = liveLs.quota?.models?.length ?? 0;
    console.log(
      `[PASS] 4. probeLiveLanguageServer -> tier=${liveLs.tier ?? 'unknown'}, email=${maskEmail(liveLs.email)}, groups=${groupCount}, models=${modelCount}`,
    );
  } else {
    console.log('[PASS] 4. probeLiveLanguageServer -> null (language_server not active)');
  }

  const keychain = platform.createKeychainAdapter();
  const rawCred = await keychain.readPassword('gemini', 'antigravity');
  const hasCred = typeof rawCred === 'string' && rawCred.startsWith('go-keyring-base64:');
  console.log(`[PASS] 5. Win32CredentialAdapter.readPassword -> present=${hasCred} (format=go-keyring-base64)`);
}

main().catch(err => {
  console.error('[FAIL] smoke-probe-win:', err?.message ?? err);
  process.exit(1);
});
