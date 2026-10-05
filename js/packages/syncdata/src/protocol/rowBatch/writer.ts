// Twin of G9SyncData.Protocol/RowBatch/G9CRowBatchWriter.cs. Same bytes for the same rows: the frame header, the
// section layout and every value encoding are specified in AiGuides/04-Protocol.md.
import { G9ESyncColumnType, isColumnType } from '../../abstractions/enums.js';
import { writeGuidBytes } from '../../abstractions/guid.js';
import { G9SyncKey } from '../../abstractions/key.js';
import { G9Long } from '../../abstractions/long.js';
import { type DecimalParts, parseDecimal } from '../../internal/dotnetFormat.js';
import { utf8Encode } from '../../internal/utf8.js';
import { fromRows } from './columnLayout.js';
import { G9ECompression, G9EPushRowOp, G9ERowBatchOp, G9RowBatchFormat, isRowBatchOp, writeInt32LE, zigZag } from './format.js';

/** A DateTime value on the wire: ticks (100 ns since 0001-01-01) and the `DateTimeKind` (0 unspecified, 1 UTC, 2 local). */
export interface G9DateTimeValue {
  ticks: G9Long;
  kind: number;
}

/** A DateTimeOffset value on the wire: UTC ticks and the offset in minutes. */
export interface G9DateTimeOffsetValue {
  utcTicks: G9Long;
  offsetMinutes: number;
}

/**
 * A compressor for push frames. Optional: without one the writer emits uncompressed frames, which every reader
 * accepts (the .NET writer falls back to them whenever compression does not help).
 */
export interface G9FrameCompressor {
  compress(body: Uint8Array, quality: number, window: number): Uint8Array | null;
}

/**
 * Writes a G9RowBatch frame: a 10-byte header (`'G' '9' 'R' 'B' | version | compression | body length i32 LE`) and a
 * body of sections, each holding rows of one table and one op.
 */
export class G9RowBatchWriter {
  private _buffer: Uint8Array;
  private _length = 4;
  private _sectionCount = 0;
  private _totalRows = 0;

  private _inSection = false;
  private _op: number = 0;
  private _keyType: number = 0;
  private _types: number[] = [];
  private _rowCountPosition = 0;
  private _sectionRows = 0;

  private _inRow = false;
  private _rowHasValues = false;
  private _column = 0;
  private _bitmapPosition = 0;

  constructor(initialCapacity: number = 64 * 1024) {
    this._buffer = new Uint8Array(Math.max(initialCapacity, 256));
  }

  /** Uncompressed body length written so far. */
  get length(): number {
    return this._length;
  }

  get totalRows(): number {
    return this._totalRows;
  }

  get sectionCount(): number {
    return this._sectionCount;
  }

  get currentSectionRows(): number {
    return this._sectionRows;
  }

  get inSection(): boolean {
    return this._inSection;
  }

  /** Starts a section. Key-only ops (Tombstone, MoveOut, KeyVersions) take no column types. */
  beginSection(op: number, tableId: number, keyType: number, columnTypes: readonly number[]): void {
    if (this._inSection) throw new Error('EndSection must be called before BeginSection.');
    if (!isRowBatchOp(op)) throw new RangeError(`Unknown row batch op ${op}.`);
    if (!G9SyncKey.isSupportedKeyType(keyType)) throw new RangeError(`Unsupported key type ${keyType}.`);
    if (!(tableId >= 0) || Math.floor(tableId) !== tableId) throw new RangeError('The table id must be a non-negative integer.');
    if ((op === G9ERowBatchOp.Tombstone || op === G9ERowBatchOp.MoveOut || op === G9ERowBatchOp.KeyVersions) && columnTypes.length > 0) {
      throw new RangeError('Key-only sections carry no columns.');
    }
    if (columnTypes.length > 1024) throw new RangeError('Too many columns.');
    for (const type of columnTypes) {
      if (!isColumnType(type)) throw new RangeError(`Invalid column type '${type}'.`);
    }

    this._ensure(2 + 20 + columnTypes.length + 4);
    this._buffer[this._length++] = op;
    this._varintNumber(tableId);
    this._buffer[this._length++] = keyType;
    this._varintNumber(columnTypes.length);
    for (const type of columnTypes) this._buffer[this._length++] = type;
    this._rowCountPosition = this._length;
    this._length += 4;

    this._op = op;
    this._keyType = keyType;
    this._types = columnTypes.slice();
    this._sectionRows = 0;
    this._inSection = true;
  }

