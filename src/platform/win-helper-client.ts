import { spawn, type ChildProcessWithoutNullStreams } from 'node:child_process';
import { existsSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { StringDecoder } from 'node:string_decoder';
import { fileURLToPath } from 'node:url';
import type { WinHelperClientLike } from './types.js';

export interface WinHelperClientOptions {
  executablePath?: string;
  idleTimeoutMs?: number;
  defaultRequestTimeoutMs?: number;
}

interface PendingRpc {
  resolve: (value: any) => void;
  reject: (reason: Error) => void;
  timer: NodeJS.Timeout;
}

export function resolveDefaultWinHelperPath(): string | null {
  const envOverride = process.env.GRAVITYBOX_WINHELPER_PATH?.trim();
  if (envOverride && existsSync(envOverride)) {
    return resolve(envOverride);
  }

  const moduleDir = dirname(fileURLToPath(import.meta.url));
  const repoRoot = resolve(moduleDir, '..', '..');

  const candidates = [
    // Packaged layout: adjacent to backend or parent app root
    join(repoRoot, 'GravityBox.WinHelper.exe'),
    join(repoRoot, '..', 'GravityBox.WinHelper.exe'),
    join(dirname(process.execPath), 'GravityBox.WinHelper.exe'),
    // Development build layout: native-win/GravityBox.WinHelper/bin/{Release,Debug}/net8.0-windows10.0.19041.0/
    join(
      repoRoot,
      'native-win',
      'GravityBox.WinHelper',
      'bin',
      'Release',
      'net8.0-windows10.0.19041.0',
      'GravityBox.WinHelper.exe',
    ),
    join(
      repoRoot,
      'native-win',
      'GravityBox.WinHelper',
      'bin',
      'Release',
      'net8.0-windows10.0.19041.0',
      'win-x64',
      'GravityBox.WinHelper.exe',
    ),
    join(
      repoRoot,
      'native-win',
      'GravityBox.WinHelper',
      'bin',
      'Debug',
      'net8.0-windows10.0.19041.0',
      'GravityBox.WinHelper.exe',
    ),
  ];

  for (const candidate of candidates) {
    if (existsSync(candidate)) {
      return resolve(candidate);
    }
  }
  return null;
}

/**
 * Node 侧 `GravityBox.WinHelper.exe` 持久/按需 stdio NDJSON RPC 客户端（02-design.md §4.2）。
 * - 按需拉起无窗口子进程，复用单进程管道实现 <5ms 暖调用
 * - 并发请求按 `id` 关联，内置单请求超时保护
 * - 子进程异常退出或崩溃后下一请求自动重建
 * - 空闲 60s（可配置）自动关闭回收
 */
export class WinHelperClient implements WinHelperClientLike {
  private child: ChildProcessWithoutNullStreams | null = null;
  private readonly pending = new Map<string, PendingRpc>();
  private nextId = 1;
  private stdoutBuffer = '';
  private decoder = new StringDecoder('utf8');
  private idleTimer: NodeJS.Timeout | null = null;
  private closedExplicitly = false;
  private readonly idleTimeoutMs: number;
  private readonly defaultRequestTimeoutMs: number;
  private readonly customExePath?: string;

  constructor(options: WinHelperClientOptions = {}) {
    this.customExePath = options.executablePath;
    this.idleTimeoutMs = options.idleTimeoutMs ?? 60_000;
    this.defaultRequestTimeoutMs = options.defaultRequestTimeoutMs ?? 6_000;
  }

  public isAvailable(): boolean {
    if (process.platform !== 'win32') return false;
    const exe = this.customExePath ?? resolveDefaultWinHelperPath();
    return Boolean(exe && existsSync(exe));
  }

  public getPid(): number | null {
    return this.child?.pid ?? null;
  }

  public async request<T = any>(
    method: string,
    params: Record<string, unknown> = {},
    timeoutMs = this.defaultRequestTimeoutMs,
  ): Promise<T> {
    if (this.closedExplicitly) {
      this.closedExplicitly = false;
    }

    const child = this.ensureProcess();
    this.refreshIdleTimer();

    const id = String(this.nextId++);
    const payload = JSON.stringify({ id, method, params }) + '\n';

    return new Promise<T>((resolveReq, rejectReq) => {
      const timer = setTimeout(() => {
        this.pending.delete(id);
        rejectReq(new Error(`WinHelper RPC timeout (${method}, ${timeoutMs}ms)`));
      }, timeoutMs);
      timer.unref?.();

      this.pending.set(id, {
        resolve: resolveReq,
        reject: rejectReq,
        timer,
      });
      this.syncChildRef();

      try {
        child.stdin.write(payload, 'utf8', err => {
          if (err) {
            const entry = this.pending.get(id);
            if (entry) {
              clearTimeout(entry.timer);
              this.pending.delete(id);
              entry.reject(err);
            }
          }
        });
      } catch (err) {
        clearTimeout(timer);
        this.pending.delete(id);
        rejectReq(err instanceof Error ? err : new Error(String(err)));
      }
    });
  }

  public async close(): Promise<void> {
    this.closedExplicitly = true;
    this.clearIdleTimer();
    const current = this.child;
    this.child = null;
    this.rejectAllPending(new Error('WinHelper client closed'));
    if (!current) return;

    await new Promise<void>(resolveClose => {
      const killTimer = setTimeout(() => {
        try {
          current.kill();
        } catch {}
        resolveClose();
      }, 800);
      killTimer.unref?.();

      current.once('close', () => {
        clearTimeout(killTimer);
        resolveClose();
      });

      try {
        current.stdin.end();
      } catch {
        clearTimeout(killTimer);
        try {
          current.kill();
        } catch {}
        resolveClose();
      }
    });
  }

  private ensureProcess(): ChildProcessWithoutNullStreams {
    if (this.child && this.child.exitCode === null && !this.child.killed) {
      return this.child;
    }

    const exePath = this.customExePath ?? resolveDefaultWinHelperPath();
    if (!exePath || !existsSync(exePath)) {
      throw new Error('GravityBox.WinHelper.exe not found');
    }

    this.stdoutBuffer = '';
    this.decoder = new StringDecoder('utf8');

    const proc = spawn(exePath, [], {
      stdio: ['pipe', 'pipe', 'pipe'],
      windowsHide: true,
    });

    this.child = proc;
    this.syncChildRef();

    proc.stdout.on('data', (chunk: Buffer) => {
      this.stdoutBuffer += this.decoder.write(chunk);
      let newlineIdx: number;
      while ((newlineIdx = this.stdoutBuffer.indexOf('\n')) >= 0) {
        const line = this.stdoutBuffer.slice(0, newlineIdx).trim();
        this.stdoutBuffer = this.stdoutBuffer.slice(newlineIdx + 1);
        if (!line) continue;
        this.handleResponseLine(line);
      }
    });

    proc.stderr.on('data', () => {
      // Diagnostic stderr from WinHelper is consumed silently to prevent pipe backpressure.
    });

    const onDisconnect = (err?: Error) => {
      if (this.child === proc) {
        this.child = null;
      }
      this.rejectAllPending(err ?? new Error('WinHelper process exited unexpectedly'));
    };

    proc.once('error', err => onDisconnect(err));
    proc.once('close', () => onDisconnect());

    return proc;
  }

  private handleResponseLine(line: string): void {
    let parsed: any;
    try {
      parsed = JSON.parse(line);
    } catch {
      return;
    }

    const id = parsed?.id !== undefined && parsed?.id !== null ? String(parsed.id) : '';
    if (!id) return;

    const entry = this.pending.get(id);
    if (!entry) return;
    clearTimeout(entry.timer);
    this.pending.delete(id);
    this.syncChildRef();
    this.refreshIdleTimer();

    if (parsed.ok === false) {
      entry.reject(new Error(typeof parsed.error === 'string' ? parsed.error : 'WinHelper RPC error'));
    } else {
      entry.resolve(parsed);
    }
  }

  private rejectAllPending(error: Error): void {
    for (const [, entry] of this.pending) {
      clearTimeout(entry.timer);
      entry.reject(error);
    }
    this.pending.clear();
    this.syncChildRef();
  }

  private syncChildRef(): void {
    const proc = this.child as any;
    if (!proc) return;
    const active = this.pending.size > 0;
    try {
      if (active) {
        proc.ref?.();
        proc.stdin?.ref?.();
        proc.stdout?.ref?.();
        proc.stderr?.ref?.();
      } else {
        proc.unref?.();
        proc.stdin?.unref?.();
        proc.stdout?.unref?.();
        proc.stderr?.unref?.();
      }
    } catch {}
  }

  private refreshIdleTimer(): void {
    this.clearIdleTimer();
    if (this.idleTimeoutMs <= 0) return;
    this.idleTimer = setTimeout(() => {
      if (this.pending.size === 0 && this.child) {
        void this.close();
      }
    }, this.idleTimeoutMs);
    this.idleTimer.unref?.();
  }

  private clearIdleTimer(): void {
    if (this.idleTimer) {
      clearTimeout(this.idleTimer);
      this.idleTimer = null;
    }
  }
}

let sharedWinHelperClient: WinHelperClient | null = null;

export function getSharedWinHelperClient(): WinHelperClient | null {
  if (process.platform !== 'win32') return null;
  if (!sharedWinHelperClient) {
    sharedWinHelperClient = new WinHelperClient();
  }
  return sharedWinHelperClient.isAvailable() ? sharedWinHelperClient : null;
}
