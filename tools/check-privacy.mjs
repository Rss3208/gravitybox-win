import { execFile } from 'node:child_process';
import { createHash } from 'node:crypto';
import { existsSync } from 'node:fs';
import { readdir, readFile } from 'node:fs/promises';
import { extname, join, relative } from 'node:path';
import { promisify } from 'node:util';
import { fileURLToPath, pathToFileURL } from 'node:url';

const execFileAsync = promisify(execFile);
const root = fileURLToPath(new URL('../', import.meta.url));

/** 测试与文档允许使用的邮箱域名白名单（禁止真实私人域名邮箱入库）。 */
export const ALLOWED_EMAIL_DOMAINS = new Set([
  'example.invalid',
  'example.test',
  'example.com',
  'test.com',
  'test.example',
  'antigravity.test',
  'google.test',
  'gravity.test',
  'antigravity.dev',
  'users.noreply.github.com',
]);

/** 测试与系统公共目录允许使用的 /Users/<name> 占位用户名白名单。 */
export const ALLOWED_HOME_USERS = new Set([
  'alice',
  'bob',
  'charlie',
  'demo',
  'example',
  'fixture',
  'private',
  'sandbox',
  'shared',
  'temp',
  'test',
  'user',
]);
export const ALLOWED_MACOS_HOME_USERS = ALLOWED_HOME_USERS;

/** 敏感账号本地标识与全名 SHA-256 指纹（避免源码中明文出现）。 */
export const BANNED_TOKEN_HASHES = new Set([
  '654a64a2b7a73dff84d0077181edaf215876848c834b8af2ca0f146f8b3a93d3',
  '02e2d9c306b6a3516440a9554f6138fa5639308d98f0d601c0bf9e21f2ec7576',
  'cd4133c3f081dd3c655b93dea7db240cde873b67a48f2743d087eea0262bac1a',
  '939ca8a9ad878d9e2ded9c4eef8004d1552685aaa523f0f1720f36eca3a27fa2',
]);

export const BANNED_CJK_BIGRAM_HASHES = new Set([
  'bcdaac6d7ced9038d09b70760d68791ecb03e55caa2f51f894e6c7e6e5414efd',
]);

export const BANNED_QUOTED_ALIAS_HASHES = new Set([
  '8651f50cc42532e3543e36a01dc906cff39c5474ff8f6427f1902841910c717e',
]);

const BINARY_EXTENSIONS = new Set([
  '.png', '.jpg', '.jpeg', '.gif', '.webp', '.icns', '.ico',
  '.dmg', '.zip', '.tar', '.gz', '.pdf', '.woff', '.woff2',
  '.ttf', '.otf', '.node', '.sqlite', '.db', '.bin',
]);

const SKIPPED_DIRS = new Set([
  '.git', '.agents', 'node_modules', 'dist', 'build', '.cache',
  'vendor', '质检报告', '成品', 'reports', 'artifacts', 'obj', 'TestResults',
]);

