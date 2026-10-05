import { brotliDecompressSync } from 'node:zlib';
import { describe, expect, it } from 'vitest';
import { G9SyncDigest } from '../src/abstractions/digest.js';
import { G9SyncKey } from '../src/abstractions/key.js';
import { G9Long, parseJson, stringifyJson } from '../src/abstractions/long.js';
import { G9SyncParameters } from '../src/abstractions/parameters.js';
import { decimalToDouble, formatDecimal, formatDouble } from '../src/internal/dotnetFormat.js';
import { md5 } from '../src/internal/md5.js';
import { utf8Encode } from '../src/internal/utf8.js';
import { G9ERowBatchOp, readVarintLong, unZigZag, zigZag } from '../src/protocol/rowBatch/format.js';
import { G9RowBatchReader, openRowBatch } from '../src/protocol/rowBatch/reader.js';
import { G9RowBatchWriter } from '../src/protocol/rowBatch/writer.js';
import { G9ESyncColumnType } from '../src/abstractions/enums.js';
import { G9SyncHubErrors } from '../src/protocol/protocol.js';
import { bytesToHex, doubleFromBits, doubleToBits, hexToBytes, type Json, vectors } from './golden/vectors.js';

const nodeBrotli = { decompress: (bytes: Uint8Array, length: number) => new Uint8Array(brotliDecompressSync(bytes, { maxOutputLength: length + 1 })) };

describe('G9Long', () => {
  it('round-trips decimal text, including the extremes', () => {
    for (const text of ['0', '1', '-1', '9007199254740991', '9007199254740992', '-9007199254740993', '9223372036854775807', '-9223372036854775808', '4294967296', '-4294967296']) {
      expect(G9Long.parse(text).toString()).toBe(text);
    }
    expect(G9Long.tryParse('9223372036854775808')).toBeNull();
    expect(G9Long.tryParse(' 1')).toBeNull();
    expect(G9Long.parse('+0042').toString()).toBe('42');
    expect(G9Long.parse('-0').toString()).toBe('0');
  });

  it('does two-complement arithmetic like C# long', () => {
    const max = G9Long.MAX_VALUE;
    expect(max.add(G9Long.ONE).equals(G9Long.MIN_VALUE)).toBe(true);
    expect(G9Long.fromNumber(-5).toString()).toBe('-5');
    expect(G9Long.ONE.shiftLeft(61).or(G9Long.fromNumber(5)).toString()).toBe('2305843009213693957');
    expect(G9Long.ONE.shiftLeft(63).equals(G9Long.MIN_VALUE)).toBe(true);
    expect(G9Long.fromNumber(-8).shiftRight(1).toString()).toBe('-4');
    expect(G9Long.parse('63895212896').multiplySmall(10000000).add(G9Long.fromNumber(1234567)).toString()).toBe('638952128961234567');
    expect(G9Long.parse('-1').toUnsignedString()).toBe('18446744073709551615');
    const { quotient, remainder } = G9Long.parse('638952128961234567').divModUnsignedSmall(10000000);
    expect(quotient.toString()).toBe('63895212896');
    expect(remainder).toBe(1234567);
  });

  it('keeps long integers exact through JSON', () => {
    const text = stringifyJson({ a: [G9Long.MIN_VALUE, G9Long.parse('9007199254740993'), 5], s: 'x' });
    expect(text).toBe('{"a":[-9223372036854775808,9007199254740993,5],"s":"x"}');
    const parsed = parseJson('{"m":2305843009213693957,"n":12,"s":"1234567890123456789","p":"{\\"a\\":12345678901234567890}"}') as Json;
    expect(parsed.m).toBeInstanceOf(G9Long);
    expect(parsed.m.toString()).toBe('2305843009213693957');
    expect(parsed.n).toBe(12);
    expect(parsed.s).toBe('1234567890123456789');
    expect(parsed.p).toBe('{"a":12345678901234567890}');
  });
});

