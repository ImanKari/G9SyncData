// GUID text and bytes, the twins of the .NET forms G9SyncData uses: canonical text is lower-case "D"
// (`xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx`, PostgreSQL's uuid::text), and the wire bytes are the big-endian (RFC 4122)
// order of that text (`Guid.TryWriteBytes(bigEndian: true)`).

const HEX_DIGIT = /^[0-9a-fA-F]{32}$/;

/** The nil GUID in canonical form. */
export const EMPTY_GUID = '00000000-0000-0000-0000-000000000000';

/**
 * Parses a GUID the way `Guid.TryParse` reads the forms G9SyncData meets — "D" (`8-4-4-4-12`), "N" (32 digits),
 * "B" (`{D}`) and "P" (`(D)`), any case, surrounding white space ignored — and returns its 32 lower-case hex digits,
 * or null. (The rare "X" form `{0x…,{0x…}}` is not accepted; PARITY.md.)
 */
export function guidDigits(text: string | null | undefined): string | null {
  if (typeof text !== 'string') return null;
  let s = text.trim();
  if (s.length === 38 && ((s.charAt(0) === '{' && s.charAt(37) === '}') || (s.charAt(0) === '(' && s.charAt(37) === ')'))) {
    s = s.slice(1, 37);
  }
  let digits: string;
  if (s.length === 36) {
    if (s.charAt(8) !== '-' || s.charAt(13) !== '-' || s.charAt(18) !== '-' || s.charAt(23) !== '-') return null;
    digits = s.slice(0, 8) + s.slice(9, 13) + s.slice(14, 18) + s.slice(19, 23) + s.slice(24);
  } else if (s.length === 32) {
    digits = s;
  } else {
    return null;
  }
  return HEX_DIGIT.test(digits) ? digits.toLowerCase() : null;
}

/** Canonical lower-case "D" text of 32 hex digits. */
function formatDigits(digits: string): string {
  return digits.slice(0, 8) + '-' + digits.slice(8, 12) + '-' + digits.slice(12, 16) + '-' + digits.slice(16, 20) + '-' + digits.slice(20);
}

/** Canonical text of a GUID (`Guid.Parse(text).ToString("D")`), or null when `text` is not a GUID. */
export function tryNormalizeGuid(text: string | null | undefined): string | null {
  const digits = guidDigits(text);
  return digits === null ? null : formatDigits(digits);
}

/** Canonical text of a GUID; throws `SyntaxError` (the .NET `FormatException`) when `text` is not one. */
export function normalizeGuid(text: string): string {
  const canonical = tryNormalizeGuid(text);
  if (canonical === null) throw new SyntaxError(`'${text}' is not a GUID.`);
  return canonical;
}

/** "N" form: 32 lower-case hex digits (`Guid.ToString("N")`). */
export function guidToN(text: string): string {
  const digits = guidDigits(text);
  if (digits === null) throw new SyntaxError(`'${text}' is not a GUID.`);
  return digits;
}

/** Writes the 16 big-endian bytes of a GUID. */
export function writeGuidBytes(text: string, bytes: Uint8Array, offset: number): void {
  const digits = guidDigits(text);
  if (digits === null) throw new SyntaxError(`'${text}' is not a GUID.`);
  for (let i = 0; i < 16; i++) bytes[offset + i] = parseInt(digits.substr(i * 2, 2), 16);
}

const HEX = '0123456789abcdef';

/** Canonical text of 16 big-endian GUID bytes. */
export function readGuidBytes(bytes: Uint8Array, offset: number): string {
  let digits = '';
  for (let i = 0; i < 16; i++) {
    const b = bytes[offset + i] as number;
    digits += HEX.charAt(b >> 4) + HEX.charAt(b & 15);
  }
  return formatDigits(digits);
}

/**
 * A random (version 4) GUID in canonical form, the `Guid.NewGuid()` twin. Uses `crypto.getRandomValues` where the
 * runtime has it (Lynx's background thread has no `crypto`; it falls back to `Math.random`, which is enough for ids that
 * are unique, not secret).
 */
export function newGuid(): string {
  const bytes = new Uint8Array(16);
  const cryptoObject = (globalThis as { crypto?: { getRandomValues?: (array: Uint8Array) => Uint8Array } }).crypto;
  if (cryptoObject && typeof cryptoObject.getRandomValues === 'function') {
    cryptoObject.getRandomValues(bytes);
  } else {
    for (let i = 0; i < 16; i++) bytes[i] = Math.floor(Math.random() * 256);
  }
  bytes[6] = ((bytes[6] as number) & 0x0f) | 0x40;
  bytes[8] = ((bytes[8] as number) & 0x3f) | 0x80;
  return readGuidBytes(bytes, 0);
}