const EMAIL_RE = /([A-Za-z0-9._%+-]+)@([A-Za-z0-9.-]+\.[A-Za-z]{2,})/g;
const RETINA_ASSET_RE = /^(?:1|2|3)x\.(?:png|jpe?g|webp|gif|svg)$/i;
const LOCAL_HOME_PATH_PATTERNS = [
  { label: '/Users', re: /(?:^|[^A-Za-z0-9_])\/Users\/([A-Za-z0-9._-]+)(?=\/|[^A-Za-z0-9._-]|$)/g, marker: '/Users/' },
  { label: '/home', re: /(?:^|[^A-Za-z0-9_])\/home\/([A-Za-z0-9._-]+)(?=\/|[^A-Za-z0-9._-]|$)/g, marker: '/home/' },
  { label: 'C:\\Users', re: /(?:^|[^A-Za-z0-9_])[A-Za-z]:(?:\\{1,2}|\/)Users(?:\\{1,2}|\/)([A-Za-z0-9._\-\u4e00-\u9fff]+)(?=\\|\/|[^A-Za-z0-9._\-\u4e00-\u9fff]|$)/gi, marker: 'Users' },
];
const SECRET_SYNTHETIC_EXEMPT_RE = /(?:abcde|secret|dummy|fake|example|test|placeholder|fixture)/i;
const SECRET_PATTERNS = [
  { name: '私钥头', re: /-----BEGIN (?:[A-Z0-9 ]+ )?PRIVATE KEY-----/g, allowSynthetic: false },
  { name: 'Google API Key', re: /\bAIza[0-9A-Za-z\-_]{35}\b/g, allowSynthetic: true },
  { name: 'Google OAuth Token', re: /\bya29\.[0-9A-Za-z\-_.]{24,}\b/g, allowSynthetic: true },
  { name: 'GitHub Token', re: /\b(?:gh[pousr]_[0-9A-Za-z]{20,}|github_pat_[0-9A-Za-z_]{30,})\b/g, allowSynthetic: true },
  { name: 'API/Webhook Token', re: /\b(?:(?:sk-proj-|sk-ant-|sk-live-)[0-9A-Za-z\-_]{16,}|xox[baprs]-[0-9A-Za-z-]{16,}|AKIA[0-9A-Z]{16})\b/g, allowSynthetic: true },
];
const ASCII_TOKEN_RE = /[A-Za-z0-9]+/g;
const CJK_RUN_RE = /[\u4e00-\u9fff]{2,}/g;
const QUOTED_LITERAL_RE = /(["'`])([^"'`\r\n]{1,64})\1/g;

function sha256Hex(text) {
  return createHash('sha256').update(text).digest('hex');
}

export function isExcludedPrivacyPath(filePath) {
  const rel = filePath.replaceAll('\\', '/').replace(/^\.\//, '');
  if (!rel) return true;
  if (rel === 'AGENTS.md' || rel.startsWith('licenses/')) return true;
  const segments = rel.split('/');
  if (segments.some(seg => SKIPPED_DIRS.has(seg))) return true;
  if (rel.startsWith('native-win/') && (segments.includes('bin') || segments.includes('obj'))) return true;
  if (BINARY_EXTENSIONS.has(extname(rel).toLowerCase())) return true;
  return false;
}

function lineNumberAt(content, index) {
  let line = 1;
  for (let i = 0; i < index; i++) {
    if (content.charCodeAt(i) === 10) line++;
  }
  return line;
}

function redactLocalPart(localPart) {
  if (localPart.length <= 2) return '*'.repeat(localPart.length);
  return `${localPart[0]}***${localPart[localPart.length - 1]}`;
}

/**
 * 扫描单个文本文件内容是否包含非白名单邮箱或敏感标识指纹。
 */
export function scanContentForPrivacy(content, filePath, options = {}) {
  const rel = filePath.replaceAll('\\', '/').replace(/^\.\//, '');
  if (isExcludedPrivacyPath(rel) || !content || content.includes('\0')) return [];

  const allowedDomains = options.allowedDomains ?? ALLOWED_EMAIL_DOMAINS;
  const allowedHomeUsers = options.allowedHomeUsers ?? ALLOWED_MACOS_HOME_USERS;
  const tokenHashes = options.tokenHashes ?? BANNED_TOKEN_HASHES;
  const cjkBigramHashes = options.cjkBigramHashes ?? BANNED_CJK_BIGRAM_HASHES;
  const quotedAliasHashes = options.quotedAliasHashes ?? BANNED_QUOTED_ALIAS_HASHES;
  const issues = [];

  for (const match of content.matchAll(EMAIL_RE)) {
    const [, localPart, domain] = match;
    if (RETINA_ASSET_RE.test(domain)) continue;
    const normalizedDomain = domain.toLowerCase();
    if (!allowedDomains.has(normalizedDomain)) {
      issues.push({
        code: 'DISALLOWED_EMAIL_DOMAIN',
        file: rel,
        line: lineNumberAt(content, match.index ?? 0),
        detail: `发现非测试占位邮箱 ${redactLocalPart(localPart)}@${normalizedDomain}（请改用 *.example.invalid 或 *.example.test）`,
      });
    }
  }

  for (const { label, re, marker } of LOCAL_HOME_PATH_PATTERNS) {
    for (const match of content.matchAll(re)) {
      const username = match[1];
      if (!allowedHomeUsers.has(username.toLowerCase())) {
        const markerOffset = match[0].toLowerCase().indexOf(marker.toLowerCase());
        const pos = (match.index ?? 0) + Math.max(0, markerOffset);
        issues.push({
          code: 'DISALLOWED_LOCAL_HOME_PATH',
          file: rel,
          line: lineNumberAt(content, pos),
          detail: `发现疑似本机用户目录路径 ${label}/${redactLocalPart(username)}（请改用相对路径或 /Users/demo、/Users/alice 等占位路径）`,
        });
      }
    }
  }

  for (const { name, re, allowSynthetic } of SECRET_PATTERNS) {
    for (const match of content.matchAll(re)) {
      const raw = match[0];
      if (allowSynthetic && SECRET_SYNTHETIC_EXEMPT_RE.test(raw)) continue;
      issues.push({
        code: 'DISALLOWED_SECRET_LITERAL',
        file: rel,
        line: lineNumberAt(content, match.index ?? 0),
        detail: `发现疑似明文 ${name} 凭据（请改用环境变量或显式测试占位串）`,
      });
    }
  }

  const asciiTokens = [];
  for (const match of content.matchAll(ASCII_TOKEN_RE)) {
    const lower = match[0].toLowerCase();
    const index = match.index ?? 0;
    if (tokenHashes.has(sha256Hex(lower))) {
      issues.push({
        code: 'SENSITIVE_TOKEN_MATCH',
        file: rel,
        line: lineNumberAt(content, index),
        detail: '命中已禁止提交的个人敏感标识指纹',
      });
    }
    asciiTokens.push({ lower, index });
    if (asciiTokens.length >= 3) {
      const a = asciiTokens[asciiTokens.length - 3];
      const b = asciiTokens[asciiTokens.length - 2];
      const c = asciiTokens[asciiTokens.length - 1];
      if (tokenHashes.has(sha256Hex(`${a.lower} ${b.lower} ${c.lower}`))) {
        issues.push({
          code: 'SENSITIVE_NAME_MATCH',
          file: rel,
          line: lineNumberAt(content, a.index),
          detail: '命中已禁止提交的个人姓名指纹',
        });
      }
    }
  }

  for (const match of content.matchAll(CJK_RUN_RE)) {
    const run = match[0];
    const baseIndex = match.index ?? 0;
    for (let i = 0; i <= run.length - 2; i++) {
      const bigram = run.slice(i, i + 2);
      if (cjkBigramHashes.has(sha256Hex(bigram))) {
        issues.push({
          code: 'SENSITIVE_CJK_NAME_MATCH',
          file: rel,
          line: lineNumberAt(content, baseIndex + i),
          detail: '命中已禁止提交的个人中文姓名指纹',
        });
      }
    }
  }

  if (!rel.startsWith('dict/') && !rel.startsWith('ui-strings/')) {
    for (const match of content.matchAll(QUOTED_LITERAL_RE)) {
      const literal = match[2].trim();
      if (literal && quotedAliasHashes.has(sha256Hex(literal))) {
        issues.push({
          code: 'SENSITIVE_ALIAS_MATCH',
          file: rel,
          line: lineNumberAt(content, match.index ?? 0),
          detail: '命中已禁止提交的个人账号别名指纹',
        });
      }
    }
  }

  issues.sort((a, b) => a.line - b.line);
  return issues;
}

async function listGitFiles(repoRoot, args) {
  const { stdout } = await execFileAsync('git', args, { cwd: repoRoot, maxBuffer: 16 * 1024 * 1024 });
  return stdout.split('\0').filter(Boolean);
}

async function walkDirectory(dir, baseRoot) {
  const collected = [];
  for (const entry of await readdir(dir, { withFileTypes: true })) {
    const fullPath = join(dir, entry.name);
    const rel = relative(baseRoot, fullPath).replaceAll('\\', '/');
    if (isExcludedPrivacyPath(rel)) continue;
    if (entry.isDirectory()) {
      collected.push(...await walkDirectory(fullPath, baseRoot));
    } else if (entry.isFile()) {
      collected.push(rel);
    }
  }
  return collected;
}

export async function runPrivacyCheck({ repoRoot = root, staged = false } = {}) {
  const issues = [];
  let scannedCount = 0;

  if (staged) {
    const stagedFiles = (await listGitFiles(repoRoot, ['diff', '--cached', '--name-only', '--diff-filter=ACMR', '-z']))
      .filter(file => !isExcludedPrivacyPath(file));
    for (const rel of stagedFiles) {
      let content = '';
      try {
        const { stdout } = await execFileAsync('git', ['show', `:${rel}`], {
          cwd: repoRoot,
          maxBuffer: 16 * 1024 * 1024,
        });
        content = stdout;
      } catch {
        continue;
      }
      scannedCount++;
      issues.push(...scanContentForPrivacy(content, rel));
    }
    return { scannedCount, issues };
  }

  let files;
  if (existsSync(join(repoRoot, '.git'))) {
    const tracked = await listGitFiles(repoRoot, ['ls-files', '-z']);
    const untracked = await listGitFiles(repoRoot, ['ls-files', '--others', '--exclude-standard', '-z']);
    files = [...new Set([...tracked, ...untracked])].filter(file => !isExcludedPrivacyPath(file));
  } else {
    files = await walkDirectory(repoRoot, repoRoot);
  }

  for (const rel of files) {
    let content;
    try {
      content = await readFile(join(repoRoot, rel), 'utf8');
    } catch {
      continue;
    }
    scannedCount++;
    issues.push(...scanContentForPrivacy(content, rel));
  }

  return { scannedCount, issues };
}

/**
 * 扫描指定目录（如已打包的 GravityBox.app/Contents）内的非二进制文件是否存在隐私或凭据泄露。
 */
export async function scanDirectoryForPrivacy(targetDir, { excludePrefixes = [], options = {} } = {}) {
  const issues = [];
  let scannedCount = 0;
  const files = await walkDirectory(targetDir, targetDir);
  for (const rel of files) {
    if (excludePrefixes.some(prefix => rel.startsWith(prefix))) continue;
    let content;
    try {
      content = await readFile(join(targetDir, rel), 'utf8');
    } catch {
      continue;
    }
    scannedCount++;
    issues.push(...scanContentForPrivacy(content, rel, options));
  }
  return { scannedCount, issues };
}

async function main() {
  const staged = process.argv.includes('--staged');
  const result = await runPrivacyCheck({ repoRoot: root, staged });
  if (result.issues.length > 0) {
    process.stderr.write(`❌ [privacy-gate] 检测到 ${result.issues.length} 处疑似真实邮箱或个人敏感标识（已扫描 ${result.scannedCount} 个文件）：\n`);
    for (const issue of result.issues) {
      process.stderr.write(`  - ${issue.file}:${issue.line} [${issue.code}] ${issue.detail}\n`);
    }
    process.exitCode = 1;
    return;
  }
  process.stdout.write(`✅ [privacy-gate] 隐私检查通过（已扫描 ${result.scannedCount} 个${staged ? '暂存' : '仓库'}文件，未发现真实邮箱或敏感标识）\n`);
}

if (typeof process.argv[1] === 'string' && import.meta.url === pathToFileURL(process.argv[1]).href) {
  main().catch(error => {
    process.stderr.write(`[privacy-gate] 检查异常: ${error?.message || String(error)}\n`);
    process.exitCode = 1;
  });
}
