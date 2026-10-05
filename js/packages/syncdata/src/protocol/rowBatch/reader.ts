// Twin of G9SyncData.Protocol/RowBatch/G9CRowBatchReader.cs: reads a G9RowBatch frame in either body layout. Forward
// only; values are decoded from the body on demand.
import { G9ESyncColumnType } from '../../abstractions/enums.js';
import { readGuidBytes } from '../../abstractions/guid.js';
import { G9SyncKey } from '../../abstractions/key.js';
import { G9Long } from '../../abstractions/long.js';
import type { DecimalParts } from '../../internal/dotnetFormat.js';
import { utf8Decode } from '../../internal/utf8.js';
import { toRows } from './columnLayout.js';
import {
  type Cursor,
  G9ECompression,
  G9EPushRowOp,
  G9ERowBatchOp,
  G9RowBatchFormat,
  G9RowBatchFormatError,
  isRowBatchOp,
  readInt32LE,
  readVarint,
  readVarintInt32,
  readVarintLong,
  readZigZagLong,
  readZigZagNumber,
} from './format.js';
import type { G9DateTimeOffsetValue, G9DateTimeValue } from './writer.js';

/**
 * Decompresses a Brotli frame body. Returns exactly `expectedLength` bytes or throws. Node: `nodeBrotliDecompressor()`
 * (`@g9tm/syncdata/node`); Lynx: the native module of `@g9tm/syncdata-lynx`; anywhere: `jsBrotliDecompressor()`
 * (`@g9tm/syncdata/brotli-js`, pure JavaScript, slower).
 */
export interface G9FrameDecompressor {
  decompress(compressed: Uint8Array, expectedLength: number): Uint8Array | Promise<Uint8Array>;
}

/** The header of a frame, read without decoding the body. */
export interface G9FrameHeader {
  version: number;
  compression: number;
  bodyLength: number;
}

/** Validates a frame's header (magic, version, compression, body length, budget). */
export function readFrameHeader(frame: Uint8Array, maxBodyBytes: number = G9RowBatchFormat.MaxDecodedBytes): G9FrameHeader {
  if (!(maxBodyBytes > 0)) throw new RangeError('maxBodyBytes must be positive.');
  if (frame.length < G9RowBatchFormat.HeaderSize || frame[0] !== 0x47 || frame[1] !== 0x39 || frame[2] !== 0x52 || frame[3] !== 0x42) {
    throw new G9RowBatchFormatError('Not a G9RowBatch frame.');
  }
  const version = frame[4] as number;
  if (version !== G9RowBatchFormat.Version && version !== G9RowBatchFormat.ColumnVersion) {
    throw new G9RowBatchFormatError(`Unsupported G9RowBatch version ${version}.`);
  }
  const compression = frame[5] as number;
  const bodyLength = readInt32LE(frame, 6);
  if (bodyLength < 4 || bodyLength > G9RowBatchFormat.MaxDecodedBytes) throw new G9RowBatchFormatError('Invalid body length.');
  if (bodyLength > maxBodyBytes) {
    throw new G9RowBatchFormatError(`The frame declares a ${bodyLength}-byte body; this reader accepts at most ${maxBodyBytes} bytes.`);
  }
  if (compression !== G9ECompression.None && compression !== G9ECompression.Brotli) {
    throw new G9RowBatchFormatError(`Unknown compression ${compression}.`);
  }
  if (compression === G9ECompression.None && frame.length - G9RowBatchFormat.HeaderSize !== bodyLength) {
    throw new G9RowBatchFormatError('Body length mismatch.');
  }
  return { version, compression, bodyLength };
}

let defaultDecompressor: G9FrameDecompressor | null = null;

/**
 * Sets the Brotli decoder used wherever none is passed explicitly (the store, the engine's reconciliation). Call it
 * once at start-up: `useNodeBrotli()` (`@g9tm/syncdata/node`), `useLynxBrotli()` (`@g9tm/syncdata-lynx`) or
 * `setDefaultFrameDecompressor(jsBrotliDecompressor())` (`@g9tm/syncdata/brotli-js`).
 */
export function setDefaultFrameDecompressor(decompressor: G9FrameDecompressor | null): void {
  defaultDecompressor = decompressor;
}

/** The decoder set with {@link setDefaultFrameDecompressor}, or null. */
export function getDefaultFrameDecompressor(): G9FrameDecompressor | null {
  return defaultDecompressor;
}

