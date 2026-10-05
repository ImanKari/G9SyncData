// Gate for code that ships to Lynx's background thread (G9LynxControls plan/specs/02-Lynx-Platform-Rules.md §1), the
// same gate as G9SignalRSuperNetCore/js/scripts/check-lynx-runtime.mjs:
//  1. the built packages contain no syntax newer than ES2019 (the app's build downlevels node_modules, but a library
//     that already is ES2019 needs nothing it might miss) — parsed with acorn when it is installed, else skipped;
//  2. the sources never use an API Lynx lacks unguarded: BigInt, Intl, TextEncoder/TextDecoder, btoa/atob,
//     queueMicrotask, structuredClone, `globalThis.setTimeout`-style timer lookups, `new URL(` / `new Blob(` outside
//     the files allowed to (guarded) use them.
// The lynxsim suites prove the rest by running the code without those APIs.
import { readdirSync, readFileSync, statSync } from 'node:fs';
import { dirname, join, relative } from 'node:path';
import { fileURLToPath } from 'node:url';

const js = join(dirname(fileURLToPath(import.meta.url)), '..');
const packages = ['packages/syncdata', 'packages/lynx'];

/** Files allowed to mention an API, because they guard it or never run on Lynx. */
const allowed = {
  TextEncoder: ['packages/syncdata/src/internal/utf8.ts'],
  TextDecoder: ['packages/syncdata/src/internal/utf8.ts'],
  BigInt: [],
};
// Node-only entry points, and the vendored Brotli decoder (its static dictionary is one long string literal that the
// patterns would misread; the acorn pass and the lynxsim runs cover it).
const nodeOnly = ['packages/syncdata/src/node/', 'packages/lynx/src/lynxtron/', 'packages/syncdata/src/brotli-js/decode.ts'];

const banned = [
  { name: 'BigInt', pattern: /\bBigInt\s*\(|\d+n\b/ },
  { name: 'Intl', pattern: /\bIntl\./ },
  { name: 'toLocale', pattern: /\.toLocale\w*\(/ },
  { name: 'TextEncoder', pattern: /new\s+TextEncoder\b/ },
  { name: 'TextDecoder', pattern: /new\s+TextDecoder\b/ },
  { name: 'btoa', pattern: /\bbtoa\s*\(/ },
  { name: 'atob', pattern: /\batob\s*\(/ },
  { name: 'queueMicrotask', pattern: /\bqueueMicrotask\s*\(/ },
  { name: 'structuredClone', pattern: /\bstructuredClone\s*\(/ },
  { name: 'globalTimers', pattern: /globalThis\.(setTimeout|setInterval|clearTimeout|clearInterval)\b/ },
  { name: 'URL', pattern: /new\s+URL\s*\(/ },
  { name: 'Blob', pattern: /new\s+Blob\s*\(/ },
];

function walk(dir, out = []) {
  for (const entry of readdirSync(dir)) {
    const path = join(dir, entry);
    if (statSync(path).isDirectory()) walk(path, out);
    else out.push(path);
  }
  return out;
}

const problems = [];
for (const pkg of packages) {
  for (const file of walk(join(js, pkg, 'src')).filter((f) => f.endsWith('.ts'))) {
    const rel = relative(js, file).replace(/\\/g, '/');
    if (nodeOnly.some((prefix) => rel.startsWith(prefix))) continue;
    const text = readFileSync(file, 'utf8')
      .replace(/\/\*[\s\S]*?\*\//g, '')
      .replace(/\/\/.*$/gm, '');
    for (const { name, pattern } of banned) {
      if (pattern.test(text) && !(allowed[name] ?? []).includes(rel)) problems.push(`${rel}: uses ${name}`);
    }
  }
}

let acorn = null;
try {
  acorn = await import('acorn');
} catch {
  console.log('check-lynx-runtime: acorn not installed, ES2019 syntax check skipped');
}
if (acorn) {
  for (const pkg of packages) {
    let dist;
    try {
      dist = walk(join(js, pkg, 'dist')).filter((f) => f.endsWith('.js'));
    } catch {
      problems.push(`${pkg}: no dist/ (run npm run build first)`);
      continue;
    }
    for (const file of dist) {
      try {
        acorn.parse(readFileSync(file, 'utf8'), { ecmaVersion: 2019, sourceType: 'module' });
      } catch (error) {
        problems.push(`${relative(js, file)}: not ES2019 (${error.message})`);
      }
    }
  }
}

if (problems.length > 0) {
  console.error(`Lynx runtime check failed:\n  ${problems.join('\n  ')}`);
  process.exit(1);
}
console.log('check-lynx-runtime: sources use no API Lynx lacks; dist is ES2019');
