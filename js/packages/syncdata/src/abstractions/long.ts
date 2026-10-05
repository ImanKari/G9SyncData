/**
 * An exact signed 64-bit integer without BigInt (Lynx's PrimJS does not guarantee it): two 32-bit halves, `hi` signed
 * and `lo` unsigned. It is the twin of .NET `long` wherever a value may exceed ±(2^53 − 1) and must stay exact:
 * reconciliation digests and row hashes, dirty masks (up to 62 bits), DateTime ticks, int64 column values.
 *
 * Everything else in the protocol that .NET types as `long` (versions, batch ids, row counts, byte counts) is a plain
 * `number` in TypeScript: those values are far below 2^53 in practice.
 */
export class G9Long {
  /** High 32 bits, signed (two's complement). */
  readonly hi: number;
  /** Low 32 bits, unsigned. */
  readonly lo: number;

  constructor(hi: number, lo: number) {
    this.hi = hi | 0;
    this.lo = lo >>> 0;
  }

  static readonly ZERO = new G9Long(0, 0);
  static readonly ONE = new G9Long(0, 1);
  static readonly MINUS_ONE = new G9Long(-1, 0xffffffff);
  static readonly MIN_VALUE = new G9Long(0x80000000 | 0, 0);
  static readonly MAX_VALUE = new G9Long(0x7fffffff, 0xffffffff);

  /** From any integer `number`. Exact for safe integers; larger magnitudes are taken from the double as it is. */
  static fromNumber(value: number): G9Long {
    if (!isFinite(value) || Math.floor(value) !== value) throw new RangeError(`${value} is not an integer.`);
    if (value >= 9223372036854775807) return G9Long.MAX_VALUE;
    if (value <= -9223372036854775808) return G9Long.MIN_VALUE;
    if (value < 0) return G9Long.fromNumber(-value).negate();
    const hi = Math.floor(value / TWO_32);
    return new G9Long(hi, value - hi * TWO_32);
  }

  /** From the two halves (hi signed or unsigned, lo unsigned or signed: both are normalised). */
  static fromBits(hi: number, lo: number): G9Long {
    return new G9Long(hi, lo);
  }

  /**
   * Parses invariant decimal text (optional sign, digits only, no white space), the .NET
   * `long.Parse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture)` twin. Throws `RangeError` for anything
   * else or a value outside the int64 range.
   */
  static parse(text: string): G9Long {
    const parsed = G9Long.tryParse(text);
    if (!parsed) throw new RangeError(`'${text}' is not a 64-bit integer.`);
    return parsed;
  }

  /** {@link parse} that returns null instead of throwing. */
  static tryParse(text: string | null | undefined): G9Long | null {
    if (typeof text !== 'string') return null;
    const m = /^([+-]?)(\d+)$/.exec(text);
    if (!m) return null;
    const negative = m[1] === '-';
    const digits = m[2] as string;
    // Four 16-bit limbs, little-endian; value = value * 10 + digit, failing past 2^64.
    const limbs = [0, 0, 0, 0];
    for (let i = 0; i < digits.length; i++) {
      let carry = digits.charCodeAt(i) - 48;
      for (let k = 0; k < 4; k++) {
        const v = (limbs[k] as number) * 10 + carry;
        limbs[k] = v & 0xffff;
        carry = Math.floor(v / 65536);
      }
      if (carry !== 0) return null;
    }
    const hiU = ((limbs[3] as number) * 65536 + (limbs[2] as number)) >>> 0;
    const lo = ((limbs[1] as number) * 65536 + (limbs[0] as number)) >>> 0;
    // Range: magnitude ≤ 2^63 − 1, or exactly 2^63 when negative.
    if (hiU > 0x7fffffff) {
      if (!(negative && hiU === 0x80000000 && lo === 0)) return null;
      return G9Long.MIN_VALUE;
    }
    const magnitude = new G9Long(hiU, lo);
    return negative ? magnitude.negate() : magnitude;
  }