  endSection(): void {
    if (!this._inSection) throw new Error('No section is open.');
    if (this._inRow) throw new Error('EndRow must be called before EndSection.');
    writeInt32LE(this._buffer, this._rowCountPosition, this._sectionRows);
    this._sectionCount++;
    this._inSection = false;
  }

  // ---------------------------------------------------------------- rows

  /** Starts an Upsert or Backfill row; write every column, then {@link endRow}. `key` is canonical key text. */
  beginRow(key: string, version: number | G9Long): void {
    this._startRow(G9ERowBatchOp.Upsert, G9ERowBatchOp.Backfill);
    this._writeKey(key);
    this._unsigned(version);
    this._openValues();
  }

  /** Adds a complete Tombstone or MoveOut row. */
  addKey(key: string, version: number | G9Long): void {
    this._startRow(G9ERowBatchOp.Tombstone, G9ERowBatchOp.MoveOut);
    this._writeKey(key);
    this._unsigned(version);
    this._completeRow();
  }

  /** Adds a complete KeyVersions row. */
  addKeyVersion(key: string, version: number | G9Long, rowHash: G9Long): void {
    this._startRow(G9ERowBatchOp.KeyVersions, G9ERowBatchOp.KeyVersions);
    this._writeKey(key);
    this._unsigned(version);
    this._ensure(8);
    rowHash.writeLE(this._buffer, this._length);
    this._length += 8;
    this._completeRow();
  }

  /** Starts a Push row. Upserts must then write every column; deletes write none. Finish with {@link endRow}. */
  beginPushRow(key: string, op: number, sequence: number | G9Long, baseVersion: number | G9Long, dirtyMask: G9Long, hlcMs: number | G9Long): void {
    this._startRow(G9ERowBatchOp.Push, G9ERowBatchOp.Push);
    if (op !== G9EPushRowOp.Upsert && op !== G9EPushRowOp.Delete) throw new RangeError(`Unknown push op ${op}.`);
    this._writeKey(key);
    this._ensure(1);
    this._buffer[this._length++] = op;
    this._unsigned(sequence);
    this._unsigned(baseVersion);
    this._ensure(8);
    dirtyMask.writeLE(this._buffer, this._length);
    this._length += 8;
    if (hlcMs instanceof G9Long) this._varintLong(zigZag(hlcMs));
    else this._zigZagNumber(hlcMs);
    if (op === G9EPushRowOp.Upsert) this._openValues();
  }

  /** Completes a row started with beginRow or beginPushRow. */
  endRow(): void {
    if (!this._inRow) throw new Error('No row is open.');
    if (this._rowHasValues && this._column !== this._types.length) {
      throw new Error(`Row ended after ${this._column} of ${this._types.length} column values.`);
    }
    this._completeRow();
  }

  // ---------------------------------------------------------------- values

  writeNull(): void {
    const column = this._nextColumn(null);
    const at = this._bitmapPosition + (column >> 3);
    this._buffer[at] = (this._buffer[at] as number) | (1 << (column & 7));
  }

  writeBoolean(value: boolean): void {
    this._nextColumn(G9ESyncColumnType.Boolean);
    this._ensure(1);
    this._buffer[this._length++] = value ? 1 : 0;
  }

  writeInt32(value: number): void {
    this._nextColumn(G9ESyncColumnType.Int32);
    if (!(value >= -2147483648 && value <= 2147483647) || Math.floor(value) !== value) throw new RangeError(`${value} is not an Int32.`);
    this._zigZagNumber(value);
  }

  writeInt64(value: number | G9Long): void {
    this._nextColumn(G9ESyncColumnType.Int64);
    if (value instanceof G9Long) this._varintLong(zigZag(value));
    else this._zigZagNumber(value);
  }

  writeDouble(value: number): void {
    this._nextColumn(G9ESyncColumnType.Double);
    this._ensure(8);
    DOUBLE_VIEW.setFloat64(0, value, true);
    for (let i = 0; i < 8; i++) this._buffer[this._length + i] = DOUBLE_BYTES[i] as number;
    this._length += 8;
  }

  /** A decimal: its .NET parts, or invariant decimal text. */
  writeDecimal(value: DecimalParts | string): void {
    this._nextColumn(G9ESyncColumnType.Decimal);
    const parts = typeof value === 'string' ? parseDecimal(value) : value;
    if (!parts) throw new RangeError(`'${String(value)}' is not a decimal.`);
    if (!(parts.scale >= 0 && parts.scale <= 28)) throw new RangeError('Decimal scale must be 0..28.');
    this._ensure(16);
    const flags = ((parts.scale & 0xff) << 16) | (parts.negative ? 0x80000000 : 0);
    writeInt32LE(this._buffer, this._length, parts.lo);
    writeInt32LE(this._buffer, this._length + 4, parts.mid);
    writeInt32LE(this._buffer, this._length + 8, parts.hi);
    writeInt32LE(this._buffer, this._length + 12, flags);
    this._length += 16;
  }

