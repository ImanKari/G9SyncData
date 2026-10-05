// Twin of G9SyncData.Client/Model/G9CSyncClientModel.cs: the client model and its builder (the .NET source generator
// has no TypeScript twin; build the model in code, or from the same table list the .NET app generates).
import { G9ESyncColumnType, columnTypeName, isColumnType } from '../abstractions/enums.js';
import { G9SyncErrorCodes, G9SyncException } from '../abstractions/errors.js';
import { G9SyncKey } from '../abstractions/key.js';
import type { G9DtClientTableModel } from '../protocol/dtos.js';

/** A synced column as the client build understands it. */
export interface G9SyncClientColumn {
  readonly name: string;
  readonly type: G9ESyncColumnType;
  readonly nullable: boolean;
}

/** A secondary index the client store creates. */
export interface G9SyncClientIndex {
  readonly name: string;
  readonly columns: readonly string[];
  readonly unique: boolean;
}

/**
 * A synced table of the client model. Column order is the bit order of the local dirty mask; it is local and never sent
 * over the wire (the push maps bits to the negotiated column order).
 */
export class G9SyncClientTable {
  /** Server logical name, e.g. `Operation.Pot`. */
  readonly serverName: string;
  /** Local table name, e.g. `Pot`. */
  readonly localName: string;
  readonly keyColumn: string;
  readonly keyType: G9ESyncColumnType;
  /** Synced columns except the key. */
  readonly columns: readonly G9SyncClientColumn[];
  readonly indexes: readonly G9SyncClientIndex[];
  private readonly _index: { [lowerName: string]: number } = {};

  constructor(
    serverName: string,
    localName: string,
    keyColumn: string,
    keyType: G9ESyncColumnType,
    columns: readonly G9SyncClientColumn[],
    indexes: readonly G9SyncClientIndex[],
  ) {
    this.serverName = serverName;
    this.localName = localName;
    this.keyColumn = keyColumn;
    this.keyType = keyType;
    this.columns = columns;
    this.indexes = indexes;
    for (let i = 0; i < columns.length; i++) this._index[(columns[i] as G9SyncClientColumn).name.toLowerCase()] = i;
  }

  /** Index of a column (its dirty-mask bit), or -1. Case-insensitive, like .NET's OrdinalIgnoreCase lookup. */
  indexOf(column: string): number {
    const index = this._index[column.toLowerCase()];
    return index === undefined ? -1 : index;
  }

  toWire(): G9DtClientTableModel {
    return {
      name: this.serverName,
      keyColumn: this.keyColumn,
      keyType: this.keyType,
      columns: this.columns.map((c) => ({ name: c.name, type: c.type, nullable: c.nullable })),
    };
  }
}

/** The immutable client model (`G9CSyncClientModel`). Build it with {@link G9SyncClientModelBuilder}. */
export class G9SyncClientModel {
  readonly tables: readonly G9SyncClientTable[];
  private readonly _byServer: { [lower: string]: G9SyncClientTable } = {};
  private readonly _byLocal: { [lower: string]: G9SyncClientTable } = {};

  constructor(tables: readonly G9SyncClientTable[]) {
    this.tables = tables;
    for (const table of tables) {
      this._byServer[table.serverName.toLowerCase()] = table;
      this._byLocal[table.localName.toLowerCase()] = table;
    }
  }

  findByServerName(serverName: string): G9SyncClientTable | null {
    return this._byServer[serverName.toLowerCase()] ?? null;
  }

  findByLocalName(localName: string): G9SyncClientTable | null {
    return this._byLocal[localName.toLowerCase()] ?? null;
  }
}

/** Builds the client model (`G9CSyncClientModelBuilder`). */
export class G9SyncClientModelBuilder {
  static readonly MaxColumns = 62;
  private readonly _tables: { server: string; local: string; builder: G9SyncClientTableBuilder }[] = [];

