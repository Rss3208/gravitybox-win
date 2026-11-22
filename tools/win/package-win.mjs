#!/usr/bin/env node
import { execFile } from 'node:child_process';
import { existsSync } from 'node:fs';
import { mkdir, rm } from 'node:fs/promises';
import { join } from 'node:path';
import { promisify } from 'node:util';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { buildWindowsStage } from './build-win.mjs';
import { writeSha256Sidecar, verifySha256Sidecar } from '../release-integrity.mjs';

const execFileAsync = promisify(execFile);
const root = fileURLToPath(new URL('../../', import.meta.url));

function findInnoSetupCompiler() {
  const candidates = [
    process.env.ISCC_EXE,
    process.env['ProgramFiles(x86)'] ? join(process.env['ProgramFiles(x86)'], 'Inno Setup 6', 'ISCC.exe') : '',
    process.env.ProgramFiles ? join(process.env.ProgramFiles, 'Inno Setup 6', 'ISCC.exe') : '',
    process.env.LOCALAPPDATA ? join(process.env.LOCALAPPDATA, 'Programs', 'Inno Setup 6', 'ISCC.exe') : '',
  ].filter(Boolean);
  return candidates.find(p => existsSync(p));
}

export async function packageWindows() {
  const { version, stageDir } = await buildWindowsStage({ selfContained: true });
  const outDir = join(root, '成品');
  await mkdir(outDir, { recursive: true });

  // 1. Portable ZIP
  const zipName = `GravityBox-${version}-win-x64-portable.zip`;
  const zipPath = join(outDir, zipName);
  console.log(`[package-win] Creating portable archive ${zipName}...`);
  await rm(zipPath, { force: true });
  await execFileAsync(
    'powershell.exe',
    [
      '-NoProfile',
      '-NonInteractive',
      '-Command',
      `Add-Type -AssemblyName System.IO.Compression.FileSystem; [System.IO.Compression.ZipFile]::CreateFromDirectory($env:GB_STAGE_DIR, $env:GB_ZIP_PATH, [System.IO.Compression.CompressionLevel]::Optimal, $false)`,
    ],
    {
      cwd: root,
      windowsHide: true,
      env: { ...process.env, GB_STAGE_DIR: stageDir, GB_ZIP_PATH: zipPath },
    }
  );
  await writeSha256Sidecar(zipPath);
  await verifySha256Sidecar(zipPath);
  console.log(`[package-win] Verified ${zipName}.sha256`);

  // 2. Inno Setup Installer (if ISCC.exe available)
  const iscc = findInnoSetupCompiler();
  if (iscc) {
    const issPath = join(root, 'tools', 'win', 'installer.iss');
    console.log(`[package-win] Compiling Inno Setup installer via ${iscc}...`);
    await execFileAsync(iscc, [`/DAppVersion=${version}`, issPath], { cwd: root });
    const setupPath = join(outDir, `GravityBox-${version}-win-x64-setup.exe`);
    if (existsSync(setupPath)) {
      await writeSha256Sidecar(setupPath);
      await verifySha256Sidecar(setupPath);
      console.log(`[package-win] Verified GravityBox-${version}-win-x64-setup.exe.sha256`);
    }
  } else {
    console.log('[package-win] Inno Setup (ISCC.exe) not found locally; skipped setup.exe compilation (portable.zip ready).');
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  packageWindows().catch(err => {
    console.error(`[package-win] ${err.message}`);
    process.exitCode = 1;
  });
}
