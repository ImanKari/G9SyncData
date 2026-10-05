// `npm run fixtures:brotli`: writes the Brotli test vectors every platform's suite decodes (test/fixtures/brotli):
// the JVM suite of the Android core, the GNUstep suite of the iOS core and the vitest suite of the Lynxtron bridge.
// Raw Brotli streams at quality 4 / window 22 — the parameters of the .NET server's `BrotliEncoder.TryCompress` — over
// deterministic inputs, so the originals need not be committed: manifest.txt carries each original's length, SHA-256
// and CRC-32. Re-running it rewrites the same bytes unless Node's Brotli encoder changes (the streams stay valid then;
// only the fixture bytes differ).
import { createHash } from 'node:crypto';
import { mkdirSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { brotliCompressSync, constants, crc32 } from 'node:zlib';

const out = join(dirname(fileURLToPath(import.meta.url)), '..', 'test', 'fixtures', 'brotli');

/** xorshift32: the same sequence on every machine. */
function random(seed) {
  let state = seed >>> 0 || 1;
  return () => {
    state ^= state << 13;
    state >>>= 0;
    state ^= state >>> 17;
    state ^= state << 5;
    state >>>= 0;
    return state;
  };
}

/** 300 KiB of 4 KiB blocks: random bytes (incompressible) alternating with repeated row-like records. */
function mixed() {
  const next = random(0x9e3779b9);
  const bytes = new Uint8Array(300 * 1024);
  const record = Buffer.from('{"id":"0f8fad5b-d9cb-469f-a165-70867728950e","state":"Active","rev":42}\n', 'latin1');
  for (let block = 0; block < bytes.length / 4096; block++) {
    const start = block * 4096;
    for (let i = 0; i < 4096; i++) {
      bytes[start + i] = block % 3 === 0 ? next() & 0xff : record[(i + block) % record.length];
    }
  }
  return bytes;
}

/** ~32 KiB of English-like text: exercises the static dictionary and its transforms. */
function text() {
  const words = ('the of and to in is that for it as was with be by on not he this are or his from at which but have an ' +
    'they you were their one all we can her has there been if more when will would who so no tree pot graft block ridge ' +
    'observation transfer barcode synchronization offline device server').split(' ');
  const next = random(0x12345678);
  const parts = [];
  let length = 0;
  while (length < 32 * 1024) {
    let word = words[next() % words.length];
    if (next() % 11 === 0) word = word[0].toUpperCase() + word.slice(1);
    const tail = next() % 13 === 0 ? '. ' : next() % 7 === 0 ? ', ' : ' ';
    parts.push(word + tail);
    length += word.length + tail.length;
  }
  return Buffer.from(parts.join('').slice(0, 32 * 1024), 'latin1');
}

const inputs = [
  ['empty', new Uint8Array(0)],
  ['one-byte', new Uint8Array([0x2a])],
  ['mixed-300k', mixed()],
  ['text-32k', text()],
];

mkdirSync(out, { recursive: true });
const lines = ['# name length sha256 crc32 (of the ORIGINAL bytes; quality 4, window 22, made by scripts/make-brotli-fixtures.mjs)'];
for (const [name, bytes] of inputs) {
  const compressed = brotliCompressSync(bytes, {
    params: {
      [constants.BROTLI_PARAM_QUALITY]: 4,
      [constants.BROTLI_PARAM_LGWIN]: 22,
      [constants.BROTLI_PARAM_SIZE_HINT]: bytes.length,
    },
  });
  writeFileSync(join(out, `${name}.br`), compressed);
  const sha = createHash('sha256').update(bytes).digest('hex');
  const crc = (crc32(bytes) >>> 0).toString(16).padStart(8, '0');
  lines.push(`${name}.br ${bytes.length} ${sha} ${crc}`);
  console.log(`${name}.br: ${bytes.length} -> ${compressed.length} bytes`);
}
writeFileSync(join(out, 'manifest.txt'), lines.join('\n') + '\n');
