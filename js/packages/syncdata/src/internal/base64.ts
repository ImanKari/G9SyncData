// Base64 helpers that work in browsers, WebViews, Node and Lynx without Buffer, btoa or atob (Lynx's background
// thread has none of them). They use the native Uint8Array.prototype.toBase64 / Uint8Array.fromBase64 (ES2026) when
// the runtime has them, and a table-driven codec otherwise.

type NativeToBase64 = { toBase64?: () => string };
type NativeFromBase64 = { fromBase64?: (text: string) => Uint8Array };

const ALPHABET = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/';
const ENCODE: string[] = [];
const DECODE = new Int16Array(256).fill(-1);
for (let i = 0; i < ALPHABET.length; i++) {
  ENCODE[i] = ALPHABET.charAt(i);
  DECODE[ALPHABET.charCodeAt(i)] = i;
}
// base64url digits decode too (they never collide with the standard ones).
DECODE['-'.charCodeAt(0)] = 62;
DECODE['_'.charCodeAt(0)] = 63;

/** Standard (RFC 4648, padded) base64 of `bytes` — what System.Text.Json writes and reads for `byte[]`. */
export function bytesToBase64(bytes: Uint8Array): string {
  const native = (bytes as unknown as NativeToBase64).toBase64;
  if (typeof native === 'function') return native.call(bytes);
  return bytesToBase64Js(bytes);
}

/** The table-driven encoder (exported for tests; use {@link bytesToBase64}). */
export function bytesToBase64Js(bytes: Uint8Array): string {
  const parts: string[] = [];
  const length = bytes.length;
  const whole = length - (length % 3);
  let line = '';
  for (let i = 0; i < whole; i += 3) {
    const n = (bytes[i]! << 16) | (bytes[i + 1]! << 8) | bytes[i + 2]!;
    line += ENCODE[n >>> 18]! + ENCODE[(n >>> 12) & 63]! + ENCODE[(n >>> 6) & 63]! + ENCODE[n & 63]!;
    // Joining bounded pieces keeps string building linear on engines without rope strings.
    if (line.length >= 8192) {
      parts.push(line);
      line = '';
    }
  }
  const rest = length - whole;
  if (rest === 1) {
    const n = bytes[whole]! << 16;
    line += ENCODE[n >>> 18]! + ENCODE[(n >>> 12) & 63]! + '==';
  } else if (rest === 2) {
    const n = (bytes[whole]! << 16) | (bytes[whole + 1]! << 8);
    line += ENCODE[n >>> 18]! + ENCODE[(n >>> 12) & 63]! + ENCODE[(n >>> 6) & 63]! + '=';
  }
  parts.push(line);
  return parts.join('');
}

/** Decodes standard or url-safe base64 (padding optional, whitespace ignored). Throws on any other character. */
export function base64ToBytes(text: string): Uint8Array {
  const native = (Uint8Array as unknown as NativeFromBase64).fromBase64;
  if (typeof native === 'function') {
    try {
      return native(text);
    } catch {
      // Fall through to the table codec, which also accepts url-safe digits and missing padding.
    }
  }
  return base64ToBytesJs(text);
}

/** The table-driven decoder (exported for tests; use {@link base64ToBytes}). */
export function base64ToBytesJs(text: string): Uint8Array {
  const out = new Uint8Array(Math.floor((text.length * 3) / 4) + 3);
  let size = 0;
  let buffer = 0;
  let bits = 0;
  for (let i = 0; i < text.length; i++) {
    const code = text.charCodeAt(i);
    if (code === 61 /* = */) break;
    if (code === 32 || code === 9 || code === 10 || code === 13) continue;
    const value = code < 256 ? DECODE[code]! : -1;
    if (value < 0) throw new SyntaxError(`Invalid base64 character at position ${i}.`);
    buffer = ((buffer << 6) | value) & 0xffffff;
    bits += 6;
    if (bits >= 8) {
      bits -= 8;
      out[size++] = (buffer >>> bits) & 0xff;
    }
  }
  return out.subarray(0, size);
}