  /** Coerces a number, decimal text or G9Long. */
  static from(value: G9Long | number | string | { int64: string }): G9Long {
    if (value instanceof G9Long) return value;
    if (typeof value === 'number') return G9Long.fromNumber(value);
    if (typeof value === 'string') return G9Long.parse(value);
    if (value && typeof value === 'object' && typeof (value as { int64?: unknown }).int64 === 'string') {
      return G9Long.parse((value as { int64: string }).int64);
    }
    throw new TypeError('Not a 64-bit integer.');
  }

  /** Reads 8 little-endian bytes. */
  static readLE(bytes: Uint8Array, offset: number): G9Long {
    const lo = (bytes[offset] as number) | ((bytes[offset + 1] as number) << 8) | ((bytes[offset + 2] as number) << 16) | ((bytes[offset + 3] as number) << 24);
    const hi =
      (bytes[offset + 4] as number) | ((bytes[offset + 5] as number) << 8) | ((bytes[offset + 6] as number) << 16) | ((bytes[offset + 7] as number) << 24);
    return new G9Long(hi, lo);
  }

  /** Reads 8 big-endian bytes. */
  static readBE(bytes: Uint8Array, offset: number): G9Long {
    const hi = ((bytes[offset] as number) << 24) | ((bytes[offset + 1] as number) << 16) | ((bytes[offset + 2] as number) << 8) | (bytes[offset + 3] as number);
    const lo =
      ((bytes[offset + 4] as number) << 24) | ((bytes[offset + 5] as number) << 16) | ((bytes[offset + 6] as number) << 8) | (bytes[offset + 7] as number);
    return new G9Long(hi, lo);
  }

  /** Writes 8 little-endian bytes. */
  writeLE(bytes: Uint8Array, offset: number): void {
    const lo = this.lo;
    const hi = this.hi;
    bytes[offset] = lo & 0xff;
    bytes[offset + 1] = (lo >>> 8) & 0xff;
    bytes[offset + 2] = (lo >>> 16) & 0xff;
    bytes[offset + 3] = (lo >>> 24) & 0xff;
    bytes[offset + 4] = hi & 0xff;
    bytes[offset + 5] = (hi >>> 8) & 0xff;
    bytes[offset + 6] = (hi >>> 16) & 0xff;
    bytes[offset + 7] = (hi >>> 24) & 0xff;
  }

  isZero(): boolean {
    return this.hi === 0 && this.lo === 0;
  }

  isNegative(): boolean {
    return this.hi < 0;
  }

  /** True when the value lies within ±(2^53 − 1), i.e. {@link toNumber} is exact. */
  isSafe(): boolean {
    const hi = this.hi;
    if (hi >= 0) return hi < 0x200000;
    // -2^53 < v  ⇔  hi > -0x200000, or hi == -0x200000 and lo > 0.
    return hi > -0x200000 || (hi === -0x200000 && this.lo > 0);
  }

  /** The value as a number: exact when {@link isSafe}, the nearest double otherwise. */
  toNumber(): number {
    return this.hi * TWO_32 + this.lo;
  }

  equals(other: G9Long): boolean {
    return this.hi === other.hi && this.lo === other.lo;
  }

  /** Signed comparison: negative, zero or positive. */
  compare(other: G9Long): number {
    if (this.hi !== other.hi) return this.hi < other.hi ? -1 : 1;
    if (this.lo !== other.lo) return this.lo < other.lo ? -1 : 1;
    return 0;
  }

  negate(): G9Long {
    return this.not().add(G9Long.ONE);
  }

  not(): G9Long {
    return new G9Long(~this.hi, ~this.lo);
  }

  and(other: G9Long): G9Long {
    return new G9Long(this.hi & other.hi, this.lo & other.lo);
  }

  or(other: G9Long): G9Long {
    return new G9Long(this.hi | other.hi, this.lo | other.lo);
  }

  xor(other: G9Long): G9Long {
    return new G9Long(this.hi ^ other.hi, this.lo ^ other.lo);
  }

  /** Wrapping addition (two's complement), like unchecked C#. */
  add(other: G9Long): G9Long {
    const lo = this.lo + other.lo;
    const carry = lo >= TWO_32 ? 1 : 0;
    return new G9Long((this.hi + other.hi + carry) | 0, lo >>> 0);
  }

