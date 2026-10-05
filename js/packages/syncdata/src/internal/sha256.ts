// FIPS 180-4 SHA-256 (copied from @g9tm/signalr-supernetcore-client, bytes only). Lynx has no WebCrypto.

const K = new Int32Array([
  0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
  0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
  0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
  0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
  0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
  0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
  0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
  0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2,
]);

const INITIAL = [0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19];

/** Incremental SHA-256 hasher (internal; the public entry point is {@link sha256Hex}). */
export class Sha256 {
  private readonly _state = new Int32Array(INITIAL);
  private readonly _w = new Int32Array(64);
  private readonly _block = new Uint8Array(64);
  private _blockLength = 0;
  private _totalBytes = 0;
  private _finished = false;

  /** Adds `data` to the hash. */
  update(data: Uint8Array): this {
    if (this._finished) throw new Error('SHA-256 digest was already computed; create a new hasher.');
    const length = data.length;
    this._totalBytes += length;
    let offset = 0;

    if (this._blockLength > 0) {
      const take = Math.min(64 - this._blockLength, length);
      this._block.set(data.subarray(0, take), this._blockLength);
      this._blockLength += take;
      offset = take;
      if (this._blockLength === 64) {
        this._compress(this._block, 0);
        this._blockLength = 0;
      }
    }

    while (offset + 64 <= length) {
      this._compress(data, offset);
      offset += 64;
    }

    if (offset < length) {
      this._block.set(data.subarray(offset), 0);
      this._blockLength = length - offset;
    }
    return this;
  }

  /** Finishes the hash and returns the 32-byte digest. The hasher cannot be updated afterwards. */
  digest(): Uint8Array {
    if (this._finished) throw new Error('SHA-256 digest was already computed; create a new hasher.');
    this._finished = true;

    const block = this._block;
    let used = this._blockLength;
    block[used++] = 0x80;
    if (used > 56) {
      block.fill(0, used);
      this._compress(block, 0);
      used = 0;
    }
    block.fill(0, used, 56);

    // Message length in bits as a 64-bit big-endian integer (exact for inputs below 2^50 bytes).
    const bytes = this._totalBytes;
    const high = Math.floor(bytes / 0x20000000);
    const low = (bytes * 8) >>> 0;
    block[56] = high >>> 24;
    block[57] = (high >>> 16) & 0xff;
    block[58] = (high >>> 8) & 0xff;
    block[59] = high & 0xff;
    block[60] = low >>> 24;
    block[61] = (low >>> 16) & 0xff;
    block[62] = (low >>> 8) & 0xff;
    block[63] = low & 0xff;
    this._compress(block, 0);

    const out = new Uint8Array(32);
    for (let i = 0; i < 8; i++) {
      const word = this._state[i]!;
      out[i * 4] = word >>> 24;
      out[i * 4 + 1] = (word >>> 16) & 0xff;
      out[i * 4 + 2] = (word >>> 8) & 0xff;
      out[i * 4 + 3] = word & 0xff;
    }
    return out;
  }

  /** Finishes the hash and returns it as lower-case hex. */
  hexDigest(): string {
    return toHex(this.digest());
  }

  private _compress(data: Uint8Array, offset: number): void {
    const w = this._w;
    for (let i = 0; i < 16; i++) {
      const j = offset + i * 4;
      w[i] = (data[j]! << 24) | (data[j + 1]! << 16) | (data[j + 2]! << 8) | data[j + 3]!;
    }
    for (let i = 16; i < 64; i++) {
      const x = w[i - 15]!;
      const y = w[i - 2]!;
      const s0 = ((x >>> 7) | (x << 25)) ^ ((x >>> 18) | (x << 14)) ^ (x >>> 3);
      const s1 = ((y >>> 17) | (y << 15)) ^ ((y >>> 19) | (y << 13)) ^ (y >>> 10);
      w[i] = (w[i - 16]! + s0 + w[i - 7]! + s1) | 0;
    }

    const state = this._state;
    let a = state[0]!;
    let b = state[1]!;
    let c = state[2]!;
    let d = state[3]!;
    let e = state[4]!;
    let f = state[5]!;
    let g = state[6]!;
    let h = state[7]!;

    for (let i = 0; i < 64; i++) {
      const s1 = ((e >>> 6) | (e << 26)) ^ ((e >>> 11) | (e << 21)) ^ ((e >>> 25) | (e << 7));
      const ch = (e & f) ^ (~e & g);
      const t1 = (h + s1 + ch + K[i]! + w[i]!) | 0;
      const s0 = ((a >>> 2) | (a << 30)) ^ ((a >>> 13) | (a << 19)) ^ ((a >>> 22) | (a << 10));
      const maj = (a & b) ^ (a & c) ^ (b & c);
      const t2 = (s0 + maj) | 0;
      h = g;
      g = f;
      f = e;
      e = (d + t1) | 0;
      d = c;
      c = b;
      b = a;
      a = (t1 + t2) | 0;
    }

    state[0] = (state[0]! + a) | 0;
    state[1] = (state[1]! + b) | 0;
    state[2] = (state[2]! + c) | 0;
    state[3] = (state[3]! + d) | 0;
    state[4] = (state[4]! + e) | 0;
    state[5] = (state[5]! + f) | 0;
    state[6] = (state[6]! + g) | 0;
    state[7] = (state[7]! + h) | 0;
  }
}

const HEX: string[] = [];
for (let i = 0; i < 256; i++) HEX.push((i < 16 ? '0' : '') + i.toString(16));

/** Lower-case hex of `bytes` (.NET `Convert.ToHexStringLower`). */
export function toHex(bytes: Uint8Array): string {
  let out = '';
  for (let i = 0; i < bytes.length; i++) out += HEX[bytes[i] as number] as string;
  return out;
}

/** SHA-256 of `bytes`. */
export function sha256(bytes: Uint8Array): Uint8Array {
  return new Sha256().update(bytes).digest();
}
