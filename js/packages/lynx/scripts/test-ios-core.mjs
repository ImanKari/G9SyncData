// `npm run test:ios-core`: builds and runs the iOS Brotli core with its Linux suite (scripts/test-ios-core.sh) —
// directly on Linux, inside WSL (G9_WSL_DISTRO, default Ubuntu-24.04) on Windows. macOS runs the full module in Xcode.
import { spawnSync } from 'node:child_process';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
let result;
if (process.platform === 'win32') {
  const linuxPath = '/mnt/' + root[0].toLowerCase() + root.slice(2).replace(/\\/g, '/');
  const distro = process.env.G9_WSL_DISTRO || 'Ubuntu-24.04';
  result = spawnSync('wsl', ['-d', distro, '--cd', linuxPath, '--', 'bash', 'scripts/test-ios-core.sh'], { stdio: 'inherit' });
} else {
  result = spawnSync('bash', ['scripts/test-ios-core.sh'], { cwd: root, stdio: 'inherit' });
}
process.exit(result.status ?? 1);
