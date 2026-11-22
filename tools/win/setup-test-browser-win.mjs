import { existsSync } from 'node:fs';
import { appendFile } from 'node:fs/promises';
import { join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const root = fileURLToPath(new URL('../../', import.meta.url));

export function getWin32BrowserCandidates() {
  const candidates = [
    process.env.CHROME_BIN,
    process.env.CHROME_PATH,
    process.env.PUPPETEER_EXECUTABLE_PATH,
    join(root, '.cache', 'test-chrome-win64', 'chrome-headless-shell.exe'),
    join(root, '.cache', 'test-chrome-win64', 'chrome.exe'),
    process.env.ProgramFiles ? join(process.env.ProgramFiles, 'Google', 'Chrome', 'Application', 'chrome.exe') : '',
    process.env['ProgramFiles(x86)'] ? join(process.env['ProgramFiles(x86)'], 'Google', 'Chrome', 'Application', 'chrome.exe') : '',
    process.env.LOCALAPPDATA ? join(process.env.LOCALAPPDATA, 'Google', 'Chrome', 'Application', 'chrome.exe') : '',
    process.env['ProgramFiles(x86)'] ? join(process.env['ProgramFiles(x86)'], 'Microsoft', 'Edge', 'Application', 'msedge.exe') : '',
    process.env.ProgramFiles ? join(process.env.ProgramFiles, 'Microsoft', 'Edge', 'Application', 'msedge.exe') : '',
  ].filter(Boolean);
  return [...new Set(candidates)];
}

export function resolveWin32TestBrowserPath() {
  for (const candidate of getWin32BrowserCandidates()) {
    if (existsSync(candidate)) {
      return candidate;
    }
  }
  return undefined;
}

export async function setupWin32TestBrowser() {
  const resolved = resolveWin32TestBrowserPath();
  if (!resolved) {
    throw new Error(
      '未在 Windows 上检测到 Chrome 或 Microsoft Edge。请安装 Chrome/Edge 或通过 CHROME_BIN 指定可执行文件路径。'
    );
  }
  if (process.env.GITHUB_ENV) {
    await appendFile(process.env.GITHUB_ENV, `CHROME_BIN=${resolved}\n`);
  }
  console.log(`Windows 测试浏览器已就绪：${resolved}`);
  return resolved;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  setupWin32TestBrowser().catch(error => {
    console.error(`[test-browser-win] ${error.message}`);
    process.exitCode = 1;
  });
}
