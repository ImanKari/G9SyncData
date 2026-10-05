// One-shot MD5 (RFC 1321), the twin of G9SyncData.Abstractions/G9CMd5.cs: the reconciliation hash's bucketing
// function (ADR-0012), not a security boundary. Lynx has no WebCrypto (and WebCrypto has no MD5).

const K = new Int32Array([
  0xd76aa478, 0xe8c7b756, 0x242070db, 0xc1bdceee, 0xf57c0faf, 0x4787c62a, 0xa8304613, 0xfd469501,
  0x698098d8, 0x8b44f7af, 0xffff5bb1, 0x895cd7be, 0x6b901122, 0xfd987193, 0xa679438e, 0x49b40821,
  0xf61e2562, 0xc040b340, 0x265e5a51, 0xe9b6c7aa, 0xd62f105d, 0x02441453, 0xd8a1e681, 0xe7d3fbc8,
  0x21e1cde6, 0xc33707d6, 0xf4d50d87, 0x455a14ed, 0xa9e3e905, 0xfcefa3f8, 0x676f02d9, 0x8d2a4c8a,
  0xfffa3942, 0x8771f681, 0x6d9d6122, 0xfde5380c, 0xa4beea44, 0x4bdecfa9, 0xf6bb4b60, 0xbebfbc70,
  0x289b7ec6, 0xeaa127fa, 0xd4ef3085, 0x04881d05, 0xd9d4d039, 0xe6db99e5, 0x1fa27cf8, 0xc4ac5665,
  0xf4292244, 0x432aff97, 0xab9423a7, 0xfc93a039, 0x655b59c3, 0x8f0ccc92, 0xffeff47d, 0x85845dd1,
  0x6fa87e4f, 0xfe2ce6e0, 0xa3014314, 0x4e0811a1, 0xf7537e82, 0xbd3af235, 0x2ad7d2bb, 0xeb86d391,
]);

const S = [
  7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22, 5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20, 4,
  11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23, 6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21,
];

const G = new Int32Array(64);
for (let i = 0; i < 64; i++) {
  G[i] = i < 16 ? i : i < 32 ? (5 * i + 1) & 15 : i < 48 ? (3 * i + 5) & 15 : (7 * i) & 15;
}

const M = new Int32Array(16);
const TAIL = new Uint8Array(128);

function block(data: Uint8Array, offset: number, state: Int32Array): void {
  for (let i = 0; i < 16; i++) {
    const j = offset + i * 4;
    M[i] = (data[j] as number) | ((data[j + 1] as number) << 8) | ((data[j + 2] as number) << 16) | ((data[j + 3] as number) << 24);
  }
  let a = state[0] as number;
  let b = state[1] as number;
  let c = state[2] as number;
  let d = state[3] as number;
  for (let i = 0; i < 64; i++) {
    let f: number;
    switch (i >> 4) {
      case 0:
        f = (b & c) | (~b & d);
        break;
      case 1:
        f = (d & b) | (~d & c);
        break;
      case 2:
        f = b ^ c ^ d;
        break;
      default:
        f = c ^ (b | ~d);
        break;
    }
    const next = d;
    d = c;
    c = b;
    const sum = (a + f + (K[i] as number) + (M[G[i] as number] as number)) | 0;
    const s = S[i] as number;
    b = (b + ((sum << s) | (sum >>> (32 - s)))) | 0;
    a = next;
  }
  state[0] = ((state[0] as number) + a) | 0;
  state[1] = ((state[1] as number) + b) | 0;
  state[2] = ((state[2] as number) + c) | 0;
  state[3] = ((state[3] as number) + d) | 0;
}

/** Writes the 16-byte MD5 of `source` to `destination` (offset 0) and returns it. */
export function md5(source: Uint8Array, destination: Uint8Array = new Uint8Array(16)): Uint8Array {
  const state = new Int32Array([0x67452301, 0xefcdab89 | 0, 0x98badcfe | 0, 0x10325476]);
  const length = source.length;
  const full = length - (length % 64);
  for (let offset = 0; offset < full; offset += 64) block(source, offset, state);

  // The tail, the 0x80 marker and the bit length: one or two final blocks.
  TAIL.fill(0);
  const remaining = length - full;
  for (let i = 0; i < remaining; i++) TAIL[i] = source[full + i] as number;
  TAIL[remaining] = 0x80;
  const tailBytes = remaining < 56 ? 64 : 128;
  const bitsLow = (length * 8) >>> 0;
  const bitsHigh = Math.floor(length / 0x20000000) >>> 0;
  const at = tailBytes - 8;
  TAIL[at] = bitsLow & 0xff;
  TAIL[at + 1] = (bitsLow >>> 8) & 0xff;
  TAIL[at + 2] = (bitsLow >>> 16) & 0xff;
  TAIL[at + 3] = (bitsLow >>> 24) & 0xff;
  TAIL[at + 4] = bitsHigh & 0xff;
  TAIL[at + 5] = (bitsHigh >>> 8) & 0xff;
  TAIL[at + 6] = (bitsHigh >>> 16) & 0xff;
  TAIL[at + 7] = (bitsHigh >>> 24) & 0xff;
  for (let offset = 0; offset < tailBytes; offset += 64) block(TAIL, offset, state);

  for (let i = 0; i < 4; i++) {
    const word = state[i] as number;
    destination[i * 4] = word & 0xff;
    destination[i * 4 + 1] = (word >>> 8) & 0xff;
    destination[i * 4 + 2] = (word >>> 16) & 0xff;
    destination[i * 4 + 3] = (word >>> 24) & 0xff;
  }
  return destination;
}