/** Options of {@link openRowBatch}. */
export interface G9OpenRowBatchOptions {
  /** Brotli decoder for compressed frames (every frame the server sends over 128 bytes); the default decoder when omitted. */
  decompressor?: G9FrameDecompressor | null;
  /** The largest decoded body this caller allocates for (ADR-0051). */
  maxBodyBytes?: number;
}

/**
 * Validates, decompresses when needed, and opens a frame. Async because a Lynx decompressor crosses the native bridge;
 * an uncompressed frame resolves without waiting on anything.
 */
export async function openRowBatch(frame: Uint8Array, options?: G9OpenRowBatchOptions): Promise<G9RowBatchReader> {
  const header = readFrameHeader(frame, options?.maxBodyBytes);
  if (header.compression === G9ECompression.None) return G9RowBatchReader.fromBody(frame.subarray(G9RowBatchFormat.HeaderSize), header.version);
  const decompressor = options?.decompressor ?? defaultDecompressor;
  if (!decompressor) {
    throw new G9RowBatchFormatError('The frame is Brotli-compressed and no decompressor is configured (see G9FrameDecompressor).');
  }
  let body: Uint8Array;
  try {
    body = await decompressor.decompress(frame.subarray(G9RowBatchFormat.HeaderSize), header.bodyLength);
  } catch (error) {
    throw new G9RowBatchFormatError(`Brotli body could not be decompressed: ${error instanceof Error ? error.message : String(error)}`);
  }
  if (!body || body.length !== header.bodyLength) throw new G9RowBatchFormatError('Brotli body could not be decompressed.');
  return G9RowBatchReader.fromBody(body, header.version);
}

/** Opens an uncompressed frame synchronously (throws for a compressed one). */
export function openRowBatchSync(frame: Uint8Array, maxBodyBytes?: number): G9RowBatchReader {
  const header = readFrameHeader(frame, maxBodyBytes);
  if (header.compression !== G9ECompression.None) throw new G9RowBatchFormatError('The frame is compressed; use openRowBatch.');
  return G9RowBatchReader.fromBody(frame.subarray(G9RowBatchFormat.HeaderSize), header.version);
}

/** Reads the sections and rows of one G9RowBatch body. */
export class G9RowBatchReader {
  private readonly _body: Uint8Array;
  private readonly _cursor: Cursor = { pos: 4 };
  private _sectionsRemaining: number;
  private _sectionOpen = false;
  private _rowsRemaining = 0;
  private _types: number[] = [];
  private _offsets: number[] = [];
  private _keyStart = 0;
  private _keyLength = 0;
  private _keyText: string | null = null;
  private _versionPos = -1;
  private _sequencePos = -1;
  private _baseVersionPos = -1;
  private _hlcPos = -1;

  readonly sectionCount: number;

  // section
  op = 0;
  tableId = 0;
  keyType = 0;
  rowCount = 0;

  // row
  version = 0;
  pushOp = 0;
  sequence = 0;
  baseVersion = 0;
  dirtyMask: G9Long = G9Long.ZERO;
  hlcMs = 0;
  rowHash: G9Long = G9Long.ZERO;
  hasValues = false;

  private constructor(body: Uint8Array) {
    if (body.length < 4) throw new G9RowBatchFormatError('Row batch body is too short.');
    this._body = body;
    this.sectionCount = readInt32LE(body, 0);
    if (this.sectionCount < 0) throw new G9RowBatchFormatError('Negative section count.');
    this._sectionsRemaining = this.sectionCount;
  }

  /** Opens an already decompressed body of the given layout version. */
  static fromBody(body: Uint8Array, version: number): G9RowBatchReader {
    return new G9RowBatchReader(version === G9RowBatchFormat.ColumnVersion ? toRows(body) : body);
  }

  /** The decoded size of this frame's body (row-wise layout). */
  get bodyBytes(): number {
    return this._body.length;
  }

  get columnTypes(): readonly number[] {
    return this._types;
  }

  get columnCount(): number {
    return this._types.length;
  }

