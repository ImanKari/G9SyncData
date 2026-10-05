import { utf8Encode } from '../internal/utf8.js';
import { normalizeGuid } from './guid.js';
import { G9Long } from './long.js';

const UNRESERVED = /^[A-Za-z0-9\-._~]*$/;
const HEX = '0123456789ABCDEF';

/**
 * `Uri.EscapeDataString`: every character except the RFC 3986 unreserved set (`A-Z a-z 0-9 - . _ ~`) as `%XX` of its
 * UTF-8 bytes, upper-case hex; a lone surrogate becomes U+FFFD. (`encodeURIComponent` differs: it keeps `!'()*` and
 * throws on lone surrogates.)
 */
export function escapeDataString(text: string): string {
  if (UNRESERVED.test(text)) return text;
  const bytes = utf8Encode(text);
  let out = '';
  for (let i = 0; i < bytes.length; i++) {
    const b = bytes[i] as number;
    const unreserved =
      (b >= 65 && b <= 90) || (b >= 97 && b <= 122) || (b >= 48 && b <= 57) || b === 45 || b === 46 || b === 95 || b === 126;
    out += unreserved ? String.fromCharCode(b) : '%' + HEX.charAt(b >> 4) + HEX.charAt(b & 15);
  }
  return out;
}

/** Ordinal (UTF-16 code unit) comparison, `StringComparer.Ordinal`. */
export function compareOrdinal(a: string, b: string): number {
  return a < b ? -1 : a > b ? 1 : 0;
}

/**
 * Typed, canonicalised subscription parameters (twin of `G9CSyncParameters`). Values are normalised on the way in
 * (GUIDs lower-case, lists sorted and de-duplicated), so two spellings of the same parameters always produce the same
 * subscription key.
 */
export class G9SyncParameters {
  private readonly _values: { [name: string]: string } = Object.create(null) as { [name: string]: string };
  private _count = 0;

  /** Canonical name → value pairs, names in ordinal order. */
  get values(): { [name: string]: string } {
    const out: { [name: string]: string } = {};
    for (const name of this.names()) out[name] = this._values[name] as string;
    return out;
  }

  get count(): number {
    return this._count;
  }

  /** Parameter names in ordinal order. */
  names(): string[] {
    return Object.keys(this._values).sort(compareOrdinal);
  }

  get(name: string): string | undefined {
    return Object.prototype.hasOwnProperty.call(this._values, name) ? this._values[name] : undefined;
  }

  /** A GUID value (canonical lower-case "D"). */
  setGuid(name: string, value: string): this {
    return this._set(name, normalizeGuid(value));
  }

  /** An integer value (invariant decimal). */
  setInt64(name: string, value: number | G9Long): this {
    return this._set(name, (value instanceof G9Long ? value : G9Long.fromNumber(value)).toString());
  }

  /** Same as {@link setInt64} (`Set(string, int)`). */
  setInt32(name: string, value: number): this {
    return this.setInt64(name, value);
  }

  setBoolean(name: string, value: boolean): this {
    return this._set(name, value ? 'true' : 'false');
  }

  setString(name: string, value: string): this {
    if (typeof value !== 'string') throw new TypeError('A string value is required.');
    return this._set(name, value);
  }

  /** A GUID list: canonical texts, distinct, ordinal-sorted, comma-joined. */
  setGuidList(name: string, values: Iterable<string>): this {
    const list = distinctSorted(mapIterable(values, normalizeGuid), compareOrdinal);
    return this._set(name, list.join(','));
  }

  /** An integer list: distinct, numerically sorted, comma-joined. */
  setInt64List(name: string, values: Iterable<number | G9Long>): this {
    const longs = mapIterable(values, (v) => (v instanceof G9Long ? v : G9Long.fromNumber(v)));
    longs.sort((a, b) => a.compare(b));
    const out: string[] = [];
    let previous: G9Long | null = null;
    for (const value of longs) {
      if (previous === null || !previous.equals(value)) out.push(value.toString());
      previous = value;
    }
    return this._set(name, out.join(','));
  }

  /** A string list: distinct, ordinal-sorted, comma-joined. Values must not contain ','. */
  setStringList(name: string, values: Iterable<string>): this {
    const list = distinctSorted(mapIterable(values, (v) => v), compareOrdinal);
    for (const value of list) {
      if (value.indexOf(',') >= 0) throw new RangeError("String list values must not contain ','.");
    }
    return this._set(name, list.join(','));
  }

  /** Builds from wire values (trusted to be canonical only after server validation). */
  static fromValues(values: { [name: string]: string } | Iterable<[string, string]> | null | undefined): G9SyncParameters {
    const result = new G9SyncParameters();
    if (!values) return result;
    if (typeof (values as Iterable<[string, string]>)[Symbol.iterator] === 'function') {
      for (const [name, value] of values as Iterable<[string, string]>) result._set(name, value);
    } else {
      const record = values as { [name: string]: string };
      for (const name of Object.keys(record)) result._set(name, record[name] as string);
    }
    return result;
  }

  /** Stable text form `a=1&b=x`: names ordinal-sorted, names and values `Uri.EscapeDataString`-escaped. */
  toCanonicalString(): string {
    const parts: string[] = [];
    for (const name of this.names()) parts.push(escapeDataString(name) + '=' + escapeDataString(this._values[name] as string));
    return parts.join('&');
  }

  /** The subscription key: `name`, or `name?canonical-parameters`. */
  static buildSubscriptionKey(subscriptionName: string, parameters?: G9SyncParameters | null): string {
    if (typeof subscriptionName !== 'string' || subscriptionName.trim() === '') throw new TypeError('A subscription name is required.');
    return !parameters || parameters.count === 0 ? subscriptionName : subscriptionName + '?' + parameters.toCanonicalString();
  }

  private _set(name: string, value: string): this {
    if (typeof name !== 'string' || name.trim() === '') throw new TypeError('A parameter name is required.');
    if (!Object.prototype.hasOwnProperty.call(this._values, name)) this._count++;
    this._values[name] = value;
    return this;
  }
}

function mapIterable<T, R>(values: Iterable<T>, map: (value: T) => R): R[] {
  const out: R[] = [];
  for (const value of values) out.push(map(value));
  return out;
}

function distinctSorted(values: string[], compare: (a: string, b: string) => number): string[] {
  values.sort(compare);
  const out: string[] = [];
  for (let i = 0; i < values.length; i++) if (i === 0 || values[i] !== values[i - 1]) out.push(values[i] as string);
  return out;
}
