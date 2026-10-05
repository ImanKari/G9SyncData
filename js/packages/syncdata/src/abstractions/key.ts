import { G9ESyncColumnType } from './enums.js';
import { tryNormalizeGuid } from './guid.js';
import { G9Long } from './long.js';

/**
 * Canonical primary-key text (twin of `G9CSyncKey`). Every place a key is compared, hashed or stored as text uses this
 * form, so the same row can never appear under two spellings. v1 supports single-column keys of type Guid, String,
 * Int32 and Int64 (ADR-0006).
 */
export const G9SyncKey = {
  /** True when `type` can be a primary key. */
  isSupportedKeyType(type: number): boolean {
    return type === G9ESyncColumnType.Guid || type === G9ESyncColumnType.String || type === G9ESyncColumnType.Int32 || type === G9ESyncColumnType.Int64;
  },

  /** Canonical text of an integer key (invariant decimal, PostgreSQL `bigint::text`). */
  fromInt64(value: number | G9Long): string {
    return value instanceof G9Long ? value.toString() : G9Long.fromNumber(value).toString();
  },

  /** Normalises a textual key of the given type to canonical form; throws `SyntaxError` (FormatException) when invalid. */
  normalize(keyType: number, key: string): string {
    const canonical = G9SyncKey.tryNormalize(keyType, key);
    if (canonical === null) {
      if (!G9SyncKey.isSupportedKeyType(keyType)) throw new RangeError(`Unsupported key type ${keyType}.`);
      throw new SyntaxError(`'${key}' is not a valid key of type ${keyType}.`);
    }
    return canonical;
  },

  /** Tries to normalise; null instead of throwing. */
  tryNormalize(keyType: number, key: string | null | undefined): string | null {
    if (typeof key !== 'string') return null;
    switch (keyType) {
      case G9ESyncColumnType.Guid:
        return tryNormalizeGuid(key);
      case G9ESyncColumnType.Int32:
      case G9ESyncColumnType.Int64: {
        const parsed = G9Long.tryParse(key);
        return parsed === null ? null : parsed.toString();
      }
      case G9ESyncColumnType.String:
        return key;
      default:
        return null;
    }
  },
};