  /** Moves to the next section, skipping unread rows of the current one. */
  nextSection(): boolean {
    if (this._sectionOpen) {
      while (this.readRow()) {
        // skip
      }
    }
    if (this._sectionsRemaining === 0) {
      this._sectionOpen = false;
      return false;
    }
    const body = this._body;
    const cursor = this._cursor;
    this.op = this._readByte();
    if (!isRowBatchOp(this.op)) throw new G9RowBatchFormatError(`Unknown op ${this.op}.`);
    this.tableId = readVarintInt32(body, cursor);
    this.keyType = this._readByte();
    if (!G9SyncKey.isSupportedKeyType(this.keyType)) throw new G9RowBatchFormatError(`Unsupported key type ${this.keyType}.`);
    const columnCount = readVarintInt32(body, cursor);
    if (columnCount > 1024) throw new G9RowBatchFormatError('Too many columns.');
    this._ensure(columnCount + 4);
    const types: number[] = [];
    for (let i = 0; i < columnCount; i++) types.push(body[cursor.pos++] as number);
    this._types = types;
    this.rowCount = readInt32LE(body, cursor.pos);
    cursor.pos += 4;
    if (this.rowCount < 0) throw new G9RowBatchFormatError('Negative row count.');
    if (this._offsets.length < columnCount) this._offsets = new Array<number>(columnCount);
    this._rowsRemaining = this.rowCount;
    this._sectionsRemaining--;
    this._sectionOpen = true;
    return true;
  }

  /** Moves to the next row of the current section. */
  readRow(): boolean {
    if (!this._sectionOpen || this._rowsRemaining === 0) return false;
    const body = this._body;
    const cursor = this._cursor;
    this._keyText = null;
    this.hasValues = false;
    this.version = 0;
    this.rowHash = G9Long.ZERO;

    switch (this.keyType) {
      case G9ESyncColumnType.Guid:
        this._ensure(16);
        this._keyStart = cursor.pos;
        this._keyLength = 16;
        cursor.pos += 16;
        break;
      case G9ESyncColumnType.Int32:
      case G9ESyncColumnType.Int64:
        this._keyStart = cursor.pos;
        readVarintLong(body, cursor);
        this._keyLength = cursor.pos - this._keyStart;
        break;
      default: {
        const length = readVarintInt32(body, cursor);
        this._ensure(length);
        this._keyStart = cursor.pos;
        this._keyLength = length;
        cursor.pos += length;
        break;
      }
    }

    switch (this.op) {
      case G9ERowBatchOp.Upsert:
      case G9ERowBatchOp.Backfill:
        this._versionPos = cursor.pos;
        this.version = readVarint(body, cursor);
        this._readValues();
        break;
      case G9ERowBatchOp.Tombstone:
      case G9ERowBatchOp.MoveOut:
        this._versionPos = cursor.pos;
        this.version = readVarint(body, cursor);
        break;
      case G9ERowBatchOp.KeyVersions:
        this._versionPos = cursor.pos;
        this.version = readVarint(body, cursor);
        this._ensure(8);
        this.rowHash = G9Long.readLE(body, cursor.pos);
        cursor.pos += 8;
        break;
      case G9ERowBatchOp.Push:
        this.pushOp = this._readByte();
        if (this.pushOp !== G9EPushRowOp.Upsert && this.pushOp !== G9EPushRowOp.Delete) throw new G9RowBatchFormatError(`Unknown push op ${this.pushOp}.`);
        this._versionPos = -1;
        this._sequencePos = cursor.pos;
        this.sequence = readVarint(body, cursor);
        this._baseVersionPos = cursor.pos;
        this.baseVersion = readVarint(body, cursor);
        this._ensure(8);
        this.dirtyMask = G9Long.readLE(body, cursor.pos);
        cursor.pos += 8;
        this._hlcPos = cursor.pos;
        this.hlcMs = readZigZagNumber(body, cursor);
        if (this.pushOp === G9EPushRowOp.Upsert) this._readValues();
        break;
    }
    this._rowsRemaining--;
    return true;
  }

  /** The row's version, exact (the {@link version} number is exact below 2^53). */
  get versionLong(): G9Long {
    return this._versionPos < 0 ? G9Long.ZERO : readVarintLong(this._body, { pos: this._versionPos });
  }

  /** A push row's local sequence, exact. */
  get sequenceLong(): G9Long {
    return readVarintLong(this._body, { pos: this._sequencePos });
  }

  /** A push row's base version, exact. */
  get baseVersionLong(): G9Long {
    return readVarintLong(this._body, { pos: this._baseVersionPos });
  }