  subtract(other: G9Long): G9Long {
    return this.add(other.negate());
  }

  /** Wrapping multiplication by a small non-negative integer (< 2^21). */
  multiplySmall(factor: number): G9Long {
    // 16-bit limbs keep every partial product below 2^37.
    const l0 = (this.lo & 0xffff) * factor;
    const l1 = (this.lo >>> 16) * factor + Math.floor(l0 / 65536);
    const l2 = (this.hi & 0xffff) * factor + Math.floor(l1 / 65536);
    const l3 = (this.hi >>> 16) * factor + Math.floor(l2 / 65536);
    return new G9Long(((l3 & 0xffff) << 16) | (l2 & 0xffff), ((l1 & 0xffff) << 16) | (l0 & 0xffff));
  }

  /** Shift left (C# `<<` with the count masked to 0..63). */
  shiftLeft(count: number): G9Long {
    const n = count & 63;
    if (n === 0) return this;
    if (n >= 32) return new G9Long(this.lo << (n - 32), 0);
    return new G9Long((this.hi << n) | (this.lo >>> (32 - n)), this.lo << n);
  }

  /** Arithmetic shift right (C# `>>` on long). */
  shiftRight(count: number): G9Long {
    const n = count & 63;
    if (n === 0) return this;
    if (n >= 32) return new G9Long(this.hi >> 31, this.hi >> (n - 32));
    return new G9Long(this.hi >> n, (this.lo >>> n) | (this.hi << (32 - n)));
  }

  /** Logical shift right (C# `>>>` / ulong `>>`). */
  shiftRightUnsigned(count: number): G9Long {
    const n = count & 63;
    if (n === 0) return this;
    if (n >= 32) return new G9Long(0, this.hi >>> (n - 32));
    return new G9Long(this.hi >>> n, (this.lo >>> n) | (this.hi << (32 - n)));
  }

  /** True when bit `index` (0..63) is set. */
  testBit(index: number): boolean {
    return index >= 32 ? ((this.hi >>> (index - 32)) & 1) !== 0 : ((this.lo >>> index) & 1) !== 0;
  }

  /**
   * Divides the value as UNSIGNED 64 bits by a small divisor (1..2^21) and returns quotient and remainder. Used for
   * decimal formatting and tick arithmetic.
   */
  divModUnsignedSmall(divisor: number): { quotient: G9Long; remainder: number } {
    const limbs = [this.hi >>> 16, this.hi & 0xffff, this.lo >>> 16, this.lo & 0xffff];
    let remainder = 0;
    for (let i = 0; i < 4; i++) {
      const value = remainder * 65536 + (limbs[i] as number);
      limbs[i] = Math.floor(value / divisor);
      remainder = value - (limbs[i] as number) * divisor;
    }
    return {
      quotient: new G9Long((((limbs[0] as number) << 16) | (limbs[1] as number)) >>> 0, (((limbs[2] as number) << 16) | (limbs[3] as number)) >>> 0),
      remainder,
    };
  }

  /** Invariant decimal text, the .NET `long.ToString(CultureInfo.InvariantCulture)` twin. */
  toString(): string {
    if (this.isSafe()) return String(this.toNumber());
    if (this.equals(G9Long.MIN_VALUE)) return '-9223372036854775808';
    const negative = this.isNegative();
    let magnitude: G9Long = negative ? this.negate() : this;
    let out = '';
    while (!magnitude.isZero()) {
      const { quotient, remainder } = magnitude.divModUnsignedSmall(10000000);
      magnitude = quotient;
      const chunk = String(remainder);
      out = (magnitude.isZero() ? chunk : '0000000'.slice(chunk.length) + chunk) + out;
    }
    return negative ? '-' + out : out;
  }