  /** Writes a string, or NULL when `value` is null. */
  writeString(value: string | null): void {
    if (value === null || value === undefined) {
      this.writeNull();
      return;
    }
    this._nextColumn(G9ESyncColumnType.String);
    this._bytesRaw(utf8Encode(value));
  }

  /** Writes a string that is already UTF-8 encoded. */
  writeStringUtf8(utf8: Uint8Array): void {
    this._nextColumn(G9ESyncColumnType.String);
    this._bytesRaw(utf8);
  }

  /** Writes a GUID given as text in any form `Guid.Parse` accepts. */
  writeGuid(value: string): void {
    this._nextColumn(G9ESyncColumnType.Guid);
    this._ensure(16);
    writeGuidBytes(value, this._buffer, this._length);
    this._length += 16;
  }

  writeDateTime(value: G9DateTimeValue): void {
    this._nextColumn(G9ESyncColumnType.DateTime);
    this._ensure(9);
    value.ticks.writeLE(this._buffer, this._length);
    this._length += 8;
    this._buffer[this._length++] = value.kind & 0xff;
  }

  writeDateTimeOffset(value: G9DateTimeOffsetValue): void {
    this._nextColumn(G9ESyncColumnType.DateTimeOffset);
    if (!(value.offsetMinutes >= -32768 && value.offsetMinutes <= 32767)) throw new RangeError('The offset does not fit 16 bits.');
    this._ensure(10);
    value.utcTicks.writeLE(this._buffer, this._length);
    this._length += 8;
    this._buffer[this._length++] = value.offsetMinutes & 0xff;
    this._buffer[this._length++] = (value.offsetMinutes >> 8) & 0xff;
  }

  /** A TimeSpan as ticks. */
  writeTimeSpan(ticks: number | G9Long): void {
    this._nextColumn(G9ESyncColumnType.TimeSpan);
    if (ticks instanceof G9Long) this._varintLong(zigZag(ticks));
    else this._zigZagNumber(ticks);
  }

  writeBytes(value: Uint8Array): void {
    this._nextColumn(G9ESyncColumnType.Bytes);
    this._bytesRaw(value);
  }

  // ---------------------------------------------------------------- frame

  /**
   * Produces the frame. With a compressor and `compression` Brotli, the body is compressed when it is over 128 bytes
   * and compression helps; otherwise the frame is uncompressed. `version` 2 writes the column-grouped layout (only for
   * a peer that asked for it).
   */
  toFrame(options?: { compression?: number; quality?: number; version?: number; compressor?: G9FrameCompressor | null }): Uint8Array {
    if (this._inSection) throw new Error('EndSection must be called before ToFrame.');
    const version = options?.version ?? G9RowBatchFormat.Version;
    if (version !== G9RowBatchFormat.Version && version !== G9RowBatchFormat.ColumnVersion) {
      throw new RangeError(`Unknown G9RowBatch version ${version}.`);
    }
    writeInt32LE(this._buffer, 0, this._sectionCount);
    const rows = this._buffer.subarray(0, this._length);
    const body = version === G9RowBatchFormat.ColumnVersion ? fromRows(rows) : rows;

    const compression = options?.compression ?? G9ECompression.Brotli;
    const compressor = options?.compressor;
    if (compression === G9ECompression.Brotli && compressor && body.length > 128) {
      const compressed = compressor.compress(body, options?.quality ?? 4, 22);
      if (compressed && compressed.length < body.length) {
        const frame = new Uint8Array(G9RowBatchFormat.HeaderSize + compressed.length);
        writeHeader(frame, version, G9ECompression.Brotli, body.length);
        frame.set(compressed, G9RowBatchFormat.HeaderSize);
        return frame;
      }
    }
    const frame = new Uint8Array(G9RowBatchFormat.HeaderSize + body.length);
    writeHeader(frame, version, G9ECompression.None, body.length);
    frame.set(body, G9RowBatchFormat.HeaderSize);
    return frame;
  }

  /** Clears all content so the buffer can be reused for the next frame. */
  reset(): void {
    this._length = 4;
    this._sectionCount = 0;
    this._totalRows = 0;
    this._inSection = false;
    this._inRow = false;
    this._rowHasValues = false;
    this._sectionRows = 0;
  }

  // ---------------------------------------------------------------- internals