describe('golden vectors (.NET)', () => {
  it('varints', () => {
    for (const v of vectors.varints) {
      expect(readVarintLong(hexToBytes(v.hex), { pos: 0 }).toUnsignedString()).toBe(v.value);
      if (BigInt(v.value) > 9223372036854775807n) continue;
      const writer = new G9RowBatchWriter();
      writer.beginSection(G9ERowBatchOp.Tombstone, 0, G9ESyncColumnType.Int64, []);
      // A key-only row is the key's zig-zag varint ('0' → one byte), then the version varint.
      writer.addKey('0', G9Long.parse(v.value));
      writer.endSection();
      const frame = writer.toFrame({ compression: 0 });
      // header 10 + section count 4 + op 1 + table id 1 + key type 1 + column count 1 + row count 4 + key 1
      expect(bytesToHex(frame.subarray(10 + 4 + 1 + 1 + 1 + 1 + 4 + 1))).toBe(v.hex);
    }
  });

  it('zig-zag', () => {
    for (const v of vectors.zigzag) {
      const zz = zigZag(G9Long.parse(v.value));
      expect(zz.toUnsignedString()).toBe(v.zigzag);
      expect(unZigZag(zz).toString()).toBe(v.value);
    }
  });

  it('md5', () => {
    for (const v of vectors.md5) expect(bytesToHex(md5(utf8Encode(v.text)))).toBe(v.hex);
  });

  it('digest buckets and row hashes', () => {
    for (const v of vectors.digests) {
      expect(G9SyncDigest.getBucket(v.key)).toBe(v.bucket);
      expect(G9SyncDigest.getBucketUtf8(utf8Encode(v.key))).toBe(v.bucket);
      expect(G9SyncDigest.getRowHash(v.key, G9Long.parse(v.version), v.computed).toString()).toBe(v.rowHash);
    }
  });

  it('canonical texts of computed values', () => {
    for (const v of vectors.canonicalText) {
      let value: unknown;
      let storage: 'integer' | 'real' | undefined;
      switch (v.kind) {
        case 'integer':
          value = G9Long.parse(v.value).toSqlValue();
          storage = 'integer';
          break;
        case 'real':
          value = doubleFromBits(v.value.bits);
          storage = 'real';
          break;
        case 'text':
          value = v.value;
          break;
        case 'blob':
          value = hexToBytes(v.value).buffer;
          break;
        default:
          value = null;
      }
      expect(G9SyncDigest.canonicalText(v.type, value, storage), JSON.stringify(v)).toBe(v.text);
      if (v.kind === 'real') expect(formatDouble(value as number)).toBe(v.value.text);
    }
  });

  it('key normalisation', () => {
    for (const v of vectors.keys) expect(G9SyncKey.tryNormalize(v.type, v.text), JSON.stringify(v)).toBe(v.canonical);
  });

  it('subscription parameters and keys', () => {
    for (const c of vectors.parameters) {
      const p = new G9SyncParameters();
      for (const op of c.ops) {
        switch (op.op) {
          case 'guid':
            p.setGuid(op.name, op.value);
            break;
          case 'int64':
            p.setInt64(op.name, G9Long.parse(op.value));
            break;
          case 'int32':
            p.setInt32(op.name, Number(op.value));
            break;
          case 'bool':
            p.setBoolean(op.name, op.value);
            break;
          case 'string':
            p.setString(op.name, op.value);
            break;
          case 'guidList':
            p.setGuidList(op.name, op.value);
            break;
          case 'int64List':
            p.setInt64List(op.name, (op.value as string[]).map((x) => G9Long.parse(x)));
            break;
          case 'stringList':
            p.setStringList(op.name, op.value);
            break;
        }
      }
      expect(p.toCanonicalString()).toBe(c.canonical);
      expect(G9SyncParameters.buildSubscriptionKey(c.subscription, p)).toBe(c.key);
    }
  });

  it('hub error markers', () => {
    const completion = JSON.parse(vectors.hub.json.error.replace('', ''));
    const parsed = G9SyncHubErrors.tryParse(completion.error)!;
    expect(parsed.code).toBe('G9S-SRV-BUSY');
    expect(parsed.retryable).toBe(true);
    expect(parsed.message).toBe('The server is busy.');
    expect(G9SyncHubErrors.tryParse('no marker')).toBeNull();
  });
});

