// Packs the G9LynxControls packages the host needs (@g9lynx/foundation, @g9lynx/sqlite, @g9lynx/native — version 0.0.0,
// not on npm) from the sibling checkout into vendor/, where package.json installs them from. Reads that repository
// only (npm pack --ignore-scripts writes the tarball here). Run before `npm install` when G9LynxControls changed.
import { spawnSync } from 'node:child_process';
import { existsSync, mkdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const app = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const controls = process.env.G9LYNXCONTROLS_DIR || resolve(app, '..', '..', '..', '..', 'G9LynxControls');
const vendor = join(app, 'vendor');
mkdirSync(vendor, { recursive: true });
for (const name of ['foundation', 'sqlite', 'native']) {
  const dir = join(controls, 'packages', name);
  if (!existsSync(join(dir, 'dist'))) {
    console.error(`${dir}/dist is missing: build G9LynxControls first (npm run build there).`);
    process.exit(2);
  }
  const result = spawnSync('npm', ['pack', dir, '--pack-destination', vendor, '--ignore-scripts'], { stdio: 'inherit', shell: process.platform === 'win32' });
  if (result.status !== 0) process.exit(result.status ?? 1);
}
