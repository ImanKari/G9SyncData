// UTF-8 without TextEncoder/TextDecoder: Lynx's background thread (PrimJS) has neither. The native codecs are used
// where they exist; the fallbacks follow the WHATWG encoder/decoder (lone surrogates become U+FFFD, invalid bytes
// decode to U+FFFD), so both paths produce the same bytes and text.

interface Encoder {
  encode(text: string): Uint8Array;
}
interface Decoder {
  decode(bytes: Uint8Array): string;
}

function nativeEncoder(): Encoder | null {
  try {
    const ctor = (globalThis as { TextEncoder?: new () => Encoder }).TextEncoder;
    return typeof ctor === 'function' ? new ctor() : null;
  } catch {
    return null;
  }
}

function nativeDecoder(): Decoder | null {
  try {
    const ctor = (globalThis as { TextDecoder?: new (label?: string) => Decoder }).TextDecoder;
    return typeof ctor === 'function' ? new ctor('utf-8') : null;
  } catch {
    return null;
  }
}

const encoder = nativeEncoder();
const decoder = nativeDecoder();

/** UTF-8 bytes of `text`. */
export function utf8Encode(text: string): Uint8Array {
  if (encoder) return encoder.encode(text);
  return utf8EncodeJs(text);
}

/** Text of UTF-8 `bytes`. */
export function utf8Decode(bytes: Uint8Array): string {
  if (decoder) return decoder.decode(bytes);
  return utf8DecodeJs(bytes);
}

/** The pure-JS encoder (exported for tests; use {@link utf8Encode}). */
export function utf8EncodeJs(text: string): Uint8Array {
  const out = new Uint8Array(text.length * 3);
  let p = 0;
  for (let i = 0; i < text.length; i++) {
    let c = text.charCodeAt(i);
    if (c < 0x80) {
      out[p++] = c;
      continue;
    }
    if (c < 0x800) {
      out[p++] = 0xc0 | (c >> 6);
      out[p++] = 0x80 | (c & 63);
      continue;
    }
    if (c >= 0xd800 && c <= 0xdfff) {
      const next = i + 1 < text.length ? text.charCodeAt(i + 1) : 0;
      if (c <= 0xdbff && next >= 0xdc00 && next <= 0xdfff) {
        c = 0x10000 + ((c - 0xd800) << 10) + (next - 0xdc00);
        i++;
        out[p++] = 0xf0 | (c >> 18);
        out[p++] = 0x80 | ((c >> 12) & 63);
        out[p++] = 0x80 | ((c >> 6) & 63);
        out[p++] = 0x80 | (c & 63);
        continue;
      }
      c = 0xfffd; // lone surrogate
    }
    out[p++] = 0xe0 | (c >> 12);
    out[p++] = 0x80 | ((c >> 6) & 63);
    out[p++] = 0x80 | (c & 63);
  }
  return out.slice(0, p);
}

/** The pure-JS decoder, the WHATWG UTF-8 decode algorithm (exported for tests; use {@link utf8Decode}). */
export function utf8DecodeJs(bytes: Uint8Array): string {
  const parts: string[] = [];
  let units: number[] = [];
  const emit = (cp: number): void => {
    if (cp >= 0x10000) {
      cp -= 0x10000;
      units.push(0xd800 + (cp >> 10), 0xdc00 + (cp & 0x3ff));
    } else {
      units.push(cp);
    }
    if (units.length >= 8192) {
      parts.push(String.fromCharCode.apply(null, units));
      units = [];
    }
  };

  let codePoint = 0;
  let needed = 0;
  let seen = 0;
  let lower = 0x80;
  let upper = 0xbf;
  for (let i = 0; i < bytes.length; i++) {
    const b = bytes[i]!;
    if (needed === 0) {
      if (b <= 0x7f) emit(b);
      else if (b >= 0xc2 && b <= 0xdf) {
        needed = 1;
        codePoint = b & 0x1f;
      } else if (b >= 0xe0 && b <= 0xef) {
        if (b === 0xe0) lower = 0xa0;
        if (b === 0xed) upper = 0x9f;
        needed = 2;
        codePoint = b & 0x0f;
      } else if (b >= 0xf0 && b <= 0xf4) {
        if (b === 0xf0) lower = 0x90;
        if (b === 0xf4) upper = 0x8f;
        needed = 3;
        codePoint = b & 0x07;
      } else emit(0xfffd);
      continue;
    }
    if (b < lower || b > upper) {
      // Not a valid continuation: the sequence so far is one error, and this byte is processed again on its own.
      codePoint = needed = seen = 0;
      lower = 0x80;
      upper = 0xbf;
      emit(0xfffd);
      i--;
      continue;
    }
    lower = 0x80;
    upper = 0xbf;
    codePoint = (codePoint << 6) | (b & 0x3f);
    if (++seen === needed) {
      emit(codePoint);
      codePoint = needed = seen = 0;
    }
  }
  if (needed !== 0) emit(0xfffd);
  parts.push(String.fromCharCode.apply(null, units));
  return parts.join('');
}
