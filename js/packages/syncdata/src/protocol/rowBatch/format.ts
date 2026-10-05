// Twins of G9SyncData.Protocol/RowBatch/G9RowBatchTypes.cs and G9Varint.cs.
import { G9Long } from '../../abstractions/long.js';

/** What the rows of a G9RowBatch section mean (`G9ERowBatchOp`). */
export const G9ERowBatchOp = {
  /** Key, version, full negotiated column values. Insert or update. */
  Upsert: 1,
  /** Key and version of a deleted row. */
  Tombstone: 2,
  /** Key and version of a row that left the chunk's subscription. */
  MoveOut: 3,
  /** Key, version and row hash (reconciliation listing). */
  KeyVersions: 4,
  /** Client upload rows: key, op, local sequence, base version, dirty mask, HLC ms, values for upserts. */
  Push: 5,
  /** Key, version and only the backfilled columns of rows the client already holds. */
  Backfill: 6,
} as const;
export type G9ERowBatchOp = (typeof G9ERowBatchOp)[keyof typeof G9ERowBatchOp];

/** Operation of a push row (`G9EPushRowOp`). */
export const G9EPushRowOp = {
  Upsert: 1,
  Delete: 2,
} as const;
export type G9EPushRowOp = (typeof G9EPushRowOp)[keyof typeof G9EPushRowOp];

/** Frame body compression (`G9ECompression`). */
export const G9ECompression = {
  None: 0,
  Brotli: 1,
} as const;
export type G9ECompression = (typeof G9ECompression)[keyof typeof G9ECompression];

/** Layout constants (`G9RowBatchFormat`). */
export const G9RowBatchFormat = {
  /** Row-wise body. */
  Version: 1,
  /** Column-grouped body (ADR-0040). */
  ColumnVersion: 2,
  MaxVersion: 2,
  HeaderSize: 10,
  MaxBodyBytes: 512 * 1024 * 1024,
  /** The largest body any reader decodes: the writer's ceiling plus the column layout's allowance. */
  MaxDecodedBytes: 512 * 1024 * 1024 + 64 * 1024 * 1024,
} as const;

export function isRowBatchOp(op: number): op is G9ERowBatchOp {
  return op >= 1 && op <= 6;
}

/** True for ops whose rows carry column values. */
export function opHasValues(op: number): boolean {
  return op === G9ERowBatchOp.Upsert || op === G9ERowBatchOp.Backfill;
}

/** A malformed frame (the .NET `FormatException`). */
export class G9RowBatchFormatError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'G9RowBatchFormatError';
    Object.setPrototypeOf(this, new.target.prototype);
  }
}

export const VARINT_MAX_BYTES = 10;

/** A read position over a byte array. */
export interface Cursor {
  pos: number;
}

/**
 * Reads an unsigned LEB128 varint as a number. Exact up to 2^53; beyond that the nearest double (callers that need
 * exactness use {@link readVarintLong}). Throws on a truncated or over-long varint.
 */
export function readVarint(bytes: Uint8Array, cursor: Cursor, end: number = bytes.length): number {
  let result = 0;
  let multiplier = 1;
  let shift = 0;
  let pos = cursor.pos;
  for (;;) {
    if (pos >= end) throw new G9RowBatchFormatError('Truncated varint.');
    const b = bytes[pos++] as number;
    result += (b & 0x7f) * multiplier;
    if ((b & 0x80) === 0) {
      cursor.pos = pos;
      return result;
    }
    shift += 7;
    if (shift > 63) throw new G9RowBatchFormatError('Varint is too long.');
    multiplier *= 128;
  }
}

/** Reads an unsigned LEB128 varint exactly, as the bits of an unsigned 64-bit value. */
export function readVarintLong(bytes: Uint8Array, cursor: Cursor, end: number = bytes.length): G9Long {
  let lo = 0;
  let hi = 0;
  let shift = 0;
  let pos = cursor.pos;
  for (;;) {
    if (pos >= end) throw new G9RowBatchFormatError('Truncated varint.');
    const b = bytes[pos++] as number;
    const bits = b & 0x7f;
    if (shift < 28) {
      lo |= bits << shift;
    } else if (shift === 28) {
      lo |= (bits & 0x0f) << 28;
      hi |= bits >>> 4;
    } else {
      hi |= bits << (shift - 32);
    }
    if ((b & 0x80) === 0) {
      cursor.pos = pos;
      return new G9Long(hi, lo);
    }
    shift += 7;
    if (shift > 63) throw new G9RowBatchFormatError('Varint is too long.');
  }
}

/** A varint that must fit an int32 (a length, a count, an id). */
export function readVarintInt32(bytes: Uint8Array, cursor: Cursor, end: number = bytes.length): number {
  const value = readVarint(bytes, cursor, end);
  if (value > 2147483647) throw new G9RowBatchFormatError('Varint is out of range.');
  return value;
}

/** Skips a varint and returns its length in bytes. */
export function varintLength(bytes: Uint8Array, position: number, end: number = bytes.length): number {
  let pos = position;
  let shift = 0;
  for (;;) {
    if (pos >= end) throw new G9RowBatchFormatError('Truncated varint.');
    const b = bytes[pos++] as number;
    if ((b & 0x80) === 0) return pos - position;
    shift += 7;
    if (shift > 63) throw new G9RowBatchFormatError('Varint is too long.');
  }
}

/** Zig-zag of a signed 64-bit value (`(v << 1) ^ (v >> 63)`), as unsigned bits. */
export function zigZag(value: G9Long): G9Long {
  return value.shiftLeft(1).xor(value.shiftRight(63));
}

/** Inverse zig-zag (`(long)(v >> 1) ^ -(long)(v & 1)`). */
export function unZigZag(value: G9Long): G9Long {
  const shifted = value.shiftRightUnsigned(1);
  return (value.lo & 1) === 0 ? shifted : shifted.xor(G9Long.MINUS_ONE);
}

/** Reads a zig-zag varint as a signed number (exact within ±2^53). */
export function readZigZagNumber(bytes: Uint8Array, cursor: Cursor, end?: number): number {
  const start = cursor.pos;
  const raw = readVarint(bytes, cursor, end);
  if (raw <= 9007199254740991) return raw % 2 === 0 ? raw / 2 : -(raw + 1) / 2;
  cursor.pos = start;
  return unZigZag(readVarintLong(bytes, cursor, end)).toNumber();
}

/** Reads a zig-zag varint exactly. */
export function readZigZagLong(bytes: Uint8Array, cursor: Cursor, end?: number): G9Long {
  return unZigZag(readVarintLong(bytes, cursor, end));
}

export function readInt32LE(bytes: Uint8Array, offset: number): number {
  return (bytes[offset] as number) | ((bytes[offset + 1] as number) << 8) | ((bytes[offset + 2] as number) << 16) | ((bytes[offset + 3] as number) << 24);
}

export function writeInt32LE(bytes: Uint8Array, offset: number, value: number): void {
  bytes[offset] = value & 0xff;
  bytes[offset + 1] = (value >>> 8) & 0xff;
  bytes[offset + 2] = (value >>> 16) & 0xff;
  bytes[offset + 3] = (value >>> 24) & 0xff;
}
