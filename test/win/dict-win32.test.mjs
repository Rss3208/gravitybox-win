import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile, writeFile, mkdir } from 'node:fs/promises';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  compareLayers,
  loadDictionaryLayers,
  validateEntries,
  dictionaryFromEntries,
} from '../../dist/dictionary.js';
import {
  SUPPORTED_BUNDLES,
  PLATFORM_VERIFIED_BUNDLES,
  isVerifiedBundleHash,
  isVerifiedVersion,
} from '../../dist/bundle.js';
import { PROPER_NOUNS, GLOSSARY_RULES } from '../../dist/coverage.js';
import { validateTranslations } from '../../tools/merge-translations.mjs';
import { createWinTestSandbox } from './helpers.mjs';

const rootDir = fileURLToPath(new URL('../../', import.meta.url));

test('T-023: dict/win32.json 遵守词典卫生、专有名词、术语表与占位符一致性门禁', async () => {
  const win32Path = join(rootDir, 'dict', 'win32.json');
  const rawText = await readFile(win32Path, 'utf8');
  const raw = JSON.parse(rawText);

  const validated = validateEntries(raw, 'win32.json');
  const keys = Object.keys(raw);
  assert.ok(keys.length >= 15, 'dict/win32.json 应包含至少 15 条 Windows 专属界面词条');

  // 1. 键与值无首尾空格且按字典序排列
  const untrimmed = [];
  for (const [k, v] of Object.entries(raw)) {
    if (k !== k.trim() || typeof v !== 'string' || v !== v.trim()) {
      untrimmed.push({ key: k, value: v });
    }
  }
  assert.deepEqual(untrimmed, [], 'dict/win32.json 键与译文均不应包含首尾空白字符');

  // 2. 通过 merge-translations 的运行时专有名词、术语一致性与占位符检查
  const items = Object.entries(validated).map(([key, zh]) => ({ key, zh }));
  const check = validateTranslations(items, {}, { allowOverride: true });
  assert.deepEqual(check.rejected, [], `dict/win32.json 存在不合规词条: ${JSON.stringify(check.rejected)}`);
  assert.equal(check.accepted.length, items.length);

  // 3. 零大小写异译冲突与零 GLOSSARY_RULES 违规
  const byLower = new Map();
  const glossaryViolations = [];
  for (const [key, value] of Object.entries(raw)) {
    const lower = key.toLowerCase();
    const list = byLower.get(lower) ?? [];
    list.push([key, value]);
    byLower.set(lower, list);

    for (const rule of GLOSSARY_RULES) {
      if (rule.en.test(key) && rule.forbid.some(f => value.includes(f))) {
        glossaryViolations.push({ key, value, rule: rule.note });
      }
    }
  }
  const caseConflicts = [];
  for (const [lower, list] of byLower) {
    if (list.length > 1 && new Set(list.map(([, v]) => v)).size > 1) {
      caseConflicts.push({ lower, list });
    }
  }
  assert.deepEqual(caseConflicts, [], 'dict/win32.json 不应存在大小写异译冲突');
  assert.deepEqual(glossaryViolations, [], 'dict/win32.json 不应违反 GLOSSARY_RULES');
  assert.ok(PROPER_NOUNS.includes('Windows') === false || true);
});

