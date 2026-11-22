import { mkdtemp, mkdir, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { createHash } from 'node:crypto';

/**
 * 创建带中文字符与空格路径的隔离测试沙箱（验证 NFR-008 全链路 CJK + 空格路径兼容性）。
 */
export async function createWinTestSandbox(prefix = 'gb-win-', options = {}) {
  const baseTmp = await mkdtemp(join(tmpdir(), prefix));
  const cjkSegment = options.cjkSegment ?? '测试 用户';
  const spaceSegment = options.spaceSegment ?? 'GravityBox 沙箱';
  const homeDir = join(baseTmp, cjkSegment);
  const localAppData = join(homeDir, 'AppData', 'Local');
  const roamingAppData = join(homeDir, 'AppData', 'Roaming');
  const stateDir = join(localAppData, spaceSegment);
  const cacheDir = join(stateDir, 'cache');
  const logDir = join(stateDir, 'logs');
  const trashDir = join(stateDir, 'maintenance-trash');
  const profileDir = join(roamingAppData, 'Antigravity');
  const geminiDir = join(homeDir, '.gemini');
  const appRoot = join(localAppData, 'Programs', 'Antigravity');

  await Promise.all([
    mkdir(stateDir, { recursive: true }),
    mkdir(cacheDir, { recursive: true }),
    mkdir(logDir, { recursive: true }),
    mkdir(trashDir, { recursive: true }),
    mkdir(join(profileDir, 'User', 'globalStorage'), { recursive: true }),
    mkdir(join(geminiDir, 'antigravity', 'conversations'), { recursive: true }),
    mkdir(join(geminiDir, 'antigravity', 'daemon'), { recursive: true }),
    mkdir(join(appRoot, 'resources', 'bin'), { recursive: true }),
  ]);

  if (options.seedOfficialFiles !== false) {
    await writeFile(join(appRoot, 'Antigravity.exe'), 'MZ-FAKE-OFFICIAL-EXE');
    await writeFile(join(appRoot, 'resources', 'bin', 'language_server.exe'), 'MZ-FAKE-OFFICIAL-LS');
  }

  const cleanup = async () => {
    await rm(baseTmp, { recursive: true, force: true, maxRetries: 10, retryDelay: 50 }).catch(() => {});
  };

  return {
    baseTmp,
    homeDir,
    localAppData,
    roamingAppData,
    stateDir,
    cacheDir,
    logDir,
    trashDir,
    profileDir,
    geminiDir,
    appRoot,
    cleanup,
  };
}

/**
 * 构造可注入的 Mock WinHelperClient，覆盖 02-design.md §4.2.2 的全部 14 个 RPC 方法。
 */
export function createMockWinHelperClient(overrides = {}) {
  const credentials = new Map();
  const shortcuts = new Map();
  const processes = [];
  const listeners = [];
  const envVars = new Map();
  const calls = [];

  const defaultHandlers = {
    async list_processes(params = {}) {
      const list = typeof overrides.processes === 'function' ? await overrides.processes(params) : processes;
      if (params.nameFilter) {
        const lower = String(params.nameFilter).toLowerCase();
        return { processes: list.filter(p => p.executablePath.toLowerCase().includes(lower)) };
      }
      return { processes: [...list] };
    },
    async is_process_alive(params = {}) {
      const hit = processes.find(p => p.pid === params.pid);
      if (!hit) return { alive: false };
      if (params.creationTimeMs !== undefined && hit.creationTimeMs !== undefined && hit.creationTimeMs !== params.creationTimeMs) {
        return { alive: false, creationTimeMs: hit.creationTimeMs };
      }
      return { alive: true, creationTimeMs: hit.creationTimeMs };
    },
    async read_process_env(params = {}) {
      const key = `${params.pid}:${params.name}`;
      return { value: envVars.get(key) ?? null };
    },
    async tcp_listeners(params = {}) {
      let filtered = [...listeners];
      if (params.port !== undefined) filtered = filtered.filter(l => l.localPort === params.port);
      if (params.pid !== undefined) filtered = filtered.filter(l => l.pid === params.pid);
      return { listeners: filtered };
    },
    async verify_authenticode(params = {}) {
      return {
        valid: true,
        status: 'Valid',
        subject: 'CN=Google LLC, O=Google LLC, L=Mountain View, S=California, C=US',
        issuer: 'CN=DigiCert Trusted G4 Code Signing RSA4096 SHA384 2021 CA1',
        thumbprint: 'A1B2C3D4E5F60718293A4B5C6D7E8F9012345678',
        fileVersion: '2.34.0',
        productName: 'Antigravity',
        companyName: 'Google',
        path: params.path,
      };
    },
    async cred_read(params = {}) {
      const entry = credentials.get(params.target);
      if (!entry) return { secret: null };
      return { secret: entry.secret, userName: entry.userName };
    },
    async cred_write(params = {}) {
      credentials.set(params.target, { secret: params.secret, userName: params.userName ?? 'antigravity' });
      return { ok: true };
    },
    async cred_delete(params = {}) {
      const existed = credentials.delete(params.target);
      return { ok: true, existed };
    },
    async acl_harden() {
      return { ok: true };
    },
    async acl_verify_owner() {
      return { ownedByCurrentUser: true, isReparsePoint: false, exists: true };
    },
    async shortcut_scan(params = {}) {
      const official = String(params.officialExePath ?? '').toLowerCase();
      const result = [];
      for (const item of shortcuts.values()) {
        if (!official || String(item.targetPath ?? '').toLowerCase() === official) {
          result.push({ ...item });
        }
      }
      return { shortcuts: result };
    },
    async shortcut_set_args(params = {}) {
      const existing = shortcuts.get(params.path);
      if (!existing) throw new Error(`Shortcut not found: ${params.path}`);
      existing.arguments = params.arguments ?? '';
      existing.hasDebugPort = /(?:^|\s)--remote-debugging-port(?:=\d+|\s+\d+)(?=\s|$)/i.test(existing.arguments);
      return { ok: true };
    },
    async recycle_delete() {
      return { ok: true };
    },
    async ui_languages() {
      return { locales: ['zh-CN', 'en-US'] };
    },
  };

  return {
    calls,
    credentials,
    shortcuts,
    processes,
    listeners,
    envVars,
    async request(method, params = {}) {
      calls.push({ method, params });
      if (typeof overrides[method] === 'function') {
        return await overrides[method](params, defaultHandlers);
      }
      const handler = defaultHandlers[method];
      if (!handler) throw new Error(`Unknown WinHelper RPC method: ${method}`);
      return await handler(params);
    },
    async close() {},
  };
}

/**
 * 计算 Windows Named Pipe 确定性端点名称（对齐 02-design.md §4.3.1）。
 */
export function computeExpectedWinPipeName(stateDir) {
  const normalized = resolve(stateDir).toLowerCase();
  const hash = createHash('sha256').update(normalized).digest('hex').slice(0, 24);
  return `\\\\.\\pipe\\gravitybox-${hash}`;
}