  /** The value as UNSIGNED decimal text (C# `ulong`). */
  toUnsignedString(): string {
    if (!this.isNegative()) return this.toString();
    let magnitude: G9Long = this;
    let out = '';
    while (!magnitude.isZero()) {
      const { quotient, remainder } = magnitude.divModUnsignedSmall(10000000);
      magnitude = quotient;
      const chunk = String(remainder);
      out = (magnitude.isZero() ? chunk : '0000000'.slice(chunk.length) + chunk) + out;
    }
    return out;
  }

  /**
   * JSON form: a sentinel string that {@link stringifyJson} turns into a bare JSON number, so `JSON.stringify` of a DTO
   * holding a G9Long writes the exact integer.
   */
  toJSON(): string {
    return LONG_SENTINEL + this.toString();
  }

  /** The SQLite driver form (G9LynxControls `G9SqlValue`): a number when safe, `{ int64 }` otherwise. */
  toSqlValue(): number | { int64: string } {
    return this.isSafe() ? this.toNumber() : { int64: this.toString() };
  }
}

const TWO_32 = 4294967296;

/** Marks a G9Long inside `JSON.stringify` output (a NUL, so no ordinary text produces it). */
export const LONG_SENTINEL = '\u0000g9long:';
const LONG_SENTINEL_JSON = /"\\u0000g9long:(-?\d+)"/g;

/** `JSON.stringify` that writes every {@link G9Long} as an exact JSON number. */
export function stringifyJson(value: unknown): string {
  return replaceLongSentinels(JSON.stringify(value));
}

/** Turns the G9Long sentinels in `JSON.stringify` output (G9Long.toJSON) into bare JSON numbers. */
export function replaceLongSentinels(text: string): string {
  return text.indexOf('\\u0000g9long:') < 0 ? text : text.replace(LONG_SENTINEL_JSON, '$1');
}

/**
 * `JSON.parse` that keeps integers outside ±(2^53 − 1) exact: they come back as {@link G9Long} instead of a rounded
 * number. Text without such an integer takes the plain `JSON.parse` path.
 */
export function parseJson(text: string): unknown {
  if (!/\d{16}/.test(text)) return JSON.parse(text);
  // Quote the long integer literals OUTSIDE strings, tagged, then let JSON.parse do the rest.
  let out = '';
  let last = 0;
  let i = 0;
  const n = text.length;
  while (i < n) {
    const c = text.charCodeAt(i);
    if (c === 34) {
      // Skip a string, honouring escapes.
      i++;
      while (i < n) {
        const d = text.charCodeAt(i);
        if (d === 92) i += 2;
        else if (d === 34) break;
        else i++;
      }
      i++;
      continue;
    }
    if (c === 45 || (c >= 48 && c <= 57)) {
      const start = i;
      i++;
      while (i < n) {
        const d = text.charCodeAt(i);
        if ((d >= 48 && d <= 57) || d === 46 || d === 101 || d === 69 || d === 43 || d === 45) i++;
        else break;
      }
      const literal = text.slice(start, i);
      if (/^-?\d{16,}$/.test(literal)) {
        const parsed = G9Long.tryParse(literal);
        if (parsed && !parsed.isSafe()) {
          // An escaped U+0001 in the JSON text: JSON.parse turns it into the tag the reviver looks for.
          out += text.slice(last, start) + '"\\u0001g9long:' + literal + '"';
          last = i;
        }
      }
      continue;
    }
    i++;
  }
  if (last === 0) return JSON.parse(text);
  out += text.slice(last);
  return JSON.parse(out, (_key, value: unknown) =>
    typeof value === 'string' && value.indexOf(LONG_SENTINEL_TEXT) === 0 ? G9Long.parse(value.slice(LONG_SENTINEL_TEXT.length)) : value,
  );
}

/** The tag parseJson puts in front of a long literal (a code point no JSON integer literal contains). */
const LONG_SENTINEL_TEXT = '\u0001g9long:';

/** A protocol `long` read as a number (exact when it fits; a G9Long rounds to the nearest double). */
export function longToNumber(value: unknown): number {
  if (typeof value === 'number') return value;
  if (value instanceof G9Long) return value.toNumber();
  if (typeof value === 'string' && value !== '') return Number(value);
  return 0;
}
