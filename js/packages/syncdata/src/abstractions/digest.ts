import { formatDouble } from '../internal/dotnetFormat.js';
import { md5 } from '../internal/md5.js';
import { utf8Encode } from '../internal/utf8.js';
import { G9ESyncColumnType } from './enums.js';
import { tryNormalizeGuid } from './guid.js';
import { G9Long } from './long.js';

const HASH = new Uint8Array(16);

/**
 * The reconciliation hash (twin of `G9CSyncDigest`), defined so PostgreSQL computes the same numbers in plain SQL:
 * bucket = first byte of md5(key); row hash = first 8 bytes (big-endian, signed) of md5(`key:version[:computed…]`);
 * bucket digest = XOR of the row hashes in the bucket.
 */
export const G9SyncDigest = {
  /** Number of first-level buckets. */
  BucketCount: 256,

  /** Bucket of a canonical key. */
  getBucket(canonicalKey: string): number {
    md5(utf8Encode(canonicalKey), HASH);
    return HASH[0] as number;
  },

  /** Bucket of a canonical key given as UTF-8. */
  getBucketUtf8(canonicalKeyUtf8: Uint8Array): number {
    md5(canonicalKeyUtf8, HASH);
    return HASH[0] as number;
  },

  /**
   * Row hash of a canonical key, its server version and optional computed-column canonical texts (a null value
   * contributes an empty field, like .NET's `StringBuilder.Append(null)`).
   */
  getRowHash(canonicalKey: string, version: number | G9Long, computedValues?: readonly (string | null)[]): G9Long {
    let text = canonicalKey + ':' + (version instanceof G9Long ? version.toString() : G9Long.fromNumber(version).toString());
    if (computedValues) for (let i = 0; i < computedValues.length; i++) text += ':' + (computedValues[i] ?? '');
    md5(utf8Encode(text), HASH);
    return G9Long.readBE(HASH, 0);
  },

  /**
   * Canonical text of a computed value, identical to what the server SQL produces (`G9CSyncDigest.CanonicalText`).
   * `value` is what the SQLite driver read: null, a number, an `{ int64 }` tag or {@link G9Long}, text, or a BLOB.
   * `storage` says whether a number came from an INTEGER or a REAL cell (JavaScript cannot tell 5 from 5.0, .NET
   * formats them alike, but 1e15 as a REAL is `1E+15` and as an INTEGER `1000000000000000`); without it, integral
   * numbers count as INTEGER.
   */
  canonicalText(type: number, value: unknown, storage?: 'integer' | 'real'): string | null {
    if (value === null || value === undefined) return null;
    if (typeof value === 'boolean') return value ? '1' : '0';
    if (value instanceof G9Long || (typeof value === 'object' && value !== null && typeof (value as { int64?: unknown }).int64 === 'string')) {
      const long = G9Long.from(value as G9Long);
      if (type === G9ESyncColumnType.Boolean) return long.isZero() ? '0' : '1';
      return long.toString();
    }
    if (typeof value === 'number') {
      const integer = storage === 'integer' || (storage === undefined && Math.floor(value) === value && Math.abs(value) <= 9007199254740991);
      if (integer) {
        if (type === G9ESyncColumnType.Boolean) return value !== 0 ? '1' : '0';
        return G9Long.fromNumber(value).toString();
      }
      return formatDouble(value);
    }
    if (typeof value === 'string') {
      if (type === G9ESyncColumnType.Guid) {
        const guid = tryNormalizeGuid(value);
        if (guid !== null) return guid;
      }
      return value;
    }
    // A BLOB: .NET's object.ToString() of a byte[].
    return 'System.Byte[]';
  },
};
