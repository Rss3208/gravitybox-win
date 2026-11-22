import test from 'node:test';
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import {
  clearDiscoveryCachesForTests,
  discover,
  inspectBundle,
  isApplicationRunning,
  resolveVerifiedLanguageServerPorts,
  verifyPageService,
} from '../../dist/discovery.js';
import { setPlatformForTests } from '../../dist/platform/index.js';
import { createWin32PlatformAdapter } from '../../dist/platform/win32.js';
import { createWinTestSandbox, createMockWinHelperClient } from './helpers.mjs';

function buildMinimalAsarBuffer(filesMap) {
  // Construct an unpacked-aware minimal Electron ASAR archive:
  // Header pickle: [4: size=4][4: headerStringSize][4: headerStringLen][4: alignedStringLen][headerJson]
  const payloadChunks = [];
  let offset = 0;

  function buildDirNode(obj) {
    const files = {};
    for (const [k, v] of Object.entries(obj)) {
      if (typeof v === 'string' || Buffer.isBuffer(v)) {
        const buf = Buffer.isBuffer(v) ? v : Buffer.from(v, 'utf8');
        files[k] = { size: buf.length, offset: String(offset) };
        payloadChunks.push(buf);
        offset += buf.length;
      } else if (v && typeof v === 'object') {
        files[k] = buildDirNode(v);
      }
    }
    return { files };
  }

  const headerObj = buildDirNode(filesMap);
  const headerJson = Buffer.from(JSON.stringify(headerObj), 'utf8');
  const strSize = headerJson.length;
  const alignedStrSize = (strSize + 3) & ~3;
  const picklePayloadSize = 4 + alignedStrSize;

  const headerBuf = Buffer.alloc(16 + alignedStrSize, 0);
  headerBuf.writeUInt32LE(4, 0);
  headerBuf.writeUInt32LE(picklePayloadSize + 4, 4);
  headerBuf.writeUInt32LE(picklePayloadSize, 8);
  headerBuf.writeUInt32LE(strSize, 12);
  headerJson.copy(headerBuf, 16);

  return Buffer.concat([headerBuf, ...payloadChunks]);
}

test('T-020: Windows inspectBundle 验真（Google 签名/官方 NotSigned PE + app.asar 校验、第三方伪造证书拒绝、旧版静态改包检测、缓存命中与失效）', async () => {
  const sandbox = await createWinTestSandbox('gb-win-disc-bundle-');
  const asarPath = join(sandbox.appRoot, 'resources', 'app.asar');

  await writeFile(
    asarPath,
    buildMinimalAsarBuffer({
      'package.json': JSON.stringify({ name: 'antigravity', productName: 'Antigravity', version: '2.34.0' }),
      dist: {
        'main.js': 'console.log("clean official bundle");',
      },
    }),
  );

  let authResponse = {
    ok: true,
    valid: true,
    status: 'Valid',
    subject: 'CN=Google LLC, O=Google LLC, L=Mountain View, S=California, C=US',
    issuer: 'CN=DigiCert Trusted G4 Code Signing RSA4096 SHA384 2021 CA1',
    thumbprint: 'GOOGLE123456',
    fileVersion: '2.34.0',
    productName: 'Antigravity',
    companyName: 'Google LLC',
  };
  let verifyCalls = 0;

  const mockHelper = createMockWinHelperClient({
    async verify_authenticode() {
      verifyCalls++;
      return authResponse;
    },
  });
  setPlatformForTests(createWin32PlatformAdapter({ helperClient: mockHelper }));
  clearDiscoveryCachesForTests();

  try {
    // 1. 合法 Google Authenticode 签名通过验真
    const info1 = await inspectBundle(sandbox.appRoot);
    assert.equal(info1.version, '2.34.0');
    assert.equal(info1.legacyPatch, false);
    assert.equal(verifyCalls, 1);

    // 2. 内存缓存命中：120s 内重复调用不再触发 WinHelper
    const infoCached = await inspectBundle(sandbox.appRoot);
    assert.equal(infoCached.version, '2.34.0');
    assert.equal(verifyCalls, 1);

    // 3. 清除缓存后测试第三方非 Google 签名拒绝 (FR-002)
    clearDiscoveryCachesForTests({ skipDiskLoad: true });
    authResponse = {
      ...authResponse,
      subject: 'CN=Malicious Corp, O=Malicious Corp, C=US',
    };
    await assert.rejects(inspectBundle(sandbox.appRoot), /签名/);

    // 4. 测试 CHK-02 实机形态：官方 NotSigned PE + Google VersionInfo + app.asar + language_server.exe 通过验真
    clearDiscoveryCachesForTests({ skipDiskLoad: true });
    authResponse = {
      ok: true,
      valid: false,
      status: 'NotSigned',
      subject: '',
      issuer: '',
      thumbprint: '',
      fileVersion: '2.34.0',
      productName: 'Antigravity',
      companyName: 'Google',
    };
    const infoNotSignedOfficial = await inspectBundle(sandbox.appRoot);
    assert.equal(infoNotSignedOfficial.version, '2.34.0');
    assert.equal(infoNotSignedOfficial.legacyPatch, false);

    // 5. 测试旧版改包（app.asar 含静态汉化补丁标记）检测为 legacyPatch: true
    clearDiscoveryCachesForTests({ skipDiskLoad: true });
    await writeFile(
      asarPath,
      buildMinimalAsarBuffer({
        'package.json': JSON.stringify({ name: 'antigravity', productName: 'Antigravity', version: '2.34.0' }),
        dist: {
          'main.js': '/* ANTIGRAVITY CHINESE LOCALIZATION START */ console.log("patched");',
        },
      }),
    );
    const infoPatched = await inspectBundle(sandbox.appRoot);
    assert.equal(infoPatched.legacyPatch, true);
  } finally {
    clearDiscoveryCachesForTests();
    setPlatformForTests(null);
    await sandbox.cleanup();
  }
});