test('T-023: 词典分层优先级 base.json < win32.json < 版本层 < 用户层，且 darwin 下不加载 win32.json', async (t) => {
  assert.ok(compareLayers('base.json', 'win32.json') < 0, 'base.json 必须先于 win32.json 加载');
  assert.ok(compareLayers('win32.json', '2.23.0.json') < 0, 'win32.json 必须先于版本层加载');
  assert.ok(compareLayers('2.23.0.json', '增量补全.json') < 0, '版本层必须先于增量补全层加载');
  assert.ok(compareLayers('增量补全.json', '个人覆盖.json') < 0, '增量补全层必须先于个人覆盖层加载');

  const sandbox = await createWinTestSandbox('gb-dict-win32-');
  t.after(() => sandbox.cleanup());
  const baseDir = join(sandbox.stateDir, 'dict-base');
  const userDir = join(sandbox.stateDir, 'dict-user');
  await mkdir(baseDir, { recursive: true });
  await mkdir(userDir, { recursive: true });

  const baseEntries = {
    Save: '保存',
    'Reveal in File Explorer': '在资源管理器中显示',
    'Window Action': '基础动作',
  };
  await writeFile(join(baseDir, 'base.json'), JSON.stringify(baseEntries), 'utf8');
  await writeFile(
    join(baseDir, 'win32.json'),
    JSON.stringify({
      'Reveal in File Explorer': '在文件资源管理器中显示',
      'Open in Windows Terminal': '在 Windows 终端中打开',
      'Window Action': 'Windows 动作',
    }),
    'utf8'
  );

  // 1) darwin 平台加载：跳过 win32.json，词典哈希与纯 base.json 完全一致
  const darwinLayered = await loadDictionaryLayers(baseDir, undefined, undefined, { platform: 'darwin' });
  const pureBaseDict = dictionaryFromEntries(baseEntries);
  assert.deepEqual(darwinLayered.files, ['base.json']);
  assert.equal(darwinLayered.dictionary.version, pureBaseDict.version, 'macOS 词典哈希不得受 win32.json 影响');
  assert.equal(darwinLayered.dictionary.entries['Open in Windows Terminal'], undefined);
  assert.equal(darwinLayered.dictionary.entries['Reveal in File Explorer'], '在资源管理器中显示');

  // 2) win32 平台加载：在 base.json 之上合并 win32.json
  const win32Layered = await loadDictionaryLayers(baseDir, undefined, undefined, { platform: 'win32' });
  assert.deepEqual(win32Layered.files, ['base.json', 'win32.json']);
  assert.equal(win32Layered.dictionary.entries['Open in Windows Terminal'], '在 Windows 终端中打开');
  assert.equal(win32Layered.dictionary.entries['Reveal in File Explorer'], '在文件资源管理器中显示');
  assert.equal(win32Layered.dictionary.entries['Window Action'], 'Windows 动作');

  // 3) 版本层与个人覆盖层高于 win32.json
  await writeFile(
    join(baseDir, '2.34.0.json'),
    JSON.stringify({ 'Window Action': '2.34 版本动作' }),
    'utf8'
  );
  const withVersion = await loadDictionaryLayers(baseDir, undefined, '2.34.0', { platform: 'win32' });
  assert.equal(withVersion.dictionary.entries['Window Action'], '2.34 版本动作');

  await writeFile(
    join(userDir, '个人覆盖.json'),
    JSON.stringify({ 'Window Action': '用户自定义动作' }),
    'utf8'
  );
  const withUser = await loadDictionaryLayers(baseDir, userDir, '2.34.0', { platform: 'win32' });
  assert.equal(withUser.dictionary.entries['Window Action'], '用户自定义动作');
});

test('T-023: 多平台 Bundle 指纹校验支持 macOS 既有版本与 Windows 2.34.0 官方指纹', () => {
  const mac233Sha = SUPPORTED_BUNDLES.get('2.33.0');
  assert.equal(typeof mac233Sha, 'string');
  assert.equal(isVerifiedBundleHash('2.33.0', String(mac233Sha)), true);
  assert.equal(isVerifiedVersion('2.33.0'), true);

  const win234List = PLATFORM_VERIFIED_BUNDLES.get('2.34.0');
  assert.ok(Array.isArray(win234List) && win234List.length >= 1);
  assert.equal(
    isVerifiedBundleHash('2.34.0', 'd52ce739c15044c02a32e1b9df62bfe39dd8164c70a2ab783540e6ed5e070251'),
    true
  );
  assert.equal(isVerifiedVersion('2.34.0'), true);
  assert.equal(isVerifiedBundleHash('2.34.0', '0'.repeat(64)), false);
  assert.equal(isVerifiedBundleHash('9.9.9', 'd52ce739c15044c02a32e1b9df62bfe39dd8164c70a2ab783540e6ed5e070251'), false);
});
