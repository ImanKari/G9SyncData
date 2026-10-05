// One version for the whole repository: G9SyncDataVersion in Directory.Build.props (the NuGet packages) must equal every
// npm package's version, their VERSION constants, the native module versions and the cross-package pins. A release that
// bumps one side and forgets the other fails here.
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const js = join(dirname(fileURLToPath(import.meta.url)), '..');
const repo = join(js, '..');
const props = readFileSync(join(repo, 'Directory.Build.props'), 'utf8');
const expected = /<G9SyncDataVersion>([^<]+)<\/G9SyncDataVersion>/.exec(props)?.[1]?.trim();
if (!expected) throw new Error('G9SyncDataVersion not found in Directory.Build.props');

const problems = [];
const check = (where, actual) => {
  if (actual !== expected) problems.push(`${where}: ${actual ?? '(missing)'} (expected ${expected})`);
};
const read = (path) => readFileSync(join(js, path), 'utf8');
const json = (path) => JSON.parse(read(path));

const syncdata = json('packages/syncdata/package.json');
const lynx = json('packages/lynx/package.json');
check('packages/syncdata/package.json version', syncdata.version);
check('packages/lynx/package.json version', lynx.version);
check('packages/lynx peerDependencies @g9tm/syncdata', lynx.peerDependencies?.['@g9tm/syncdata']);
check('packages/lynx devDependencies @g9tm/syncdata', lynx.devDependencies?.['@g9tm/syncdata']);
check('packages/syncdata/src/index.ts VERSION', /export const VERSION = '([^']+)'/.exec(read('packages/syncdata/src/index.ts'))?.[1]);
check('packages/lynx/src/index.ts VERSION', /export const VERSION = '([^']+)'/.exec(read('packages/lynx/src/index.ts'))?.[1]);
check(
  'packages/lynx/android/.../G9SyncDataLynxModule.kt VERSION',
  /const val VERSION = "([^"]+)"/.exec(read('packages/lynx/android/src/main/java/com/g9tm/syncdatalynx/G9SyncDataLynxModule.kt'))?.[1],
);
check('packages/lynx/ios/src/G9SyncDataLynxModule.m version', /G9SyncDataLynxVersion = @"([^"]+)"/.exec(read('packages/lynx/ios/src/G9SyncDataLynxModule.m'))?.[1]);
check('packages/lynx/ios/g9tm-syncdata-lynx.podspec version', /s\.version\s*=\s*'([^']+)'/.exec(read('packages/lynx/ios/g9tm-syncdata-lynx.podspec'))?.[1]);
check('packages/lynx/src/lynxtron/index.ts VERSION', /const VERSION = '([^']+)'/.exec(read('packages/lynx/src/lynxtron/index.ts'))?.[1]);

if (problems.length > 0) {
  console.error(`Version drift (Directory.Build.props says ${expected}):\n  ${problems.join('\n  ')}`);
  process.exit(1);
}
console.log(`versions: everything is ${expected}`);
