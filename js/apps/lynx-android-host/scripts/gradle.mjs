// Runs Gradle on ../android: `node scripts/gradle.mjs :app:assembleDebug`.
// Finds Gradle (PATH, GRADLE_HOME, or the wrapper cache ~/.gradle/wrapper/dists/gradle-8.*), a JDK (JAVA_HOME, else
// Android Studio's jbr) and the Android SDK (ANDROID_HOME, else the default Windows location). Google Maven is blocked
// from some networks (dl.google.com answers 404): G9_GRADLE_INIT_SCRIPT names a mirror init script, passed as
// `--init-script`; without it, the AgriPadMapBench mirror script is used when it exists next to this repository.
import { spawnSync } from 'node:child_process';
import { existsSync, readdirSync } from 'node:fs';
import { homedir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const project = resolve(here, '..', 'android');
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

const firstExisting = (...paths) => paths.find((p) => p && existsSync(p)) ?? null;

const gradle = findGradle();
if (!gradle) {
  console.error('Gradle was not found (PATH, GRADLE_HOME or ~/.gradle/wrapper/dists). Install Gradle 8.14.x.');
  process.exit(2);
}
const env = { ...process.env };
const java = firstExisting(process.env.JAVA_HOME, 'C:\\Program Files\\Android\\Android Studio\\jbr', '/Applications/Android Studio.app/Contents/jbr/Contents/Home');
if (java) env.JAVA_HOME = java;
const sdk = firstExisting(process.env.ANDROID_HOME, process.env.ANDROID_SDK_ROOT, 'C:\\Program Files (x86)\\Android\\android-sdk');
if (sdk) env.ANDROID_HOME = sdk;
const init = firstExisting(
  process.env.G9_GRADLE_INIT_SCRIPT,
  // AgriPadMapBench lives in G9Libraries since 2026-10-10; its old place under G9BrowserAgent stays as a fallback.
  resolve(here, '..', '..', '..', '..', '..', 'AgriPadMapBench', 'tools', 'google-maven-mirror.gradle'),
  resolve(here, '..', '..', '..', '..', '..', 'G9BrowserAgent', 'AgriPadMapBench', 'tools', 'google-maven-mirror.gradle'),
);

const args = ['-p', project, '--console=plain', ...(init ? ['--init-script', init] : []), ...process.argv.slice(2)];
console.log(`> ${gradle} ${args.join(' ')}\n  JAVA_HOME=${env.JAVA_HOME ?? '(unset)'} ANDROID_HOME=${env.ANDROID_HOME ?? '(unset)'}`);
const result = spawnSync(gradle, args, { stdio: 'inherit', env, shell: windows });
process.exit(result.status ?? 1);
