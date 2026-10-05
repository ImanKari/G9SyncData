// Value forms between G9RowBatch frames and the local SQLite storage — the twins of BindValue / ToDbValue (frame → cell)
// and WriteLocalValue / EstimateEncodedBytes (cell → push frame) of the .NET store, with the same text formats:
// DateTime `yyyy-MM-dd'T'HH:mm:ss.fffffff` (or ticks), DateTimeOffset `…fffffffzzz`, GUIDs lower-case "D", decimals as
// REAL ((double)decimal, bit-exact) or invariant text.
import { G9ESyncColumnType } from '../abstractions/enums.js';
import { normalizeGuid, readGuidBytes } from '../abstractions/guid.js';
import { G9Long } from '../abstractions/long.js';
import { decimalToDouble, formatDecimal, parseDecimal } from '../internal/dotnetFormat.js';
import { utf8Encode } from '../internal/utf8.js';
import type { G9RowBatchReader } from '../protocol/rowBatch/reader.js';
import type { G9DateTimeOffsetValue, G9DateTimeValue, G9RowBatchWriter } from '../protocol/rowBatch/writer.js';
import { isInt64Tag, type G9SqlValue } from './driver.js';
import { G9ESqliteDateTimeStorage, G9ESqliteDecimalStorage, type G9SqliteStorageOptions } from './sql.js';

const TICKS_PER_SECOND = 10000000;
const SECONDS_PER_DAY = 86400;
/** Days from 0001-01-01 to 1970-01-01. */
const UNIX_EPOCH_DAYS = 719162;

function pad(value: number, width: number): string {
  let text = String(value);
  while (text.length < width) text = '0' + text;
  return text;
}

/** Proleptic Gregorian civil date of a day number (days since 1970-01-01), Howard Hinnant's algorithm. */
function civilFromDays(z: number): { y: number; m: number; d: number } {
  const zz = z + 719468;
  const era = Math.floor(zz / 146097);
  const doe = zz - era * 146097;
  const yoe = Math.floor((doe - Math.floor(doe / 1460) + Math.floor(doe / 36524) - Math.floor(doe / 146096)) / 365);
  const y = yoe + era * 400;
  const doy = doe - (365 * yoe + Math.floor(yoe / 4) - Math.floor(yoe / 100));
  const mp = Math.floor((5 * doy + 2) / 153);
  const d = doy - Math.floor((153 * mp + 2) / 5) + 1;
  const m = mp < 10 ? mp + 3 : mp - 9;
  return { y: m <= 2 ? y + 1 : y, m, d };
}

/** Days since 1970-01-01 of a civil date. */
function daysFromCivil(year: number, month: number, day: number): number {
  const y = month <= 2 ? year - 1 : year;
  const era = Math.floor(y / 400);
  const yoe = y - era * 400;
  const mp = month > 2 ? month - 3 : month + 9;
  const doy = Math.floor((153 * mp + 2) / 5) + day - 1;
  const doe = yoe * 365 + Math.floor(yoe / 4) - Math.floor(yoe / 100) + doy;
  return era * 146097 + doe - 719468;
}

/** `ticks` (≥ 0) as `yyyy-MM-dd'T'HH:mm:ss.fffffff`. */
export function formatTicks(ticks: G9Long): string {
  const { quotient, remainder } = ticks.divModUnsignedSmall(TICKS_PER_SECOND);
  const seconds = quotient.toNumber();
  const days = Math.floor(seconds / SECONDS_PER_DAY);
  let rest = seconds - days * SECONDS_PER_DAY;
  const { y, m, d } = civilFromDays(days - UNIX_EPOCH_DAYS);
  const hh = Math.floor(rest / 3600);
  rest -= hh * 3600;
  const mm = Math.floor(rest / 60);
  const ss = rest - mm * 60;
  return pad(y, 4) + '-' + pad(m, 2) + '-' + pad(d, 2) + 'T' + pad(hh, 2) + ':' + pad(mm, 2) + ':' + pad(ss, 2) + '.' + pad(remainder, 7);
}

