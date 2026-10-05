// Invariant-culture number text exactly as .NET writes it, where G9SyncData hashes or stores that text: a computed
// column's canonical text (G9CSyncDigest.CanonicalText) and the TEXT storage of decimals.

/**
 * `double.ToString(CultureInfo.InvariantCulture)` (.NET Core 3.0+): the shortest round-trip digits, in fixed notation
 * unless the decimal exponent is 17 or more, or below -4, then `d.dddE+XX` (the golden vectors pin both edges).
 */
export function formatDouble(value: number): string {
  if (value !== value) return 'NaN';
  if (value === Infinity) return 'Infinity';
  if (value === -Infinity) return '-Infinity';
  if (value === 0) return 1 / value < 0 ? '-0' : '0';
  const negative = value < 0;
  // toExponential() without an argument gives the shortest digits that round-trip, like .NET's.
  const text = Math.abs(value).toExponential();
  const e = text.indexOf('e');
  const digits = text.slice(0, e).replace('.', '');
  const exponent = parseInt(text.slice(e + 1), 10);
  const scale = exponent + 1;
  let out: string;
  if (scale > 17 || scale < -3) {
    const abs = Math.abs(exponent);
    out = digits.charAt(0) + (digits.length > 1 ? '.' + digits.slice(1) : '') + 'E' + (exponent < 0 ? '-' : '+') + (abs < 10 ? '0' : '') + abs;
  } else if (scale <= 0) {
    out = '0.' + repeat('0', -scale) + digits;
  } else if (scale >= digits.length) {
    out = digits + repeat('0', scale - digits.length);
  } else {
    out = digits.slice(0, scale) + '.' + digits.slice(scale);
  }
  return negative ? '-' + out : out;
}

function repeat(text: string, count: number): string {
  let out = '';
  for (let i = 0; i < count; i++) out += text;
  return out;
}

/** The parts of a .NET `decimal`: 96-bit magnitude as three 32-bit words, a scale 0..28 and a sign. */
export interface DecimalParts {
  lo: number;
  mid: number;
  hi: number;
  scale: number;
  negative: boolean;
}

/** Divides a 96-bit magnitude (three uint32 words, little-endian order) by a small divisor, in place; returns the remainder. */
function divide96(words: number[], divisor: number): number {
  let remainder = 0;
  for (let i = 2; i >= 0; i--) {
    // Two 16-bit steps keep every intermediate below 2^53.
    const word = words[i] as number;
    let v = remainder * 65536 + (word >>> 16);
    const qHigh = Math.floor(v / divisor);
    remainder = v - qHigh * divisor;
    v = remainder * 65536 + (word & 0xffff);
    const qLow = Math.floor(v / divisor);
    remainder = v - qLow * divisor;
    words[i] = ((qHigh << 16) | qLow) >>> 0;
  }
  return remainder;
}

/** `decimal.ToString(CultureInfo.InvariantCulture)`: every digit of the magnitude, the scale kept (`1.50`, `0.000`). */
export function formatDecimal(parts: DecimalParts): string {
  const words = [parts.lo >>> 0, parts.mid >>> 0, parts.hi >>> 0];
  let digits = '';
  while (words[0] !== 0 || words[1] !== 0 || words[2] !== 0) {
    const chunk = String(divide96(words, 10000000));
    digits = (words[0] !== 0 || words[1] !== 0 || words[2] !== 0 ? '0000000'.slice(chunk.length) + chunk : chunk) + digits;
  }
  if (digits === '') digits = '0';
  const scale = parts.scale;
  if (scale > 0) {
    while (digits.length <= scale) digits = '0' + digits;
    digits = digits.slice(0, digits.length - scale) + '.' + digits.slice(digits.length - scale);
  }
  const zero = parts.lo === 0 && parts.mid === 0 && parts.hi === 0;
  return parts.negative && !zero ? '-' + digits : digits;
}

const DOUBLE_POWERS_10 = [
  1, 1e1, 1e2, 1e3, 1e4, 1e5, 1e6, 1e7, 1e8, 1e9, 1e10, 1e11, 1e12, 1e13, 1e14, 1e15, 1e16, 1e17, 1e18, 1e19, 1e20, 1e21, 1e22, 1e23, 1e24, 1e25,
  1e26, 1e27, 1e28,
];

/**
 * `(double)decimal` exactly as .NET computes it (`DecCalc.VarR8FromDec`): `((double)low64 + (double)high · 2^64) /
 * 10^scale`, each step an IEEE double operation, so the result matches .NET to the bit (it is not always the correctly
 * rounded value, and must not be: the server compares what .NET stored).
 */
export function decimalToDouble(parts: DecimalParts): number {
  const low64 = (parts.mid >>> 0) * 4294967296 + (parts.lo >>> 0);
  const value = (low64 + (parts.hi >>> 0) * 18446744073709552000) / (DOUBLE_POWERS_10[parts.scale] as number);
  return parts.negative ? -value : value;
}

