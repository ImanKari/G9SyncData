// Runs Gradle for the JVM suite and the Android compile check: `node scripts/run-gradle.mjs -p jvm test`.
// Finds Gradle on PATH, in GRADLE_HOME, or in the wrapper cache (~/.gradle/wrapper/dists/gradle-8.*), and a JDK in
// JAVA_HOME or Android Studio's bundled runtime, so a developer machine without a global Gradle works too.
// Neither project needs Google Maven (both resolve from Maven Central only). Where a build does need it and
// dl.google.com is blocked, set G9_GRADLE_INIT_SCRIPT to a mirror init script; it is passed as `--init-script`.
import { spawnSync } from 'node:child_process';
import { existsSync, readdirSync } from 'node:fs';
import { homedir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const windows = process.platform === 'win32';
const exe = windows ? 'gradle.bat' : 'gradle';

function onPath(name) {
  const which = spawnSync(windows ? 'where' : 'which', [name], { encoding: 'utf8' });
  return which.status === 0 ? which.stdout.split(/\r?\n/)[0].trim() : null;
}

function findGradle() {
  if (process.env.GRADLE_HOME && existsSync(join(process.env.GRADLE_HOME, 'bin', exe))) return join(process.env.GRADLE_HOME, 'bin', exe);
  const found = onPath('gradle');
  if (found) return found;
  const dists = join(homedir(), '.gradle', 'wrapper', 'dists');
  if (existsSync(dists)) {
    for (const dist of readdirSync(dists).filter((d) => d.startsWith('gradle-8.')).sort().reverse()) {
      for (const hash of readdirSync(join(dists, dist))) {
        const home = join(dists, dist, hash);
        for (const inner of readdirSync(home)) {
          const candidate = join(home, inner, 'bin', exe);
          if (existsSync(candidate)) return candidate;
        }
      }
    }
  }
  return null;
}

function findJava() {
  if (process.env.JAVA_HOME && existsSync(process.env.JAVA_HOME)) return process.env.JAVA_HOME;
  for (const candidate of [
    'C:\\Program Files\\Android\\Android Studio\\jbr',
    '/Applications/Android Studio.app/Contents/jbr/Contents/Home',
    '/opt/android-studio/jbr',
  ]) {
    if (existsSync(candidate)) return candidate;
  }
  return null;
}

const gradle = findGradle();
if (!gradle) {
  console.error('Gradle was not found (PATH, GRADLE_HOME or ~/.gradle/wrapper/dists). Install Gradle 8.14.x.');
  process.exit(2);
}
const env = { ...process.env };
const java = findJava();
if (java) env.JAVA_HOME = java;
const init = process.env.G9_GRADLE_INIT_SCRIPT;
if (init && !existsSync(init)) {
  console.error(`G9_GRADLE_INIT_SCRIPT does not exist: ${init}`);
  process.exit(2);
}
const args = [...(init ? ['--init-script', init] : []), ...process.argv.slice(2), '--console=plain'];
const result = spawnSync(gradle, args, { cwd: root, env, stdio: 'inherit', shell: windows });
process.exit(result.status ?? 1);