// ---------------------------------------------------------------- frames

function valueOf(reader: G9RowBatchReader, column: number, type: number): Json {
  if (reader.isNull(column)) return null;
  switch (type) {
    case G9ESyncColumnType.Boolean:
      return reader.getBoolean(column);
    case G9ESyncColumnType.Int32:
      return reader.getInt32(column);
    case G9ESyncColumnType.Int64:
      return reader.getInt64(column).toString();
    case G9ESyncColumnType.Double: {
      const d = reader.getDouble(column);
      return { bits: doubleToBits(d), text: formatDouble(d) };
    }
    case G9ESyncColumnType.Decimal: {
      const p = reader.getDecimal(column);
      return { lo: p.lo | 0, mid: p.mid | 0, hi: p.hi | 0, scale: p.scale, negative: p.negative, text: formatDecimal(p), double: doubleToBits(decimalToDouble(p)) };
    }
    case G9ESyncColumnType.String:
      return reader.getString(column);
    case G9ESyncColumnType.Guid:
      return reader.getGuid(column);
    case G9ESyncColumnType.DateTime: {
      const v = reader.getDateTime(column);
      return { ticks: v.ticks.toString(), kind: v.kind };
    }
    case G9ESyncColumnType.DateTimeOffset: {
      const v = reader.getDateTimeOffset(column);
      return { utcTicks: v.utcTicks.toString(), offsetMinutes: v.offsetMinutes };
    }
    case G9ESyncColumnType.TimeSpan:
      return { ticks: reader.getTimeSpan(column).toString() };
    case G9ESyncColumnType.Bytes:
      return { hex: bytesToHex(reader.getBytes(column)) };
  }
  throw new Error('type');
}

function contentOf(reader: G9RowBatchReader): Json[] {
  const sections: Json[] = [];
  while (reader.nextSection()) {
    const types = reader.columnTypes.slice();
    const rows: Json[] = [];
    while (reader.readRow()) {
      const values = () => types.map((t, i) => valueOf(reader, i, t));
      switch (reader.op) {
        case G9ERowBatchOp.Upsert:
        case G9ERowBatchOp.Backfill:
          rows.push({ key: reader.key, version: reader.versionLong.toString(), values: values() });
          break;
        case G9ERowBatchOp.Tombstone:
        case G9ERowBatchOp.MoveOut:
          rows.push({ key: reader.key, version: reader.versionLong.toString() });
          break;
        case G9ERowBatchOp.KeyVersions:
          rows.push({ key: reader.key, version: reader.versionLong.toString(), rowHash: reader.rowHash.toString() });
          break;
        case G9ERowBatchOp.Push: {
          const row: Json = {
            key: reader.key,
            pushOp: reader.pushOp,
            sequence: reader.sequenceLong.toString(),
            baseVersion: reader.baseVersionLong.toString(),
            dirtyMask: reader.dirtyMask.toString(),
            hlcMs: reader.hlcLong.toString(),
          };
          if (reader.hasValues) row.values = values();
          rows.push(row);
          break;
        }
      }
    }
    sections.push({ op: reader.op, tableId: reader.tableId, keyType: reader.keyType, types, rows });
  }
  return sections;
}

