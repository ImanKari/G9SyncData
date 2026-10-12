// Runs the device test end to end: `node scripts/device-test.mjs [--protocol messagepack|json|both] [--files off|on|both]
// [--serial S] [--install] [--timeout-s 900]`.
//
// For each protocol (MessagePack first, then JSON) and each files mode (rows first, then snapshot files - ADR-0072),
// EACH against a FRESH server database:
//   1. starts the .NET sample server (samples/G9SyncData.Samples.Server, built Debug) on 0.0.0.0:<free port> with a new
//      database g9t_js_<random> on the disposable PostgreSQL (G9SYNC_TEST_PG, default localhost:15433 — never a shared
//      server); the sample creates and seeds it (2 sites × 25,000 pots);
//   2. launches the app on the emulator/device with `--es server <url> --es protocol <p>`: http://10.0.2.2:<port> on an
//      emulator (the host's loopback), http://127.0.0.1:<port> through `adb reverse` on a physical device;
//   3. follows logcat until the page logs `G9SYNC-DEVICE RESULT PASS|FAIL`, then takes a screenshot;
//   4. stops the server and drops its database.
// Logcat, the result lines and the screenshots land in build/device-test/. Exit code 0 only when every run passed.
import { execFile, spawn } from 'node:child_process';
import { createWriteStream, existsSync, mkdirSync, writeFileSync } from 'node:fs';
import { connect, createServer } from 'node:net';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const app = resolve(here, '..');
const repo = resolve(app, '..', '..', '..');
const serverDll = join(repo, 'samples', 'G9SyncData.Samples.Server', 'bin', 'Debug', 'net10.0', 'G9SyncData.Samples.Server.dll');
const out = join(app, 'build', 'device-test');
const apk = join(app, 'android', 'app', 'build', 'outputs', 'apk', 'debug', 'app-debug.apk');
const pkg = 'com.g9tm.syncdatahost';

const args = process.argv.slice(2);
const option = (name, fallback) => {
  const at = args.indexOf(name);
  return at >= 0 && at + 1 < args.length ? args[at + 1] : fallback;
};
const protocols = (() => {
  const p = option('--protocol', 'both');
  return p === 'both' ? ['messagepack', 'json'] : [p];
})();
const fileModes = (() => {
  const f = option('--files', 'off');
  return f === 'both' ? ['off', 'on'] : [f];
})();
const timeoutMs = Number(option('--timeout-s', '900')) * 1000;
const adb =
  process.env.ADB ||
  [process.env.ANDROID_HOME, process.env.ANDROID_SDK_ROOT, 'C:\\Program Files (x86)\\Android\\android-sdk']
    .filter(Boolean)
    .map((sdk) => join(sdk, 'platform-tools', process.platform === 'win32' ? 'adb.exe' : 'adb'))
    .find((p) => existsSync(p)) ||
  'adb';
const psql = process.env.G9SYNC_PSQL || (existsSync('D:\\Apps\\pgsql\\bin\\psql.exe') ? 'D:\\Apps\\pgsql\\bin\\psql.exe' : 'psql');

function pgParts() {
  const text = process.env.G9SYNC_TEST_PG || 'Host=localhost;Port=15433;Username=postgres;Database=postgres';
  const parts = {};
  for (const part of text.split(';')) {
    const at = part.indexOf('=');
    if (at > 0) parts[part.slice(0, at).trim().toLowerCase()] = part.slice(at + 1).trim();
  }
  return { host: parts.host ?? 'localhost', port: parts.port ?? '5432', user: parts.username ?? parts['user id'] ?? 'postgres', password: parts.password ?? '' };
}

function run(command, commandArgs, options = {}) {
  return new Promise((resolvePromise, reject) => {
    execFile(command, commandArgs, { timeout: options.timeoutMs ?? 120000, maxBuffer: 64 * 1024 * 1024, encoding: options.encoding ?? 'utf8', env: options.env ?? process.env }, (error, stdout, stderr) => {
      if (error) reject(new Error(`${command} ${commandArgs.join(' ')} failed: ${error.message}\n${stdout}\n${stderr}`));
      else resolvePromise(stdout);
    });
  });
}