/** Ticks of a civil date-time (fraction in ticks, 0..9999999). */
export function ticksOf(year: number, month: number, day: number, hour: number, minute: number, second: number, fractionTicks: number): G9Long {
  const days = daysFromCivil(year, month, day) + UNIX_EPOCH_DAYS;
  const seconds = days * SECONDS_PER_DAY + hour * 3600 + minute * 60 + second;
  return G9Long.fromNumber(seconds).multiplySmall(TICKS_PER_SECOND).add(G9Long.fromNumber(fractionTicks));
}

const TICKS_PER_MINUTE = 600000000;

/** A DateTimeOffset as `yyyy-MM-dd'T'HH:mm:ss.fffffffzzz` (local clock time and the offset). */
export function formatDateTimeOffset(value: G9DateTimeOffsetValue): string {
  const local = value.utcTicks.add(G9Long.fromNumber(value.offsetMinutes * TICKS_PER_MINUTE));
  const minutes = Math.abs(value.offsetMinutes);
  return formatTicks(local) + (value.offsetMinutes < 0 ? '-' : '+') + pad(Math.floor(minutes / 60), 2) + ':' + pad(minutes % 60, 2);
}

/** Ticks of 1970-01-01T00:00:00. */
const UNIX_EPOCH_TICKS = G9Long.fromNumber(UNIX_EPOCH_DAYS * SECONDS_PER_DAY).multiplySmall(TICKS_PER_SECOND);

/** The device's UTC offset in minutes at a moment given as ticks of its UTC time (what .NET's TimeZoneInfo.Local gives). */
function localOffsetMinutesAtUtc(utcTicks: G9Long): number {
  const ms = utcTicks.subtract(UNIX_EPOCH_TICKS).toNumber() / 10000;
  return -new Date(ms).getTimezoneOffset();
}

/** Epoch milliseconds of ticks (UTC). */
export function ticksToUnixMs(ticks: G9Long): number {
  return ticks.subtract(UNIX_EPOCH_TICKS).toNumber() / 10000;
}

const DATE_TIME_TEXT = /^\s*(\d{4})-(\d{1,2})-(\d{1,2})(?:[T ](\d{1,2}):(\d{2})(?::(\d{2})(?:\.(\d+))?)?)?\s*(Z|[+-]\d{2}:?\d{2})?\s*$/i;

interface ParsedText {
  ticks: G9Long;
  zone: string | null;
}

function parseText(text: string): ParsedText | null {
  const m = DATE_TIME_TEXT.exec(text);
  if (!m) return null;
  const fractionDigits = m[7] ?? '';
  // .NET keeps 7 fraction digits (ticks); more are rounded away.
  let fraction = parseInt((fractionDigits + '0000000').slice(0, 7), 10);
  if (fractionDigits.length > 7 && fractionDigits.charCodeAt(7) >= 53) fraction += 1;
  const ticks = ticksOf(+(m[1] as string), +(m[2] as string), +(m[3] as string), m[4] ? +m[4] : 0, m[5] ? +m[5] : 0, m[6] ? +m[6] : 0, 0).add(G9Long.fromNumber(fraction));
  return { ticks, zone: m[8] ?? null };
}

function zoneMinutes(zone: string): number {
  if (zone.toUpperCase() === 'Z') return 0;
  const sign = zone.charAt(0) === '-' ? -1 : 1;
  const digits = zone.slice(1).replace(':', '');
  return sign * (parseInt(digits.slice(0, 2), 10) * 60 + parseInt(digits.slice(2, 4), 10));
}

/**
 * `DateTime.Parse(text, InvariantCulture, DateTimeStyles.RoundtripKind)` for the ISO-like forms SQLite stores
 * (`yyyy-MM-dd[ T]HH:mm[:ss[.fffffff]]` with an optional `Z` or offset, or a date alone): `Z` gives a UTC value, an
 * offset converts to device-local time (kind Local), none gives an unspecified kind. Throws for anything else.
 */
export function parseDateTime(text: string): G9DateTimeValue {
  const parsed = parseText(text);
  if (!parsed) throw new SyntaxError(`'${text}' is not a DateTime this store reads.`);
  if (parsed.zone === null) return { ticks: parsed.ticks, kind: 0 };
  const offset = zoneMinutes(parsed.zone);
  if (parsed.zone.toUpperCase() === 'Z') return { ticks: parsed.ticks, kind: 1 };
  const utc = parsed.ticks.subtract(G9Long.fromNumber(offset * TICKS_PER_MINUTE));
  return { ticks: utc.add(G9Long.fromNumber(localOffsetMinutesAtUtc(utc) * TICKS_PER_MINUTE)), kind: 2 };
}

