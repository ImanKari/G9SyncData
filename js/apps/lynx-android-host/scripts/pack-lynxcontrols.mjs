// Packs the G9LynxControls packages the host needs (@g9tm/lynxcontrols-foundation, -sqlite, -native, version = the
// pinned G9MAUIControls version) from the sibling checkout into vendor/, for testing UNPUBLISHED changes: package.json
// takes the published 1.4.0 from npm; `npm install --no-save vendor/*.tgz` puts the working tree over them. Reads that repository
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