  /** A push row's HLC milliseconds, exact. */
  get hlcLong(): G9Long {
    return readZigZagLong(this._body, { pos: this._hlcPos });
  }

  // ---------------------------------------------------------------- key

  /** Canonical key text (see G9SyncKey). */
  get key(): string {
    if (this._keyText === null) {
      switch (this.keyType) {
        case G9ESyncColumnType.Guid:
          this._keyText = readGuidBytes(this._body, this._keyStart);
          break;
        case G9ESyncColumnType.Int32:
        case G9ESyncColumnType.Int64:
          this._keyText = this.keyInt64.toString();
          break;
        default:
          this._keyText = utf8Decode(this._body.subarray(this._keyStart, this._keyStart + this._keyLength));
          break;
      }
    }
    return this._keyText;
  }

  /** The integer key. */
  get keyInt64(): G9Long {
    if (this.keyType !== G9ESyncColumnType.Int32 && this.keyType !== G9ESyncColumnType.Int64) throw new Error(`Key type is ${this.keyType}.`);
    return readZigZagLong(this._body, { pos: this._keyStart });
  }

  /** UTF-8 bytes of a string key. */
  get keyUtf8(): Uint8Array {
    if (this.keyType !== G9ESyncColumnType.String) throw new Error(`Key type is ${this.keyType}.`);
    return this._body.subarray(this._keyStart, this._keyStart + this._keyLength);
  }

  // ---------------------------------------------------------------- values

  isNull(column: number): boolean {
    this._ensureValues(column);
    return (this._offsets[column] as number) < 0;
  }

  getBoolean(column: number): boolean {
    return this._body[this._offset(column, G9ESyncColumnType.Boolean)] !== 0;
  }

  getInt32(column: number): number {
    const value = readZigZagNumber(this._body, { pos: this._offset(column, G9ESyncColumnType.Int32) });
    if (value < -2147483648 || value > 2147483647) throw new G9RowBatchFormatError('Int32 value is out of range.');
    return value;
  }

  /** An Int64 value, exact. */
  getInt64(column: number): G9Long {
    return readZigZagLong(this._body, { pos: this._offset(column, G9ESyncColumnType.Int64) });
  }

  getDouble(column: number): number {
    const at = this._offset(column, G9ESyncColumnType.Double);
    for (let i = 0; i < 8; i++) DOUBLE_BYTES[i] = this._body[at + i] as number;
    return DOUBLE_VIEW.getFloat64(0, true);
  }

  getDecimal(column: number): DecimalParts {
    const at = this._offset(column, G9ESyncColumnType.Decimal);
    const flags = readInt32LE(this._body, at + 12);
    // decimal.GetBits: bits 0-15 and 24-30 of the flags are zero and the scale is at most 28.
    if ((flags & 0x7f00ffff) !== 0 || ((flags >> 16) & 0xff) > 28) throw new G9RowBatchFormatError('Invalid decimal value.');
    return {
      lo: readInt32LE(this._body, at) >>> 0,
      mid: readInt32LE(this._body, at + 4) >>> 0,
      hi: readInt32LE(this._body, at + 8) >>> 0,
      scale: (flags >> 16) & 0xff,
      negative: flags < 0,
    };
  }

  getString(column: number): string {
    return utf8Decode(this.getUtf8(column));
  }

  getUtf8(column: number): Uint8Array {
    return this._lengthPrefixed(this._offset(column, G9ESyncColumnType.String));
  }

  /** A GUID in canonical text form. */
  getGuid(column: number): string {
    return readGuidBytes(this._body, this._offset(column, G9ESyncColumnType.Guid));
  }

  getDateTime(column: number): G9DateTimeValue {
    const at = this._offset(column, G9ESyncColumnType.DateTime);
    const ticks = G9Long.readLE(this._body, at);
    const kind = this._body[at + 8] as number;
    if (ticks.compare(G9Long.ZERO) < 0 || ticks.compare(MAX_TICKS) > 0 || kind > 2) throw new G9RowBatchFormatError('Invalid DateTime value.');
    return { ticks, kind };
  }