  table(serverName: string, localName: string, configure: (table: G9SyncClientTableBuilder) => void): this {
    if (typeof serverName !== 'string' || serverName.trim() === '') throw new TypeError('A server table name is required.');
    if (typeof localName !== 'string' || localName.trim() === '') throw new TypeError('A local table name is required.');
    const builder = new G9SyncClientTableBuilder();
    configure(builder);
    this._tables.push({ server: serverName, local: localName, builder });
    return this;
  }

  build(): G9SyncClientModel {
    const errors: string[] = [];
    const tables: G9SyncClientTable[] = [];
    const servers: { [lower: string]: true } = {};
    const locals: { [lower: string]: true } = {};
    for (const { server, local, builder } of this._tables) {
      if (servers[server.toLowerCase()]) errors.push(`Server table '${server}' is mapped twice.`);
      servers[server.toLowerCase()] = true;
      if (locals[local.toLowerCase()]) errors.push(`Local table '${local}' is mapped twice.`);
      locals[local.toLowerCase()] = true;
      if (local.toLowerCase().indexOf('g9_') === 0) errors.push(`Local table '${local}' uses the reserved prefix g9_.`);
      const table = builder.build(server, local, errors);
      if (table) tables.push(table);
    }
    if (errors.length > 0) {
      throw new G9SyncException(G9SyncErrorCodes.ModelInvalid, 'The G9SyncData client model is invalid:\n' + errors.map((e) => ' - ' + e).join('\n'));
    }
    return new G9SyncClientModel(tables);
  }
}

export class G9SyncClientTableBuilder {
  private readonly _columns: G9SyncClientColumn[] = [];
  private readonly _indexes: G9SyncClientIndex[] = [];
  private _key: string | null = null;
  private _keyType: number = G9ESyncColumnType.Guid;

  key(column: string, type: G9ESyncColumnType = G9ESyncColumnType.Guid): this {
    if (typeof column !== 'string' || column.trim() === '') throw new TypeError('A key column is required.');
    this._key = column;
    this._keyType = type;
    return this;
  }

  column(name: string, type: G9ESyncColumnType, nullable = true): this {
    if (typeof name !== 'string' || name.trim() === '') throw new TypeError('A column name is required.');
    this._columns.push({ name, type, nullable });
    return this;
  }

  index(name: string | null, unique: boolean, ...columns: string[]): this {
    this._indexes.push({ name: name ?? '', columns, unique });
    return this;
  }

  /** @internal */
  build(server: string, local: string, errors: string[]): G9SyncClientTable | null {
    const before = errors.length;
    if (this._key === null) errors.push(`${local}: no key.`);
    else if (!G9SyncKey.isSupportedKeyType(this._keyType)) errors.push(`${local}: key type ${columnTypeName(this._keyType)} is not supported.`);
    if (this._columns.length > G9SyncClientModelBuilder.MaxColumns) {
      errors.push(`${local}: ${this._columns.length} columns; the maximum is ${G9SyncClientModelBuilder.MaxColumns}.`);
    }
    const names: { [lower: string]: true } = {};
    if (this._key !== null) names[this._key.toLowerCase()] = true;
    for (const column of this._columns) {
      if (names[column.name.toLowerCase()]) errors.push(`${local}: column '${column.name}' is declared twice (or as the key).`);
      names[column.name.toLowerCase()] = true;
      if (!isColumnType(column.type)) errors.push(`${local}.${column.name}: invalid type.`);
    }
    const indexes: G9SyncClientIndex[] = [];
    for (const index of this._indexes) {
      if (index.columns.length === 0) errors.push(`${local}: an index has no columns.`);
      for (const column of index.columns) {
        if (!names[column.toLowerCase()]) errors.push(`${local}: index column '${column}' is not a synced column.`);
      }
      const name = index.name.trim() === '' ? 'IX_' + local + '_' + index.columns.join('_') : index.name;
      indexes.push({ name, columns: index.columns.slice(), unique: index.unique });
    }
    return errors.length > before ? null : new G9SyncClientTable(server, local, this._key as string, this._keyType as G9ESyncColumnType, this._columns.slice(), indexes);
  }
}
