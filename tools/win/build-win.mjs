#!/usr/bin/env node
import { execFile } from 'node:child_process';
import { existsSync } from 'node:fs';
import { cp, mkdir, readFile, rm, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { promisify } from 'node:util';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { generateWindowsIcons } from './generate-win-icons.mjs';
import { includeDictionaryPath } from '../release-integrity.mjs';

const execFileAsync = promisify(execFile);
const root = fileURLToPath(new URL('../../', import.meta.url));

function resolveDotnetExecutable() {
  const localDotnet = process.env.LOCALAPPDATA
    ? join(process.env.LOCALAPPDATA, 'Microsoft', 'dotnet', 'dotnet.exe')
    : '';
  if (localDotnet && existsSync(localDotnet)) {
    return localDotnet;
  }
  return 'dotnet';
}

export async function buildWindowsStage({ selfContained = true, stageDir: customStageDir } = {}) {
  if (process.platform !== 'win32') {
    throw new Error('build-win.mjs must be run on Windows');
  }

  const pkg = JSON.parse(await readFile(join(root, 'package.json'), 'utf8'));
  let stageDir = customStageDir ?? process.env.GB_BUILD_STAGE_DIR ?? join(root, 'build', 'win-stage', 'GravityBox');
  try {
    await rm(stageDir, { recursive: true, force: true });
  } catch (err) {
    if (err?.code === 'EBUSY' || err?.code === 'EPERM') {
      stageDir = join(root, 'build', 'win-stage', `GravityBox-${Date.now()}`);
      console.log(`[build-win] Default stage dir is in use; staging into ${stageDir}`);
    } else {
      throw err;
    }
  }
  await mkdir(stageDir, { recursive: true });

  // 1. Build TypeScript backend
  console.log('[build-win] Building TypeScript backend...');
  await execFileAsync(process.execPath, [join(root, 'node_modules/typescript/bin/tsc')], { cwd: root });
  await execFileAsync(process.execPath, [join(root, 'tools/copy-runtime.mjs')], { cwd: root });

  // 2. Generate Windows icons
  console.log('[build-win] Generating Windows icons...');
  await generateWindowsIcons();

  // 3. Publish .NET 8 WPF App + WinHelper
  const dotnet = resolveDotnetExecutable();
  const publishArgsBase = [
    'publish',
    '-c', 'Release',
    '-r', 'win-x64',
    '--self-contained', selfContained ? 'true' : 'false',
    '-p:PublishSingleFile=false',
    '-o', stageDir,
  ];

  console.log('[build-win] Publishing GravityBox.WinHelper...');
  await execFileAsync(dotnet, [publishArgsBase[0], join(root, 'native-win/GravityBox.WinHelper/GravityBox.WinHelper.csproj'), ...publishArgsBase.slice(1)], { cwd: root });

  console.log('[build-win] Publishing GravityBox.App...');
  await execFileAsync(dotnet, [publishArgsBase[0], join(root, 'native-win/GravityBox.App/GravityBox.App.csproj'), ...publishArgsBase.slice(1)], { cwd: root });

  // 4. Copy Node.js runtime + backend core + dict + assets + licenses
  const runtimeDir = join(stageDir, 'runtime');
  await mkdir(runtimeDir, { recursive: true });
  await cp(process.execPath, join(runtimeDir, 'node.exe'));

  const coreDistDir = join(stageDir, 'core', 'dist');
  await mkdir(coreDistDir, { recursive: true });
  await cp(join(root, 'dist'), coreDistDir, { recursive: true });

  await cp(join(root, 'dict'), join(stageDir, 'dict'), {
    recursive: true,
    filter: src => includeDictionaryPath(src.replace(root, '').replaceAll('\\', '/').replace(/^\//, '')),
  });

  const stageAssets = join(stageDir, 'assets');
  await mkdir(stageAssets, { recursive: true });
  await cp(join(root, 'assets', 'i18n'), join(stageAssets, 'i18n'), { recursive: true });
  await cp(join(root, 'assets', 'AppIcon.ico'), join(stageAssets, 'AppIcon.ico'));
  await cp(join(root, 'assets', 'icons'), join(stageAssets, 'icons'), { recursive: true });
  for (const jsonAsset of ['model-catalog.json', 'pricing-feed.json', 'ui-extraction-manifest.json']) {
    const srcJson = join(root, 'assets', jsonAsset);
    if (existsSync(srcJson)) {
      await cp(srcJson, join(stageAssets, jsonAsset));
    }
  }

  // Node backend under core/dist/ resolves ../assets, ../dict, and ../package.json relative to core/
  await cp(join(root, 'package.json'), join(stageDir, 'core', 'package.json'));
  await cp(join(stageDir, 'dict'), join(stageDir, 'core', 'dict'), { recursive: true });
  await cp(stageAssets, join(stageDir, 'core', 'assets'), { recursive: true });

  for (const doc of ['LICENSE', 'THIRD_PARTY_NOTICES.md']) {
    const srcDoc = join(root, doc);
    if (existsSync(srcDoc)) {
      await cp(srcDoc, join(stageDir, doc));
    }
  }
  if (existsSync(join(root, 'licenses'))) {
    await cp(join(root, 'licenses'), join(stageDir, 'licenses'), { recursive: true });
  }

  await writeFile(
    join(stageDir, 'build-info.json'),
    JSON.stringify({
      version: pkg.version,
      platform: 'win32',
      arch: 'x64',
      selfContained,
      nodeVersion: process.version,
      builtAt: new Date().toISOString(),
    }, null, 2) + '\n',
    'utf8'
  );

  console.log(`[build-win] Stage ready: ${stageDir}`);
  return { version: pkg.version, stageDir };
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  buildWindowsStage().catch(err => {
    console.error(`[build-win] ${err.message}`);
    process.exitCode = 1;
  });
}