/** `DateTimeOffset.Parse(text, InvariantCulture)` for the same forms; without an offset the device's local offset applies. */
export function parseDateTimeOffset(text: string): G9DateTimeOffsetValue {
  const parsed = parseText(text);
  if (!parsed) throw new SyntaxError(`'${text}' is not a DateTimeOffset this store reads.`);
  if (parsed.zone !== null) {
    const offset = zoneMinutes(parsed.zone);
    return { utcTicks: parsed.ticks.subtract(G9Long.fromNumber(offset * TICKS_PER_MINUTE)), offsetMinutes: offset };
  }
  // A local clock time: the offset in force then (two guesses settle a DST boundary the way .NET's lookup does).
  let offset = localOffsetMinutesAtUtc(parsed.ticks);
  offset = localOffsetMinutesAtUtc(parsed.ticks.subtract(G9Long.fromNumber(offset * TICKS_PER_MINUTE)));
  return { utcTicks: parsed.ticks.subtract(G9Long.fromNumber(offset * TICKS_PER_MINUTE)), offsetMinutes: offset };
}

/** A frame value in its local storage form (`BindValue` / `ToDbValue`). */
export function frameValueToSql(reader: G9RowBatchReader, column: number, type: number, options: G9SqliteStorageOptions): G9SqlValue {
  if (reader.isNull(column)) return null;
  switch (type) {
    case G9ESyncColumnType.Boolean:
      return reader.getBoolean(column) ? 1 : 0;
    case G9ESyncColumnType.Int32:
      return reader.getInt32(column);
    case G9ESyncColumnType.Int64:
      return reader.getInt64(column).toSqlValue();
    case G9ESyncColumnType.TimeSpan:
      return reader.getTimeSpan(column).toSqlValue();
    case G9ESyncColumnType.Double:
      return reader.getDouble(column);
    case G9ESyncColumnType.Decimal: {
      const parts = reader.getDecimal(column);
      return options.decimalStorage === G9ESqliteDecimalStorage.Real ? decimalToDouble(parts) : formatDecimal(parts);
    }
    case G9ESyncColumnType.String:
      return reader.getString(column);
    case G9ESyncColumnType.Guid:
      return reader.getGuid(column);
    case G9ESyncColumnType.DateTime: {
      const value = reader.getDateTime(column);
      return options.dateTimeStorage === G9ESqliteDateTimeStorage.Ticks ? value.ticks.toSqlValue() : formatTicks(value.ticks);
    }
    case G9ESyncColumnType.DateTimeOffset:
      return formatDateTimeOffset(reader.getDateTimeOffset(column));
    case G9ESyncColumnType.Bytes: {
      const bytes = reader.getBytes(column);
      const copy = new Uint8Array(bytes.length);
      copy.set(bytes);
      return copy.buffer;
    }
    default:
      throw new Error(`Unsupported column type ${type}.`);
  }
}

/** The SELECT expression the push reads a column with: decimals and strings as SQLite's own text (`GetString`). */
export function pushSelectExpression(quotedColumn: string, type: number): string {
  return type === G9ESyncColumnType.Decimal || type === G9ESyncColumnType.String ? 'CAST(' + quotedColumn + ' AS TEXT)' : quotedColumn;
}

function cellToLong(value: G9SqlValue): G9Long {
  if (typeof value === 'number') return G9Long.fromNumber(Math.trunc(value));
  if (isInt64Tag(value)) return G9Long.parse(value.int64);
  if (typeof value === 'string') {
    // sqlite3_column_int64 of text: the leading integer, 0 when there is none.
    const m = /^\s*([+-]?\d+)/.exec(value);
    return m ? (G9Long.tryParse(m[1] as string) ?? G9Long.ZERO) : G9Long.ZERO;
  }
  return G9Long.ZERO;
}