const serialArgs = () => (option('--serial', process.env.ANDROID_SERIAL) ? ['-s', option('--serial', process.env.ANDROID_SERIAL)] : []);
const adbRun = (adbArgs, options) => run(adb, [...serialArgs(), ...adbArgs], options);

function freePort() {
  return new Promise((resolvePromise, reject) => {
    const server = createServer();
    server.once('error', reject);
    server.listen(0, '127.0.0.1', () => {
      const port = server.address().port;
      server.close(() => resolvePromise(port));
    });
  });
}

function postgresAvailable() {
  const { host, port } = pgParts();
  return new Promise((resolvePromise) => {
    const socket = connect({ host, port: Number(port) }, () => {
      socket.end();
      resolvePromise(true);
    });
    socket.on('error', () => resolvePromise(false));
    socket.setTimeout(2000, () => {
      socket.destroy();
      resolvePromise(false);
    });
  });
}

async function psqlCommand(sql) {
  const { host, port, user, password } = pgParts();
  return run(psql, ['-h', host, '-p', port, '-U', user, '-d', 'postgres', '-v', 'ON_ERROR_STOP=1', '-At', '-c', sql], {
    env: { ...process.env, PGPASSWORD: password },
    timeoutMs: 60000,
  });
}

async function dropDatabase(database) {
  if (!/^g9t_js_[a-z0-9]+$/.test(database)) throw new Error(`Refusing to drop '${database}': not a test database.`);
  await psqlCommand(`DROP DATABASE IF EXISTS "${database}" WITH (FORCE)`);
}

