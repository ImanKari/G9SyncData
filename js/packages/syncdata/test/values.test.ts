import { describe, expect, it } from 'vitest';
import { G9Long } from '../src/abstractions/long.js';
import { brotliDecompressJs, jsBrotliDecompressor } from '../src/brotli-js/index.js';
import { decimalToDouble, formatDecimal, parseDecimal } from '../src/internal/dotnetFormat.js';
import { openRowBatch } from '../src/protocol/rowBatch/reader.js';
import { DATE_TIME_MIN_TEXT } from '../src/sqlite/sql.js';
import { formatDateTimeOffset, formatTicks, parseDateTime, parseDateTimeOffset } from '../src/sqlite/values.js';
import { doubleToBits, hexToBytes, type Json, vectors } from './golden/vectors.js';

const storage = vectors.storage;

describe('local storage forms (.NET golden)', () => {
  it('DateTime text', () => {
    expect(storage.dateTimeFormat).toBe("yyyy-MM-dd'T'HH:mm:ss.fffffff");
    expect("'" + DATE_TIME_MIN_TEXT + "'").toBe(storage.dateTimeMinLiteral);
    for (const v of storage.dateTimes) expect(formatTicks(G9Long.parse(v.ticks)), v.ticks).toBe(v.text);
  });

  it('DateTimeOffset text', () => {
    for (const v of storage.dateTimeOffsets) expect(formatDateTimeOffset({ utcTicks: G9Long.parse(v.utcTicks), offsetMinutes: v.offsetMinutes })).toBe(v.text);
  });

  it('decimals: invariant text and (double)decimal to the bit', () => {
    for (const v of storage.decimals) {
      const parts = { lo: v.lo >>> 0, mid: v.mid >>> 0, hi: v.hi >>> 0, scale: v.scale, negative: v.negative };
      expect(formatDecimal(parts), v.text).toBe(v.text);
      expect(doubleToBits(decimalToDouble(parts)), v.text).toBe(v.double);
      const parsed = parseDecimal(v.text)!;
      expect(formatDecimal(parsed)).toBe(v.text);
    }
  });

  it('a REAL-stored decimal pushes what decimal.Parse makes of SQLite\'s text for it', () => {
    for (const v of storage.realDecimals) expect(formatDecimal(parseDecimal(v.sqliteText)!), v.sqliteText).toBe(v.decimal);
  });

  it('DateTime.Parse (RoundtripKind) of the stored forms', () => {
    for (const v of storage.parsedDateTimes) {
      const parsed = parseDateTime(v.text);
      expect([parsed.ticks.toString(), parsed.kind], v.text).toEqual([v.ticks, v.kind]);
    }
  });

  it('DateTimeOffset.Parse with an explicit offset', () => {
    for (const v of storage.parsedDateTimeOffsets) {
      const parsed = parseDateTimeOffset(v.text);
      expect([parsed.utcTicks.toString(), parsed.offsetMinutes], v.text).toEqual([v.utcTicks, v.offsetMinutes]);
    }
  });
});

describe('the pure-JavaScript Brotli decoder', () => {
  it('decodes the .NET writer\'s Brotli frames (quality 4, window 22) in both layouts', async () => {
    const decompressor = jsBrotliDecompressor();
    for (const frame of vectors.frames as Json[]) {
      for (const encoding of ['v1br', 'v2br']) {
        const compressed = await openRowBatch(hexToBytes(frame[encoding]), { decompressor });
        const plain = await openRowBatch(hexToBytes(frame[encoding === 'v1br' ? 'v1' : 'v2']));
        expect(compressed.bodyBytes).toBe(plain.bodyBytes);
        expect(compressed.sectionCount).toBe(plain.sectionCount);
      }
    }
  });

  it('rejects a corrupt stream', () => {
    expect(() => brotliDecompressJs(new Uint8Array([0xff, 0xff, 0xff, 0xff]))).toThrow();
  });
});
