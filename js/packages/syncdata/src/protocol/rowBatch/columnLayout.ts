// Twin of G9SyncData.Protocol/RowBatch/G9RowBatchColumnLayout.cs: the column-grouped body of G9RowBatch version 2
// (ADR-0040), converted to and from the row-wise layout of version 1. Only the order of the bytes changes, so the
// conversion is exact both ways.
import { G9ESyncColumnType } from '../../abstractions/enums.js';
import { G9SyncKey } from '../../abstractions/key.js';
import {
  type Cursor,
  G9EPushRowOp,
  G9ERowBatchOp,
  G9RowBatchFormat,
  G9RowBatchFormatError,
  isRowBatchOp,
  readInt32LE,
  readVarint,
  varintLength,
  writeInt32LE,
} from './format.js';

const KEY_STREAM = 0;

interface SectionHeader {
  start: number;
  length: number;
  op: number;
  keyType: number;
  types: Uint8Array;
  rowCount: number;
}

function readSectionHeader(body: Uint8Array, cursor: Cursor): SectionHeader {
  const start = cursor.pos;
  const op = available(body, cursor.pos, 1)[0] as number;
  cursor.pos++;
  if (!isRowBatchOp(op)) throw new G9RowBatchFormatError(`Unknown op ${op}.`);
  readVarint(body, cursor);
  const keyType = available(body, cursor.pos, 1)[0] as number;
  cursor.pos++;
  if (!G9SyncKey.isSupportedKeyType(keyType)) throw new G9RowBatchFormatError(`Unsupported key type ${keyType}.`);
  const columnCount = readVarint(body, cursor);
  if (columnCount > 1024) throw new G9RowBatchFormatError('Too many columns.');
  const types = available(body, cursor.pos, columnCount);
  cursor.pos += columnCount;
  const rowCount = readInt32LE(available(body, cursor.pos, 4), 0);
  cursor.pos += 4;
  if (rowCount < 0) throw new G9RowBatchFormatError('Negative row count.');
  return { start, length: cursor.pos - start, op, keyType, types, rowCount };
}

function streamCount(header: SectionHeader): number {
  switch (header.op) {
    case G9ERowBatchOp.Upsert:
    case G9ERowBatchOp.Backfill:
      return 3 + header.types.length;
    case G9ERowBatchOp.Tombstone:
    case G9ERowBatchOp.MoveOut:
      return 2;
    case G9ERowBatchOp.KeyVersions:
      return 3;
    case G9ERowBatchOp.Push:
      return 7 + header.types.length;
    default:
      throw new G9RowBatchFormatError(`Unknown op ${header.op}.`);
  }
}

function available(body: Uint8Array, position: number, length: number): Uint8Array {
  if (length < 0 || position + length > body.length) throw new G9RowBatchFormatError('Row batch is truncated.');
  return body.subarray(position, position + length);
}

function keyLength(body: Uint8Array, position: number, keyType: number): number {
  if (keyType === G9ESyncColumnType.Guid) return 16;
  if (keyType === G9ESyncColumnType.Int32 || keyType === G9ESyncColumnType.Int64) return varintLength(body, position);
  return prefixedLength(body, position);
}

function valueLength(body: Uint8Array, position: number, type: number): number {
  switch (type) {
    case G9ESyncColumnType.Boolean:
      return 1;
    case G9ESyncColumnType.Int32:
    case G9ESyncColumnType.Int64:
    case G9ESyncColumnType.TimeSpan:
      return varintLength(body, position);
    case G9ESyncColumnType.Double:
      return 8;
    case G9ESyncColumnType.Decimal:
    case G9ESyncColumnType.Guid:
      return 16;
    case G9ESyncColumnType.DateTime:
      return 9;
    case G9ESyncColumnType.DateTimeOffset:
      return 10;
    case G9ESyncColumnType.String:
    case G9ESyncColumnType.Bytes:
      return prefixedLength(body, position);
    default:
      throw new G9RowBatchFormatError(`Unknown column type ${type}.`);
  }
}

function prefixedLength(body: Uint8Array, position: number): number {
  const cursor = { pos: position };
  const length = readVarint(body, cursor);
  if (length > 2147483647) throw new G9RowBatchFormatError('Row batch value is too long.');
  return cursor.pos - position + length;
}

/** Counts stream lengths (pass 1) or copies fields to their stream cursors (pass 2). */
interface StreamSink {
  lengths: number[] | null;
  cursors: number[] | null;
  output: Uint8Array | null;
}

function put(sink: StreamSink, stream: number, body: Uint8Array, cursor: Cursor, length: number): void {
  const field = available(body, cursor.pos, length);
  if (sink.lengths) {
    sink.lengths[stream] = (sink.lengths[stream] as number) + length;
  } else {
    const cursors = sink.cursors as number[];
    (sink.output as Uint8Array).set(field, cursors[stream] as number);
    cursors[stream] = (cursors[stream] as number) + length;
  }
  cursor.pos += length;
}