/**
 * Parses invariant decimal text (`decimal.Parse(text, NumberStyles.Number | NumberStyles.AllowExponent, Invariant)`
 * for the forms SQLite and .NET produce: sign, digits, one '.', an optional exponent). Digits past 28 significant are
 * rounded half to even… as .NET does (to the 96-bit limit). Returns null for text that is not a number.
 */
export function parseDecimal(text: string): DecimalParts | null {
  const m = /^\s*([+-]?)(\d*)(?:\.(\d*))?(?:[eE]([+-]?\d+))?\s*$/.exec(text);
  if (!m) return null;
  const intPart = m[2] ?? '';
  const fracPart = m[3] ?? '';
  if (intPart === '' && fracPart === '') return null;
  let digits = (intPart + fracPart).replace(/^0+/, '');
  let scale = fracPart.length - (m[4] ? parseInt(m[4], 10) : 0);
  if (digits === '') return { lo: 0, mid: 0, hi: 0, scale: Math.max(0, Math.min(28, scale)), negative: m[1] === '-' };
  // Negative scale: append zeros (the value is an integer).
  while (scale < 0) {
    digits += '0';
    scale++;
  }
  // Too many digits for 96 bits or a scale past 28: round away the excess, half to even.
  while (scale > 28 || !fits96(digits)) {
    if (scale === 0) return null; // overflow
    const last = digits.charCodeAt(digits.length - 1) - 48;
    digits = digits.slice(0, -1);
    scale--;
    if (last > 5 || (last === 5 && (digits.length === 0 || (digits.charCodeAt(digits.length - 1) - 48) % 2 === 1))) digits = incrementDigits(digits);
    if (digits === '') digits = '0';
  }
  const words = toWords(digits);
  return { lo: words[0] as number, mid: words[1] as number, hi: words[2] as number, scale, negative: m[1] === '-' };
}

function incrementDigits(digits: string): string {
  const chars = digits.split('');
  let i = chars.length - 1;
  while (i >= 0) {
    if (chars[i] === '9') {
      chars[i] = '0';
      i--;
    } else {
      chars[i] = String.fromCharCode((chars[i] as string).charCodeAt(0) + 1);
      return chars.join('');
    }
  }
  return '1' + chars.join('');
}

const MAX_96 = '79228162514264337593543950335';

function fits96(digits: string): boolean {
  const d = digits.replace(/^0+/, '');
  return d.length < MAX_96.length || (d.length === MAX_96.length && d <= MAX_96);
}

function toWords(digits: string): number[] {
  // 16-bit limbs, little-endian; value = value * 10 + digit.
  const limbs = [0, 0, 0, 0, 0, 0];
  for (let i = 0; i < digits.length; i++) {
    let carry = digits.charCodeAt(i) - 48;
    for (let k = 0; k < 6; k++) {
      const v = (limbs[k] as number) * 10 + carry;
      limbs[k] = v & 0xffff;
      carry = Math.floor(v / 65536);
    }
  }
  return [
    (((limbs[1] as number) << 16) | (limbs[0] as number)) >>> 0,
    (((limbs[3] as number) << 16) | (limbs[2] as number)) >>> 0,
    (((limbs[5] as number) << 16) | (limbs[4] as number)) >>> 0,
  ];
}

/**
 * `(decimal)double` exactly as .NET converts it (`new decimal(double)`, VarDecFromR8): the value rounded to 15
 * significant digits, then trailing zeros dropped. Throws `RangeError` for NaN, infinities and |value| ≥ 7.9e28.
 */
export function doubleToDecimal(value: number): DecimalParts {
  if (!isFinite(value)) throw new RangeError('Value was either too large or too small for a Decimal.');
  if (value === 0) return { lo: 0, mid: 0, hi: 0, scale: 0, negative: false };
  if (Math.abs(value) >= 7.9228162514264338e28) throw new RangeError('Value was either too large or too small for a Decimal.');
  if (Math.abs(value) < 1e-28) return { lo: 0, mid: 0, hi: 0, scale: 0, negative: value < 0 };
  // 15 significant digits (toExponential(14) rounds half-even on the exact binary value, like .NET's double→decimal).
  const text = Math.abs(value).toExponential(14);
  const e = text.indexOf('e');
  let digits = text.slice(0, e).replace('.', '');
  const exponent = parseInt(text.slice(e + 1), 10);
  digits = digits.replace(/0+$/, '');
  if (digits === '') digits = '0';
  // value = digits × 10^(exponent − (digits.length − 1))
  let scale = digits.length - 1 - exponent;
  if (scale > 28) {
    // Fewer digits fit: drop to scale 28 with rounding.
    const parsed = parseDecimal(digits + 'e' + String(-scale));
    if (!parsed) throw new RangeError('Value was either too large or too small for a Decimal.');
    parsed.negative = value < 0;
    return parsed;
  }
  while (scale < 0) {
    digits += '0';
    scale++;
  }
  const words = toWords(digits);
  return { lo: words[0] as number, mid: words[1] as number, hi: words[2] as number, scale, negative: value < 0 };
}