  getDateTimeOffset(column: number): G9DateTimeOffsetValue {
    const at = this._offset(column, G9ESyncColumnType.DateTimeOffset);
    const utcTicks = G9Long.readLE(this._body, at);
    const raw = (this._body[at + 8] as number) | ((this._body[at + 9] as number) << 8);
    const minutes = raw >= 0x8000 ? raw - 0x10000 : raw;
    const local = utcTicks.add(G9Long.fromNumber(minutes * 600000000));
    if (
      minutes < -840 ||
      minutes > 840 ||
      utcTicks.compare(G9Long.ZERO) < 0 ||
      utcTicks.compare(MAX_TICKS) > 0 ||
      local.compare(G9Long.ZERO) < 0 ||
      local.compare(MAX_TICKS) > 0
    ) {
      throw new G9RowBatchFormatError('Invalid DateTimeOffset value.');
    }
    return { utcTicks, offsetMinutes: minutes };
  }

  /** A TimeSpan as ticks, exact. */
  getTimeSpan(column: number): G9Long {
    return readZigZagLong(this._body, { pos: this._offset(column, G9ESyncColumnType.TimeSpan) });
  }

  /** The bytes of a Bytes column (a view into the body; copy it to keep it). */
  getBytes(column: number): Uint8Array {
    return this._lengthPrefixed(this._offset(column, G9ESyncColumnType.Bytes));
  }

  // ---------------------------------------------------------------- internals

  private _readValues(): void {
    const body = this._body;
    const cursor = this._cursor;
    const count = this._types.length;
    const bitmapBytes = (count + 7) >> 3;
    this._ensure(bitmapBytes);
    const bitmap = cursor.pos;
    cursor.pos += bitmapBytes;
    this.hasValues = true;
    const offsets = this._offsets;
    for (let column = 0; column < count; column++) {
      if (((body[bitmap + (column >> 3)] as number) & (1 << (column & 7))) !== 0) {
        offsets[column] = -1;
        continue;
      }
      offsets[column] = cursor.pos;
      switch (this._types[column]) {
        case G9ESyncColumnType.Boolean:
          this._ensure(1);
          cursor.pos += 1;
          break;
        case G9ESyncColumnType.Int32:
        case G9ESyncColumnType.Int64:
        case G9ESyncColumnType.TimeSpan:
          readVarintLong(body, cursor);
          break;
        case G9ESyncColumnType.Double:
          this._ensure(8);
          cursor.pos += 8;
          break;
        case G9ESyncColumnType.Decimal:
        case G9ESyncColumnType.Guid:
          this._ensure(16);
          cursor.pos += 16;
          break;
        case G9ESyncColumnType.DateTime:
          this._ensure(9);
          cursor.pos += 9;
          break;
        case G9ESyncColumnType.DateTimeOffset:
          this._ensure(10);
          cursor.pos += 10;
          break;
        case G9ESyncColumnType.String:
        case G9ESyncColumnType.Bytes: {
          const length = readVarintInt32(body, cursor);
          this._ensure(length);
          cursor.pos += length;
          break;
        }
        default:
          throw new G9RowBatchFormatError(`Unknown column type ${this._types[column]}.`);
      }
    }
  }

  private _offset(column: number, expected: number): number {
    this._ensureValues(column);
    if (this._types[column] !== expected) throw new Error(`Column ${column} is ${this._types[column]}, not ${expected}.`);
    const offset = this._offsets[column] as number;
    if (offset < 0) throw new Error(`Column ${column} is NULL.`);
    return offset;
  }

  private _ensureValues(column: number): void {
    if (!this.hasValues) throw new Error('The current row carries no column values.');
    if (!(column >= 0 && column < this._types.length)) throw new RangeError(`Column ${column} is out of range.`);
  }

  private _lengthPrefixed(position: number): Uint8Array {
    const cursor = { pos: position };
    const length = readVarintInt32(this._body, cursor);
    return this._body.subarray(cursor.pos, cursor.pos + length);
  }

  private _readByte(): number {
    this._ensure(1);
    return this._body[this._cursor.pos++] as number;
  }

  private _ensure(count: number): void {
    if (count < 0 || this._cursor.pos + count > this._body.length) throw new G9RowBatchFormatError('Row batch is truncated.');
  }
}

/** DateTime.MaxValue.Ticks. */
const MAX_TICKS = G9Long.parse('3155378975999999999');

const DOUBLE_BUFFER = new ArrayBuffer(8);
const DOUBLE_VIEW = new DataView(DOUBLE_BUFFER);
const DOUBLE_BYTES = new Uint8Array(DOUBLE_BUFFER);