function writeContent(sections: Json[]): G9RowBatchWriter {
  const writer = new G9RowBatchWriter(64);
  for (const section of sections) {
    writer.beginSection(section.op, section.tableId, section.keyType, section.types);
    for (const row of section.rows) {
      switch (section.op) {
        case G9ERowBatchOp.Upsert:
        case G9ERowBatchOp.Backfill:
          writer.beginRow(row.key, G9Long.parse(row.version));
          writeValues(writer, section.types, row.values);
          writer.endRow();
          break;
        case G9ERowBatchOp.Tombstone:
        case G9ERowBatchOp.MoveOut:
          writer.addKey(row.key, G9Long.parse(row.version));
          break;
        case G9ERowBatchOp.KeyVersions:
          writer.addKeyVersion(row.key, G9Long.parse(row.version), G9Long.parse(row.rowHash));
          break;
        case G9ERowBatchOp.Push:
          writer.beginPushRow(row.key, row.pushOp, G9Long.parse(row.sequence), G9Long.parse(row.baseVersion), G9Long.parse(row.dirtyMask), G9Long.parse(row.hlcMs));
          if (row.pushOp === 1) writeValues(writer, section.types, row.values);
          writer.endRow();
          break;
      }
    }
    writer.endSection();
  }
  return writer;
}

function writeValues(writer: G9RowBatchWriter, types: number[], values: Json[]): void {
  types.forEach((type, i) => {
    const v = values[i];
    if (v === null) return writer.writeNull();
    switch (type) {
      case G9ESyncColumnType.Boolean:
        return writer.writeBoolean(v);
      case G9ESyncColumnType.Int32:
        return writer.writeInt32(v);
      case G9ESyncColumnType.Int64:
        return writer.writeInt64(G9Long.parse(v));
      case G9ESyncColumnType.Double:
        return writer.writeDouble(doubleFromBits(v.bits));
      case G9ESyncColumnType.Decimal:
        return writer.writeDecimal({ lo: v.lo >>> 0, mid: v.mid >>> 0, hi: v.hi >>> 0, scale: v.scale, negative: v.negative });
      case G9ESyncColumnType.String:
        return writer.writeString(v);
      case G9ESyncColumnType.Guid:
        return writer.writeGuid(v);
      case G9ESyncColumnType.DateTime:
        return writer.writeDateTime({ ticks: G9Long.parse(v.ticks), kind: v.kind });
      case G9ESyncColumnType.DateTimeOffset:
        return writer.writeDateTimeOffset({ utcTicks: G9Long.parse(v.utcTicks), offsetMinutes: v.offsetMinutes });
      case G9ESyncColumnType.TimeSpan:
        return writer.writeTimeSpan(G9Long.parse(v.ticks));
      case G9ESyncColumnType.Bytes:
        return writer.writeBytes(hexToBytes(v.hex));
    }
  });
}

/** The decimal objects of the vectors carry the .NET text and double next to the parts; compare what both sides have. */
function comparable(sections: Json[]): Json[] {
  return JSON.parse(JSON.stringify(sections));
}

describe('G9RowBatch frames (.NET golden)', () => {
  for (const frame of vectors.frames) {
    it(`reads '${frame.name}' in all four encodings`, async () => {
      for (const encoding of ['v1', 'v2', 'v1br', 'v2br']) {
        const reader = await openRowBatch(hexToBytes(frame[encoding]), { decompressor: nodeBrotli });
        expect(contentOf(reader), encoding).toEqual(comparable(frame.sections));
      }
    });

    it(`writes '${frame.name}' to the same bytes`, () => {
      const writer = writeContent(frame.sections);
      expect(bytesToHex(writer.toFrame({ compression: 0, version: 1 }))).toBe(frame.v1);
      expect(bytesToHex(writer.toFrame({ compression: 0, version: 2 }))).toBe(frame.v2);
    });
  }

  it('refuses a compressed frame without a decompressor, a truncated frame and an over-budget body', async () => {
    const all = vectors.frames.find((f: Json) => f.name === 'all-ops');
    await expect(openRowBatch(hexToBytes(all.v1br))).rejects.toThrow(/decompressor/);
    await expect(openRowBatch(hexToBytes(all.v1).subarray(0, 40))).rejects.toThrow(/length mismatch/);
    await expect(openRowBatch(hexToBytes(all.v1), { maxBodyBytes: 16 })).rejects.toThrow(/accepts at most/);
  });
});
