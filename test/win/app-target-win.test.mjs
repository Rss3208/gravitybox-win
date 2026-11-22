import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdir, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { createWinTestSandbox } from './helpers.mjs';
import {
  applicationTarget,
  detectInstalledChannels,
  matchingTargetForLanguageServerCommand,
  probeBundleCapabilities,
  targetForApp,
} from '../../dist/application-target.js';
import { BundleCache } from '../../dist/bundle-cache.js';
import { nativeSources } from '../../dist/coverage.js';
import {
  readInstalledAppBundleVersion,
  readValidLiveIdeModelsSnapshot,
} from '../../dist/pricing-catalog-runtime.js';

function buildTestAsarBuffer(pkgObj, distFiles = {}) {
  const chunks = [];
  const filesHeader = {};
  let offset = 0;

  const pkgBytes = Buffer.from(JSON.stringify(pkgObj), 'utf8');
  filesHeader['package.json'] = { size: pkgBytes.length, offset: String(offset) };
  chunks.push(pkgBytes);
  offset += pkgBytes.length;

  const distHeader = {};
  for (const [name, content] of Object.entries(distFiles)) {
    const buf = Buffer.from(content, 'utf8');
    distHeader[name] = { size: buf.length, offset: String(offset) };
    chunks.push(buf);
    offset += buf.length;
  }
  if (Object.keys(distHeader).length > 0) {
    filesHeader.dist = { files: distHeader };
  }

  const headerBuf = Buffer.from(JSON.stringify({ files: filesHeader }), 'utf8');
  const alignedHeaderSize = Math.ceil((headerBuf.length + 4) / 4) * 4;
  const prefix = Buffer.alloc(16);
  prefix.writeUInt32LE(4, 0);
  prefix.writeUInt32LE(alignedHeaderSize + 4, 4);
  prefix.writeUInt32LE(alignedHeaderSize, 8);
  prefix.writeUInt32LE(headerBuf.length, 12);
  const paddedHeader = Buffer.alloc(alignedHeaderSize - 4);
  headerBuf.copy(paddedHeader, 0);

  return Buffer.concat([prefix, paddedHeader, ...chunks]);
}

test('T-007: Windows 安装目录探测、asar 能力识别、language_server 命令行匹配与 Insiders 拦截', async t => {
  const sandbox = await createWinTestSandbox('gb-t007-target-');
  t.after(() => sandbox.cleanup());

  const asarBuf = buildTestAsarBuffer(
    { name: 'antigravity', productName: 'Antigravity', version: '2.34.0' },
    {
      'wcvBrowserRegistry.js': 'exports.ok = true;',
      'menu.js': 'const m = { label: "Show in File Explorer" };',
    }
  );
  await writeFile(join(sandbox.appRoot, 'resources', 'app.asar'), asarBuf);

  const detected = detectInstalledChannels('stable', sandbox.appRoot);
  assert.deepEqual(detected.channels, ['stable']);

  const caps = probeBundleCapabilities(sandbox.appRoot, 'stable');
  assert.equal(caps.architecture, 'wcv-hub');
  assert.equal(caps.version, '2.34.0');
  assert.equal(caps.productName, 'Antigravity');

  const winTarget = applicationTarget('stable', sandbox.homeDir, {
    appPath: sandbox.appRoot,
    profile: sandbox.profileDir,
  });
  assert.equal(winTarget.appPath, sandbox.appRoot);
  assert.equal(winTarget.profile, sandbox.profileDir);

  // 支持 Windows 反斜杠、引号包裹、大小写不敏感的 language_server.exe 命令匹配
  const lsExe = join(sandbox.appRoot, 'resources', 'bin', 'language_server.exe');
  const winCmdQuoted = `"${lsExe.toUpperCase()}" --standalone --subclient_type hub --override_ide_version 2.34.0 --app_data_dir antigravity`;
  const matched = matchingTargetForLanguageServerCommand(winCmdQuoted, winTarget, sandbox.homeDir);
  assert.ok(matched);
  assert.equal(matched.appPath, sandbox.appRoot);

  // Insiders 路径与 --app_data_dir antigravity-insiders 必须被拒绝
  const insidersCmd = `"${lsExe}" --standalone --app_data_dir antigravity-insiders`;
  assert.equal(matchingTargetForLanguageServerCommand(insidersCmd, winTarget, sandbox.homeDir), null);
  assert.throws(() => targetForApp(join(sandbox.localAppData, 'Programs', 'Antigravity - Insiders')), /仅支持/);

  // BundleCache.key 支持 Windows resources\app.asar 与 resources\bin\language_server.exe
  const cache = new BundleCache();
  const key = await cache.key(sandbox.appRoot, 1234, '2.34.0', 'https://127.0.0.1:52124');
  assert.ok(key.includes('app.asar'));
  assert.ok(key.includes('bin/language_server'));

  // coverage.nativeSources 支持 Windows resources\app.asar
  const sources = await nativeSources(sandbox.appRoot);
  assert.ok(sources.some(s => s.id === 'app.asar/dist/menu.js' && s.source.includes('Show in File Explorer')));

  // pricing-catalog-runtime: readInstalledAppBundleVersion 与 readValidLiveIdeModelsSnapshot 在 Windows 下正常工作
  const ver = await readInstalledAppBundleVersion(sandbox.appRoot);
  assert.equal(ver, '2.34.0');

  const snapshotPath = join(sandbox.stateDir, 'live-ide-models.json');
  await writeFile(
    snapshotPath,
    JSON.stringify({
      schema: 2,
      appVersion: '2.34.0',
      tier: 'PRO',
      observedAt: 1700000000,
      models: [{ id: 'gemini-2.5-pro', displayName: 'Gemini 2.5 Pro', modelOrAlias: 'gemini-2.5-pro', disabled: false }],
    })
  );
  const validSnap = await readValidLiveIdeModelsSnapshot(snapshotPath, '2.34.0');
  assert.ok(validSnap);
  assert.equal(validSnap.appVersion, '2.34.0');
  assert.equal(validSnap.models.length, 1);
});