test('T-020: Windows discover、verifyPageService、isApplicationRunning 与 resolveVerifiedLanguageServerPorts 端到端校验（含非回环 0.0.0.0 拦截）', async () => {
  const sandbox = await createWinTestSandbox('gb-win-disc-e2e-');
  const asarPath = join(sandbox.appRoot, 'resources', 'app.asar');
  const officialExe = join(sandbox.appRoot, 'Antigravity.exe');
  const officialLs = join(sandbox.appRoot, 'resources', 'bin', 'language_server.exe');

  await writeFile(
    asarPath,
    buildMinimalAsarBuffer({
      'package.json': JSON.stringify({ name: 'antigravity', productName: 'Antigravity', version: '2.34.0' }),
      dist: { 'main.js': 'console.log("ok");' },
    }),
  );

  // 启动模拟 CDP /json/version 的本机回环 HTTP 服务
  let cdpPort = 0;
  const server = createServer((req, res) => {
    if (req.url === '/json/version') {
      res.writeHead(200, { 'Content-Type': 'application/json' });
      res.end(
        JSON.stringify({
          Browser: 'Antigravity/2.34.0',
          webSocketDebuggerUrl: `ws://127.0.0.1:${cdpPort}/devtools/browser/win-test-id`,
        }),
      );
      return;
    }
    res.writeHead(404);
    res.end();
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  cdpPort = server.address().port;

  await writeFile(join(sandbox.profileDir, 'DevToolsActivePort'), `${cdpPort}\n/devtools/browser/win-test-id\n`, 'utf8');

  let currentListeners = [
    {
      pid: 7788,
      localAddress: '127.0.0.1',
      localPort: cdpPort,
      executablePath: officialExe,
    },
  ];

  const mockHelper = createMockWinHelperClient({
    async tcp_listeners(params) {
      if (params.port !== undefined) {
        return { listeners: currentListeners.filter(l => l.localPort === params.port) };
      }
      if (params.pid !== undefined) {
        return { listeners: currentListeners.filter(l => l.pid === params.pid) };
      }
      return { listeners: currentListeners };
    },
    async list_processes() {
      return {
        processes: [
          {
            pid: 7788,
            ppid: 1000,
            name: 'Antigravity.exe',
            executablePath: officialExe,
            commandLine: `"${officialExe}" --remote-debugging-port=0`,
            creationTimeMs: Date.now() - 5000,
          },
        ],
      };
    },
  });

  setPlatformForTests(createWin32PlatformAdapter({ helperClient: mockHelper }));
  clearDiscoveryCachesForTests();

  try {
    // 1. 正常回环 CDP 发现
    const endpoint = await discover(sandbox.appRoot, sandbox.profileDir);
    assert.ok(endpoint);
    assert.equal(endpoint.port, cdpPort);
    assert.equal(endpoint.pid, 7788);
    assert.equal(endpoint.appVersion, '2.34.0');
    assert.equal(endpoint.legacyPatch, false);

    // 2. isApplicationRunning 识别运行态
    assert.equal(await isApplicationRunning(sandbox.appRoot), true);

    // 3. verifyPageService 放行官方主程序或 language_server.exe 监听的回环页面，拒绝外部进程
    currentListeners.push({
      pid: 8899,
      localAddress: '127.0.0.1',
      localPort: 54321,
      executablePath: officialLs,
    });
    assert.equal(await verifyPageService('https://127.0.0.1:54321/', sandbox.appRoot), true);

    const lsPorts = await resolveVerifiedLanguageServerPorts(8899, sandbox.appRoot);
    assert.deepEqual(lsPorts, [54321]);

    // 4. 非回环监听地址（0.0.0.0）必须抛出 discovery.non_loopback 拦截 (INV-01 / NFR-004)
    clearDiscoveryCachesForTests({ skipDiskLoad: true });
    currentListeners = [
      {
        pid: 7788,
        localAddress: '0.0.0.0',
        localPort: cdpPort,
        executablePath: officialExe,
      },
    ];
    await assert.rejects(discover(sandbox.appRoot, sandbox.profileDir), /回环|本机|loopback/i);
  } finally {
    server.closeAllConnections?.();
    await new Promise(resolve => server.close(resolve));
    clearDiscoveryCachesForTests();
    setPlatformForTests(null);
    await sandbox.cleanup();
  }
});