function cellToText(value: G9SqlValue): string {
  if (typeof value === 'string') return value;
  if (typeof value === 'number') return String(value);
  if (isInt64Tag(value)) return value.int64;
  return '';
}

/** .NET `new Guid(byte[])`: the first three fields little-endian. */
function guidFromDotNetBytes(buffer: ArrayBuffer): string {
  const b = new Uint8Array(buffer);
  if (b.length !== 16) throw new RangeError('A GUID BLOB must be 16 bytes.');
  const ordered = new Uint8Array([b[3] as number, b[2] as number, b[1] as number, b[0] as number, b[5] as number, b[4] as number, b[7] as number, b[6] as number]);
  const full = new Uint8Array(16);
  full.set(ordered, 0);
  full.set(b.subarray(8), 8);
  return readGuidBytes(full, 0);
}

/** Writes a local cell into a push row (`WriteLocalValue`). */
export function writeLocalValue(writer: G9RowBatchWriter, value: G9SqlValue, type: number, options: G9SqliteStorageOptions): void {
  if (value === null) {
    writer.writeNull();
    return;
  }
  switch (type) {
    case G9ESyncColumnType.Boolean:
      writer.writeBoolean(!cellToLong(value).isZero());
      return;
    case G9ESyncColumnType.Int32: {
      const long = cellToLong(value);
      if (long.compare(G9Long.fromNumber(-2147483648)) < 0 || long.compare(G9Long.fromNumber(2147483647)) > 0) {
        throw new RangeError('Arithmetic operation resulted in an overflow (an Int32 column holds a value outside Int32).');
      }
      writer.writeInt32(long.toNumber());
      return;
    }
    case G9ESyncColumnType.Int64:
      writer.writeInt64(cellToLong(value));
      return;
    case G9ESyncColumnType.Double:
      writer.writeDouble(typeof value === 'number' ? value : isInt64Tag(value) ? Number(value.int64) : Number(cellToText(value)) || 0);
      return;
    case G9ESyncColumnType.Decimal: {
      const parts = parseDecimal(cellToText(value));
      if (!parts) throw new SyntaxError(`'${cellToText(value)}' is not a decimal.`);
      writer.writeDecimal(parts);
      return;
    }
    case G9ESyncColumnType.String:
      writer.writeString(cellToText(value));
      return;
    case G9ESyncColumnType.Guid:
      writer.writeGuid(value instanceof ArrayBuffer ? guidFromDotNetBytes(value) : normalizeGuid(cellToText(value)));
      return;
    case G9ESyncColumnType.DateTime:
      if (options.dateTimeStorage === G9ESqliteDateTimeStorage.Ticks || typeof value === 'number' || isInt64Tag(value)) {
        if (typeof value === 'number' && Math.floor(value) !== value) throw new SyntaxError(`'${value}' is not a DateTime this store reads.`);
        writer.writeDateTime({ ticks: cellToLong(value), kind: 0 });
      } else {
        writer.writeDateTime(parseDateTime(cellToText(value)));
      }
      return;
    case G9ESyncColumnType.DateTimeOffset:
      writer.writeDateTimeOffset(parseDateTimeOffset(cellToText(value)));
      return;
    case G9ESyncColumnType.TimeSpan:
      writer.writeTimeSpan(cellToLong(value));
      return;
    case G9ESyncColumnType.Bytes:
      if (!(value instanceof ArrayBuffer)) throw new TypeError('Unable to cast a non-BLOB value to Byte[] (a Bytes column holds text).');
      writer.writeBytes(new Uint8Array(value));
      return;
    default:
      throw new Error(`Unsupported column type ${type}.`);
  }
}

/** A conservative upper bound of one value's encoded size in a push frame (`EstimateEncodedBytes`). */
export function estimateEncodedBytes(value: G9SqlValue, type: number): number {
  if (value === null) return 1;
  switch (type) {
    case G9ESyncColumnType.String:
      return 5 + utf8Encode(cellToText(value)).length;
    case G9ESyncColumnType.Bytes:
      return 5 + (value instanceof ArrayBuffer ? value.byteLength : utf8Encode(cellToText(value)).length);
    case G9ESyncColumnType.Decimal:
    case G9ESyncColumnType.Guid:
      return 17;
    default:
      return 9;
  }
}