function scanRow(rows: Uint8Array, cursor: Cursor, header: SectionHeader, sink: StreamSink): void {
  put(sink, KEY_STREAM, rows, cursor, keyLength(rows, cursor.pos, header.keyType));
  switch (header.op) {
    case G9ERowBatchOp.Upsert:
    case G9ERowBatchOp.Backfill:
      put(sink, 1, rows, cursor, varintLength(rows, cursor.pos));
      scanValues(rows, cursor, header, 2, sink);
      break;
    case G9ERowBatchOp.Tombstone:
    case G9ERowBatchOp.MoveOut:
      put(sink, 1, rows, cursor, varintLength(rows, cursor.pos));
      break;
    case G9ERowBatchOp.KeyVersions:
      put(sink, 1, rows, cursor, varintLength(rows, cursor.pos));
      put(sink, 2, rows, cursor, 8);
      break;
    case G9ERowBatchOp.Push: {
      const op = available(rows, cursor.pos, 1)[0] as number;
      put(sink, 1, rows, cursor, 1);
      put(sink, 2, rows, cursor, varintLength(rows, cursor.pos));
      put(sink, 3, rows, cursor, varintLength(rows, cursor.pos));
      put(sink, 4, rows, cursor, 8);
      put(sink, 5, rows, cursor, varintLength(rows, cursor.pos));
      if (op === G9EPushRowOp.Upsert) scanValues(rows, cursor, header, 6, sink);
      else if (op !== G9EPushRowOp.Delete) throw new G9RowBatchFormatError(`Unknown push op ${op}.`);
      break;
    }
  }
}

function scanValues(rows: Uint8Array, cursor: Cursor, header: SectionHeader, bitmapStream: number, sink: StreamSink): void {
  const count = header.types.length;
  const bitmapBytes = (count + 7) >> 3;
  const bitmap = cursor.pos;
  put(sink, bitmapStream, rows, cursor, bitmapBytes);
  for (let column = 0; column < count; column++) {
    if (((rows[bitmap + (column >> 3)] as number) & (1 << (column & 7))) !== 0) continue;
    put(sink, bitmapStream + 1 + column, rows, cursor, valueLength(rows, cursor.pos, header.types[column] as number));
  }
}

class Output {
  bytes: Uint8Array;
  length = 0;

  constructor(capacity: number) {
    this.bytes = new Uint8Array(capacity);
  }

  ensure(additional: number): void {
    const required = this.length + additional;
    if (required <= this.bytes.length) return;
    if (required > G9RowBatchFormat.MaxDecodedBytes) throw new Error('Row batch exceeds the maximum body size.');
    const next = new Uint8Array(Math.max(required, this.bytes.length * 2));
    next.set(this.bytes.subarray(0, this.length));
    this.bytes = next;
  }

  writeVarint(value: number): void {
    this.ensure(10);
    let v = value;
    while (v >= 128) {
      this.bytes[this.length++] = (v % 128) | 0x80;
      v = Math.floor(v / 128);
    }
    this.bytes[this.length++] = v;
  }
}

/** Converts a version 1 (row-wise) body to the version 2 (column-grouped) layout. */
export function fromRows(rows: Uint8Array): Uint8Array {
  const sections = readInt32LE(available(rows, 0, 4), 0);
  const out = new Output(rows.length + 256);
  writeInt32LE(out.bytes, 0, sections);
  out.length = 4;
  const cursor: Cursor = { pos: 4 };
  for (let s = 0; s < sections; s++) {
    const header = readSectionHeader(rows, cursor);
    const streams = streamCount(header);
    out.ensure(header.length + streams * 10);
    out.bytes.set(rows.subarray(header.start, header.start + header.length), out.length);
    out.length += header.length;

    // Pass 1: every stream's length.
    const lengths: number[] = [];
    for (let i = 0; i < streams; i++) lengths.push(0);
    const rowsStart = cursor.pos;
    const counting: StreamSink = { lengths, cursors: null, output: null };
    for (let row = 0; row < header.rowCount; row++) scanRow(rows, cursor, header, counting);
    for (let i = 0; i < streams; i++) out.writeVarint(lengths[i] as number);

    // Pass 2: copy each field to its stream.
    let total = 0;
    const cursors: number[] = [];
    for (let i = 0; i < streams; i++) {
      cursors.push(out.length + total);
      total += lengths[i] as number;
    }
    out.ensure(total);
    cursor.pos = rowsStart;
    const copying: StreamSink = { lengths: null, cursors, output: out.bytes };
    for (let row = 0; row < header.rowCount; row++) scanRow(rows, cursor, header, copying);
    out.length += total;
  }
  if (cursor.pos !== rows.length) throw new G9RowBatchFormatError('Row batch body has trailing bytes.');
  return out.bytes.subarray(0, out.length);
}

