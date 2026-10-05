// The real .NET side of the interop suite: the sample server (samples/G9SyncData.Samples.Server) on a throwaway
// database `g9t_js_<random>` of the disposable PostgreSQL (G9SYNC_TEST_PG, default localhost:15433 — never a shared
// server), and the .NET sample client (samples/G9SyncData.Samples.AotClient) for the database comparison.
import { execFile, spawn, type ChildProcess } from 'node:child_process';
import { connect, createServer } from 'node:net';
import { existsSync, mkdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

export const repo = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..', '..', '..', '..');
const serverProject = join(repo, 'samples', 'G9SyncData.Samples.Server');
const clientProject = join(repo, 'samples', 'G9SyncData.Samples.AotClient');

/** Connection string parts of the disposable server (G9SYNC_TEST_PG, a superuser connection to `postgres`). */
export function testServer(): { [key: string]: string } {
  const text = process.env.G9SYNC_TEST_PG || 'Host=localhost;Port=15433;Username=postgres;Database=postgres';
  const parts: { [key: string]: string } = {};
  for (const part of text.split(';')) {
    const at = part.indexOf('=');
    if (at > 0) parts[part.slice(0, at).trim().toLowerCase()] = part.slice(at + 1).trim();
  }
  return parts;
}

function connectionString(database: string): string {
  const parts = testServer();
  return (
    `Host=${parts.host ?? 'localhost'};Port=${parts.port ?? '5432'};Username=${parts.username ?? parts['user id'] ?? 'postgres'};` +
    (parts.password ? `Password=${parts.password};` : '') +
    `Database=${database}`
  );
}

export function run(command: string, args: string[], options: { cwd?: string; env?: NodeJS.ProcessEnv; timeoutMs?: number } = {}): Promise<string> {
  return new Promise((resolvePromise, reject) => {
    execFile(command, args, { cwd: options.cwd, env: options.env ?? process.env, timeout: options.timeoutMs ?? 600000, maxBuffer: 64 * 1024 * 1024 }, (error, stdout, stderr) => {
      if (error) reject(new Error(`${command} ${args.join(' ')} failed: ${error.message}\n${stdout}\n${stderr}`));
      else resolvePromise(stdout + stderr);
    });
  });
}

export async function freePort(): Promise<number> {
  return new Promise((resolvePromise, reject) => {
    const server = createServer();
    server.once('error', reject);
    server.listen(0, '127.0.0.1', () => {
      const address = server.address();
      const port = typeof address === 'object' && address ? address.port : 0;
      server.close(() => resolvePromise(port));
    });
  });
}

/** Is the disposable PostgreSQL answering? (The suite is skipped, never failed, when it is not — like [PostgresFact].) */
export async function postgresAvailable(): Promise<string | null> {
  const parts = testServer();
  const port = Number(parts.port ?? '5432');
  const host = parts.host ?? 'localhost';
  return new Promise((resolvePromise) => {
    const socket = connect({ host, port }, () => {
      socket.end();
      resolvePromise(null);
    });
    socket.on('error', (error: Error) => resolvePromise(`${host}:${port} ${error.message}`));
    socket.setTimeout(2000, () => {
      socket.destroy();
      resolvePromise(`${host}:${port} timed out`);
    });
  });
}


/**
 * Runs a test body against `server`; when it fails, the error carries the tail of the sample server's log — on a CI agent
 * nothing else shows why the server closed a connection or rejected a call.
 */
export async function withServerLog<T>(server: SampleServer, body: () => Promise<T>, lines = 150): Promise<T> {
  try {
    return await body();
  } catch (error) {
    const tail = server.log.join('').split(/\r?\n/).slice(-lines).join('\n');
    if (error instanceof Error) error.message += `\n--- sample server log (last ${lines} lines) ---\n${tail}`;
    throw error;
  }
}

/** Drops a g9t_js_ database with psql (G9SYNC_PSQL, else psql on PATH, else D:\Apps\pgsql\bin\psql.exe). */
export async function dropDatabase(database: string): Promise<void> {
  if (!/^g9t_js_[a-z0-9]+$/.test(database)) throw new Error(`Refusing to drop '${database}': not a test database.`);
  const parts = testServer();
  const psql = process.env.G9SYNC_PSQL || (existsSync('D:\\Apps\\pgsql\\bin\\psql.exe') ? 'D:\\Apps\\pgsql\\bin\\psql.exe' : 'psql');
  const env = { ...process.env, PGPASSWORD: parts.password ?? '' };
  await run(
    psql,
    ['-h', parts.host ?? 'localhost', '-p', parts.port ?? '5432', '-U', parts.username ?? 'postgres', '-d', 'postgres', '-v', 'ON_ERROR_STOP=1', '-c', `DROP DATABASE IF EXISTS "${database}" WITH (FORCE)`],
    { env, timeoutMs: 60000 },
  );
}

let built = false;

/** Builds the sample server and client once per process. */
export async function buildSamples(): Promise<void> {
  if (built) return;
  await run('dotnet', ['build', serverProject, '-c', 'Debug', '-nologo', '-v', 'q']);
  await run('dotnet', ['build', clientProject, '-c', 'Debug', '-nologo', '-v', 'q']);
  built = true;
}

export interface SampleServer {
  url: string;
  hubUrl: string;
  database: string;
  log: string[];
  stop(): Promise<void>;
}

/** Starts the sample server on a free port and a new database (created and seeded by the sample: 2 sites × 25,000 pots). */
export async function startServer(): Promise<SampleServer> {
  await buildSamples();
  const port = await freePort();
  const database = 'g9t_js_' + Math.random().toString(36).slice(2, 10);
  const url = `http://127.0.0.1:${port}`;
  const dll = join(serverProject, 'bin', 'Debug', 'net10.0', 'G9SyncData.Samples.Server.dll');
  const log: string[] = [];
  const child: ChildProcess = spawn('dotnet', [dll, '--urls', url, '--ConnectionStrings:G9Sync=' + connectionString(database)], {
    cwd: dirname(dll),
    env: { ...process.env, ASPNETCORE_ENVIRONMENT: 'Development', Logging__LogLevel__Default: 'Warning' },
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  child.stdout?.on('data', (d: Buffer) => log.push(d.toString()));
  child.stderr?.on('data', (d: Buffer) => log.push(d.toString()));
  const stop = async (): Promise<void> => {
    if (child.exitCode === null) {
      child.kill();
      await new Promise((r) => setTimeout(r, 500));
    }
    try {
      await dropDatabase(database);
    } catch (error) {
      console.warn(`Could not drop ${database}: ${(error as Error).message}`);
    }
  };
  const deadline = Date.now() + 180000;
  for (;;) {
    if (child.exitCode !== null) {
      await stop();
      throw new Error('The sample server exited:\n' + log.join(''));
    }
    try {
      const response = await fetch(url + '/');
      if (response.ok) break;
    } catch {
      // not yet listening
    }
    if (Date.now() > deadline) {
      await stop();
      throw new Error('The sample server did not start:\n' + log.join(''));
    }
    await new Promise((r) => setTimeout(r, 500));
  }
  return { url, hubUrl: url + '/g9sync', database, log, stop };
}

/** Runs the .NET sample client against the hub; returns the path of the SQLite file it wrote. */
export async function runDotnetClient(hubUrl: string, copyTo: string): Promise<string> {
  const bin = join(clientProject, 'bin', 'Debug', 'net10.0');
  const dll = join(bin, 'G9SyncData.Samples.AotClient.dll');
  const db = join(bin, 'g9sync-sample-client.db');
  for (const suffix of ['', '-wal', '-shm']) {
    try {
      (await import('node:fs')).rmSync(db + suffix, { force: true });
    } catch {
      // not there
    }
  }
  await run('dotnet', [dll, hubUrl], { cwd: bin, timeoutMs: 600000 });
  mkdirSync(dirname(copyTo), { recursive: true });
  const fs = await import('node:fs');
  fs.copyFileSync(db, copyTo);
  for (const suffix of ['-wal', '-shm']) if (fs.existsSync(db + suffix)) fs.copyFileSync(db + suffix, copyTo + suffix);
  return copyTo;
}
