// Twins of G9SyncData.Abstractions/G9SyncEnums.cs and the protocol enums. Values are the wire numbers; the objects
// give them names (const objects, not TypeScript enums, so the shipped JavaScript stays plain ES2019).

/** The portable column types (`G9ESyncColumnType`). */
export const G9ESyncColumnType = {
  Unspecified: 0,
  Boolean: 1,
  Int32: 2,
  Int64: 3,
  Double: 4,
  Decimal: 5,
  String: 6,
  Guid: 7,
  DateTime: 8,
  DateTimeOffset: 9,
  TimeSpan: 10,
  Bytes: 11,
} as const;
export type G9ESyncColumnType = (typeof G9ESyncColumnType)[keyof typeof G9ESyncColumnType];

const COLUMN_TYPE_NAMES = [
  'Unspecified',
  'Boolean',
  'Int32',
  'Int64',
  'Double',
  'Decimal',
  'String',
  'Guid',
  'DateTime',
  'DateTimeOffset',
  'TimeSpan',
  'Bytes',
] as const;

/** The .NET enum member name of a column type (`G9ESyncColumnType.ToString()`), e.g. `Guid`. */
export function columnTypeName(type: number): string {
  return COLUMN_TYPE_NAMES[type] ?? String(type);
}

/** True for a defined, specified column type. */
export function isColumnType(type: unknown): type is G9ESyncColumnType {
  return typeof type === 'number' && type >= 1 && type <= 11 && Math.floor(type) === type;
}

/** Which way rows of a table may travel (`G9ESyncDirection`). */
export const G9ESyncDirection = {
  Bidirectional: 0,
  DownloadOnly: 1,
  UploadOnly: 2,
} as const;
export type G9ESyncDirection = (typeof G9ESyncDirection)[keyof typeof G9ESyncDirection];

/** How the server resolves a conflicting upload (`G9EConflictPolicy`). */
export const G9EConflictPolicy = {
  ServerWins: 0,
  ClientWins: 1,
  ColumnMerge: 2,
  LastWriterWins: 3,
  Custom: 4,
} as const;
export type G9EConflictPolicy = (typeof G9EConflictPolicy)[keyof typeof G9EConflictPolicy];

/** What the client does with a row that left a subscription (`G9EMoveOutPolicy`). */
export const G9EMoveOutPolicy = {
  Delete: 0,
  Keep: 1,
} as const;
export type G9EMoveOutPolicy = (typeof G9EMoveOutPolicy)[keyof typeof G9EMoveOutPolicy];
