// Packs every published package into ../artifacts/npm (what CI publishes and attaches as an artifact):
//   node scripts/pack-all.mjs            -> js/artifacts/npm/*.tgz
// Run `npm run build` first (or rely on each package's prepack).
import { execFileSync } from 'node:child_process';
import { mkdirSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const js = join(dirname(fileURLToPath(import.meta.url)), '..');
const out = join(js, 'artifacts', 'npm');
mkdirSync(out, { recursive: true });
const npm = process.platform === 'win32' ? 'npm.cmd' : 'npm';
for (const workspace of ['packages/syncdata', 'packages/lynx']) {
  const result = execFileSync(npm, ['pack', '--json', '--pack-destination', out], {
    cwd: join(js, workspace),
    encoding: 'utf8',
    shell: process.platform === 'win32',
  });
  const [info] = JSON.parse(result);
  console.log(`${info.name}@${info.version} -> ${join(out, info.filename)} (${info.entryCount} files, ${info.size} bytes)`);
}