/** Converts a version 2 (column-grouped) body back to the version 1 layout, which is never longer. */
export function toRows(columns: Uint8Array): Uint8Array {
  if (columns.length < 4) throw new G9RowBatchFormatError('Row batch body is too short.');
  const sections = readInt32LE(columns, 0);
  if (sections < 0) throw new G9RowBatchFormatError('Negative section count.');
  const output = new Uint8Array(columns.length);
  writeInt32LE(output, 0, sections);
  const written: Cursor = { pos: 4 };
  const cursor: Cursor = { pos: 4 };
  for (let s = 0; s < sections; s++) {
    const header = readSectionHeader(columns, cursor);
    output.set(columns.subarray(header.start, header.start + header.length), written.pos);
    written.pos += header.length;

    const streams = streamCount(header);
    const starts: number[] = [];
    const ends: number[] = [];
    let offset = 0;
    for (let i = 0; i < streams; i++) {
      const streamLength = readVarint(columns, cursor);
      starts.push(offset);
      offset += streamLength;
      if (offset > columns.length) throw new G9RowBatchFormatError('Row batch stream is truncated.');
      ends.push(offset);
    }
    if (cursor.pos + offset > columns.length) throw new G9RowBatchFormatError('Row batch stream is truncated.');
    for (let i = 0; i < streams; i++) {
      starts[i] = (starts[i] as number) + cursor.pos;
      ends[i] = (ends[i] as number) + cursor.pos;
    }
    for (let row = 0; row < header.rowCount; row++) copyRow(columns, starts, ends, header, output, written);
    for (let i = 0; i < streams; i++) {
      if (starts[i] !== ends[i]) throw new G9RowBatchFormatError('Row batch stream has bytes no row used.');
    }
    cursor.pos += offset;
  }
  if (cursor.pos !== columns.length) throw new G9RowBatchFormatError('Row batch body has trailing bytes.');
  return output.subarray(0, written.pos);
}

function copyRow(columns: Uint8Array, cursors: number[], ends: number[], header: SectionHeader, output: Uint8Array, written: Cursor): void {
  take(columns, cursors, ends, KEY_STREAM, keyLength(columns, cursors[KEY_STREAM] as number, header.keyType), output, written);
  switch (header.op) {
    case G9ERowBatchOp.Upsert:
    case G9ERowBatchOp.Backfill:
      take(columns, cursors, ends, 1, varintLength(columns, cursors[1] as number), output, written);
      copyValues(columns, cursors, ends, header, 2, output, written);
      break;
    case G9ERowBatchOp.Tombstone:
    case G9ERowBatchOp.MoveOut:
      take(columns, cursors, ends, 1, varintLength(columns, cursors[1] as number), output, written);
      break;
    case G9ERowBatchOp.KeyVersions:
      take(columns, cursors, ends, 1, varintLength(columns, cursors[1] as number), output, written);
      take(columns, cursors, ends, 2, 8, output, written);
      break;
    case G9ERowBatchOp.Push: {
      if ((cursors[1] as number) >= (ends[1] as number)) throw new G9RowBatchFormatError('Row batch stream is truncated.');
      const op = columns[cursors[1] as number] as number;
      take(columns, cursors, ends, 1, 1, output, written);
      take(columns, cursors, ends, 2, varintLength(columns, cursors[2] as number), output, written);
      take(columns, cursors, ends, 3, varintLength(columns, cursors[3] as number), output, written);
      take(columns, cursors, ends, 4, 8, output, written);
      take(columns, cursors, ends, 5, varintLength(columns, cursors[5] as number), output, written);
      if (op === G9EPushRowOp.Upsert) copyValues(columns, cursors, ends, header, 6, output, written);
      else if (op !== G9EPushRowOp.Delete) throw new G9RowBatchFormatError(`Unknown push op ${op}.`);
      break;
    }
  }
}

function copyValues(
  columns: Uint8Array,
  cursors: number[],
  ends: number[],
  header: SectionHeader,
  bitmapStream: number,
  output: Uint8Array,
  written: Cursor,
): void {
  const count = header.types.length;
  const bitmapBytes = (count + 7) >> 3;
  const bitmap = written.pos;
  take(columns, cursors, ends, bitmapStream, bitmapBytes, output, written);
  for (let column = 0; column < count; column++) {
    if (((output[bitmap + (column >> 3)] as number) & (1 << (column & 7))) !== 0) continue;
    const stream = bitmapStream + 1 + column;
    take(columns, cursors, ends, stream, valueLength(columns, cursors[stream] as number, header.types[column] as number), output, written);
  }
}

function take(columns: Uint8Array, cursors: number[], ends: number[], stream: number, length: number, output: Uint8Array, written: Cursor): void {
  const start = cursors[stream] as number;
  if (length < 0 || start + length > (ends[stream] as number)) throw new G9RowBatchFormatError('Row batch stream is truncated.');
  if (written.pos + length > output.length) throw new G9RowBatchFormatError('Row batch body is inconsistent.');
  output.set(columns.subarray(start, start + length), written.pos);
  cursors[stream] = start + length;
  written.pos += length;
}
