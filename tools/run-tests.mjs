import { existsSync } from 'node:fs';
import { readdir, realpath } from 'node:fs/promises';
import { spawn } from 'node:child_process';
import { join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const root = fileURLToPath(new URL('../', import.meta.url));
export function selectTestFiles(entries, mode = 'all') {
  if (!['all', 'native', 'fast'].includes(mode)) throw new Error(`未知测试范围：${mode}`);
  return entries.filter(name => name.endsWith('.test.mjs'))
    .filter(name => mode === 'all' || name.startsWith('native-') === (mode === 'native'))
    .sort().map(name => join('test', name));
}

async function collectWinTestFiles(baseRoot) {
  const winDir = join(baseRoot, 'test', 'win');
  if (!existsSync(winDir)) return [];
  const winEntries = await readdir(winDir).catch(() => []);
  return winEntries
    .filter(name => name.endsWith('.test.mjs'))
    .sort()
    .map(name => join('test', 'win', name));
}

async function main() {
  const args = process.argv.slice(2);
  if (args.length > 1 || args.some(arg => !['--native', '--fast', '--win'].includes(arg))) {
    throw new Error('用法：node tools/run-tests.mjs [--native | --fast | --win]');
  }
  const mode = args[0]?.slice(2) ?? 'all';
  let selected;
  if (mode === 'win') {
    selected = await collectWinTestFiles(root);
  } else if (process.platform === 'win32' && (mode === 'all' || mode === 'fast')) {
    const baseEntries = await readdir(join(root, 'test'));
    const coreSelected = selectTestFiles(baseEntries, 'fast');
    const winSelected = await collectWinTestFiles(root);
    selected = [...coreSelected, ...winSelected];
  } else {
    selected = selectTestFiles(await readdir(join(root, 'test')), mode);
  }
  if (selected.length === 0) throw new Error(`未找到匹配的测试文件（mode=${mode}）`);

  const child = spawn(process.execPath, ['--test', ...selected], { cwd: root, stdio: 'inherit' });
  process.exitCode = await new Promise((resolve, reject) => {
    child.once('error', reject);
    child.once('exit', (code, signal) => {
      if (signal) reject(new Error(`测试被 ${signal} 中断`));
      else resolve(code ?? 1);
    });
  });
}

const entryPath = process.argv[1] ? await realpath(process.argv[1]).catch(() => null) : null;
if (entryPath && import.meta.url.toLowerCase() === pathToFileURL(entryPath).href.toLowerCase()) {
  main().catch(error => { console.error(error.message); process.exitCode = 1; });
}