  private _startRow(allowed1: number, allowed2: number): void {
    if (!this._inSection) throw new Error('BeginSection must be called before adding rows.');
    if (this._inRow) throw new Error('EndRow must be called before starting another row.');
    if (this._op !== allowed1 && this._op !== allowed2) throw new Error(`This row kind is not valid in a section of op ${this._op}.`);
    this._inRow = true;
    this._rowHasValues = false;
    this._column = 0;
  }

  private _completeRow(): void {
    this._inRow = false;
    this._rowHasValues = false;
    this._sectionRows++;
    this._totalRows++;
  }

  private _openValues(): void {
    const bitmapBytes = (this._types.length + 7) >> 3;
    this._ensure(bitmapBytes);
    this._bitmapPosition = this._length;
    this._buffer.fill(0, this._length, this._length + bitmapBytes);
    this._length += bitmapBytes;
    this._column = 0;
    this._rowHasValues = true;
  }

  private _nextColumn(expected: number | null): number {
    if (!this._inRow || !this._rowHasValues) throw new Error('No row with column values is open.');
    if (this._column >= this._types.length) throw new Error('Every column of this row has already been written.');
    if (expected !== null && this._types[this._column] !== expected) {
      throw new Error(`Column ${this._column} is declared ${this._types[this._column]}, but a ${expected} value was written.`);
    }
    return this._column++;
  }

  private _writeKey(key: string): void {
    if (typeof key !== 'string') throw new TypeError('The key is required.');
    switch (this._keyType) {
      case G9ESyncColumnType.Guid:
        this._ensure(16);
        writeGuidBytes(key, this._buffer, this._length);
        this._length += 16;
        break;
      case G9ESyncColumnType.Int32:
      case G9ESyncColumnType.Int64:
        this._varintLong(zigZag(G9Long.parse(key)));
        break;
      default:
        this._bytesRaw(utf8Encode(key));
        break;
    }
  }

  private _unsigned(value: number | G9Long): void {
    if (value instanceof G9Long) {
      if (value.isNegative()) throw new RangeError(`${value.toString()} must be non-negative.`);
      this._varintLong(value);
      return;
    }
    if (!(value >= 0) || Math.floor(value) !== value) throw new RangeError(`${value} must be a non-negative integer.`);
    if (value > 9007199254740991) this._varintLong(G9Long.fromNumber(value));
    else this._varintNumber(value);
  }

  private _varintNumber(value: number): void {
    this._ensure(10);
    let v = value;
    while (v >= 128) {
      this._buffer[this._length++] = (v % 128) | 0x80;
      v = Math.floor(v / 128);
    }
    this._buffer[this._length++] = v;
  }

  private _varintLong(value: G9Long): void {
    this._ensure(10);
    let lo = value.lo;
    let hi = value.hi >>> 0;
    while (hi !== 0 || lo >= 128) {
      this._buffer[this._length++] = (lo & 0x7f) | 0x80;
      lo = ((lo >>> 7) | (hi << 25)) >>> 0;
      hi = hi >>> 7;
    }
    this._buffer[this._length++] = lo;
  }

  private _zigZagNumber(value: number): void {
    if (Math.floor(value) !== value) throw new RangeError(`${value} is not an integer.`);
    if (value >= -4503599627370496 && value < 4503599627370496) this._varintNumber(value >= 0 ? value * 2 : -value * 2 - 1);
    else this._varintLong(zigZag(G9Long.fromNumber(value)));
  }

  private _bytesRaw(value: Uint8Array): void {
    this._ensure(10 + value.length);
    this._varintNumber(value.length);
    this._buffer.set(value, this._length);
    this._length += value.length;
  }

  private _ensure(additional: number): void {
    const required = this._length + additional;
    if (required <= this._buffer.length) return;
    if (required > G9RowBatchFormat.MaxBodyBytes) throw new Error('Row batch exceeds the maximum body size.');
    const next = new Uint8Array(Math.min(Math.max(required, this._buffer.length * 2), G9RowBatchFormat.MaxBodyBytes));
    next.set(this._buffer.subarray(0, this._length));
    this._buffer = next;
  }
}

const DOUBLE_BUFFER = new ArrayBuffer(8);
const DOUBLE_VIEW = new DataView(DOUBLE_BUFFER);
const DOUBLE_BYTES = new Uint8Array(DOUBLE_BUFFER);

function writeHeader(frame: Uint8Array, version: number, compression: number, bodyLength: number): void {
  frame[0] = 0x47; // G
  frame[1] = 0x39; // 9
  frame[2] = 0x52; // R
  frame[3] = 0x42; // B
  frame[4] = version;
  frame[5] = compression;
  writeInt32LE(frame, 6, bodyLength);
}