async function startServer(log) {
  if (!existsSync(serverDll)) throw new Error(`${serverDll} is missing: dotnet build samples/G9SyncData.Samples.Server -c Debug`);
  const port = await freePort();
  const database = 'g9t_js_' + Math.random().toString(36).slice(2, 10).replace(/[^a-z0-9]/g, 'x');
  const { host, port: pgPort, user, password } = pgParts();
  const connection = `Host=${host};Port=${pgPort};Username=${user};${password ? `Password=${password};` : ''}Database=${database}`;
  const child = spawn('dotnet', [serverDll, '--urls', `http://0.0.0.0:${port}`, '--ConnectionStrings:G9Sync=' + connection], {
    cwd: dirname(serverDll),
    env: { ...process.env, ASPNETCORE_ENVIRONMENT: 'Development', Logging__LogLevel__Default: 'Warning' },
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  child.stdout.on('data', (d) => log.write(d));
  child.stderr.on('data', (d) => log.write(d));
  let stopped = false;
  const stop = async () => {
    if (stopped) return;
    stopped = true;
    if (child.exitCode === null) {
      const exited = new Promise((r) => child.once('exit', r));
      child.kill();
      await Promise.race([exited, new Promise((r) => setTimeout(r, 5000))]);
      if (child.exitCode === null && child.signalCode === null) throw new Error(`The sample server (pid ${child.pid}) did not stop.`);
    }
    await dropDatabase(database);
  };
  const deadline = Date.now() + 300000;
  for (;;) {
    if (child.exitCode !== null) {
      await stop().catch(() => undefined);
      throw new Error(`The sample server exited with ${child.exitCode}; see ${log.path}`);
    }
    try {
      const response = await fetch(`http://127.0.0.1:${port}/`);
      if (response.ok) break;
    } catch {
      // not listening yet (the sample seeds its database first)
    }
    if (Date.now() > deadline) {
      await stop().catch(() => undefined);
      throw new Error('The sample server did not start within 300 s.');
    }
    await new Promise((r) => setTimeout(r, 500));
  }
  return { port, database, pid: child.pid, stop };
}

async function runOnDevice(protocol, files) {
  const runId = protocol.slice(0, 4) + Date.now().toString(36);
  const label = files === 'on' ? `${protocol}-files` : protocol;
  const serverLog = createWriteStream(join(out, `${label}-server.log`));
  const logcatFile = createWriteStream(join(out, `${label}-logcat.txt`));
  console.log(`\n=== ${label}: starting the sample server on a fresh database`);
  const server = await startServer(serverLog);
  console.log(`    server pid ${server.pid} on 0.0.0.0:${server.port}, database ${server.database}`);
  const lines = [];
  let logcat;
  let reversed = null;
  try {
    await adbRun(['shell', 'am', 'force-stop', pkg]);
    await adbRun(['logcat', '-c']);
    logcat = spawn(adb, [...serialArgs(), 'logcat', '-v', 'threadtime'], { stdio: ['ignore', 'pipe', 'pipe'] });
    const result = new Promise((resolvePromise) => {
      let buffer = '';
      logcat.stdout.on('data', (chunk) => {
        logcatFile.write(chunk);
        buffer += chunk.toString('utf8');
        let at;
        while ((at = buffer.indexOf('\n')) >= 0) {
          const line = buffer.slice(0, at).replace(/\r$/, '');
          buffer = buffer.slice(at + 1);
          const marker = line.indexOf('G9SYNC-DEVICE');
          if (marker < 0) continue;
          const text = line.slice(marker);
          lines.push(text);
          console.log('    ' + text);
          if (/G9SYNC-DEVICE RESULT (PASS|FAIL)/.test(text)) resolvePromise(text.includes('RESULT PASS'));
        }
      });
    });
    // A physical device cannot see 10.0.2.2 (that is the emulator's alias for the host): reverse the port instead.
    const serial = option('--serial', process.env.ANDROID_SERIAL) || '';
    const physical = serial !== '' && !serial.startsWith('emulator-');
    if (physical) await adbRun(['reverse', `tcp:${server.port}`, `tcp:${server.port}`]);
    reversed = physical ? server.port : null;
    const serverUrl = physical ? `http://127.0.0.1:${server.port}` : `http://10.0.2.2:${server.port}`;
    await adbRun(['shell', 'am', 'start', '-S', '-W', '-n', `${pkg}/.MainActivity`, '--es', 'server', serverUrl, '--es', 'protocol', protocol, '--es', 'runId', runId, '--es', 'files', files]);
    const pass = await Promise.race([result, new Promise((r) => setTimeout(() => r(null), timeoutMs))]);
    await new Promise((r) => setTimeout(r, 1500)); // let the page paint the final state
    const png = await adbRun(['exec-out', 'screencap', '-p'], { encoding: 'buffer' });
    writeFileSync(join(out, `${label}.png`), png);
    await adbRun(['shell', 'am', 'force-stop', pkg]);
    return { protocol, files, label, pass: pass === true, timedOut: pass === null, lines, database: server.database, port: server.port };
  } finally {
    if (logcat) logcat.kill();
    if (reversed !== null) await adbRun(['reverse', '--remove', `tcp:${reversed}`]).catch(() => undefined);
    await server.stop();
    serverLog.end();
    logcatFile.end();
    console.log(`    server stopped, database ${server.database} dropped`);
  }
}

mkdirSync(out, { recursive: true });
if (!(await postgresAvailable())) {
  const { host, port } = pgParts();
  console.error(`The disposable PostgreSQL is not answering on ${host}:${port} (G9SYNC_TEST_PG).`);
  process.exit(2);
}
if (args.includes('--install')) {
  console.log(`Installing ${apk}`);
  console.log(await adbRun(['install', '-r', apk], { timeoutMs: 300000 }));
}
const results = [];
for (const protocol of protocols) {
  for (const files of fileModes) results.push(await runOnDevice(protocol, files));
}
const left = (await psqlCommand("SELECT count(*) FROM pg_database WHERE datname LIKE 'g9t_%'")).trim();
console.log('\n=== Summary');
for (const r of results) console.log(`  ${r.label}: ${r.pass ? 'PASS' : r.timedOut ? 'TIMEOUT' : 'FAIL'} (${r.lines.length} lines, ${join(out, r.label + '.png')})`);
console.log(`  g9t_ databases left on the test server: ${left}`);
writeFileSync(join(out, 'summary.json'), JSON.stringify({ results, g9tDatabasesLeft: Number(left) }, null, 2));
process.exit(results.every((r) => r.pass) ? 0 : 1);
