import { describe, expect, it } from 'vitest';
import { G9ESyncColumnType } from '../src/abstractions/enums.js';
import { G9SyncClientModelBuilder } from '../src/client/model.js';
import {
  createCommandTriggers,
  createTable,
  createTriggers,
  dropTriggers,
  G9ESqliteDateTimeStorage,
  G9ESqliteDecimalStorage,
  METADATA,
  modelHash,
  splitSql,
} from '../src/sqlite/sql.js';
import { vectors } from './golden/vectors.js';

const T = G9ESyncColumnType;

/** The model JsGoldenVectorsTests.Model() builds, column for column. */
function goldenModel() {
  return new G9SyncClientModelBuilder()
    .table('Operation.Pot', 'Pot', (t) =>
      t
        .key('Id')
        .column('RidgeId', T.Guid)
        .column('Code', T.String, false)
        .column('PlantedAt', T.DateTime)
        .column('Weight', T.Decimal)
        .column('Count', T.Int32, false)
        .column('Alive', T.Boolean, false)
        .column('Photo', T.Bytes)
        .column('Moment', T.DateTimeOffset, false)
        .column('Span', T.TimeSpan)
        .column('Ratio', T.Double, false)
        .column('Big', T.Int64)
        .column('Due', T.DateTime, false)
        .column('Price', T.Decimal, false)
        .column('Owner', T.Guid, false)
        .column('Blob', T.Bytes, false)
        .index(null, false, 'RidgeId')
        .index('UX_Pot_Code', true, 'Code', 'Count'),
    )
    .table('Base.Site', 'Site', (t) => t.key('SiteId', T.Int64).column('Name', T.String))
    .table('Base.Tag', 'Tag]x', (t) => t.key('Name', T.String).column('Color', T.String, false))
    .table('Wide.T', 'Wide', (t) => {
      t.key('Id', T.Int32);
      for (let i = 0; i < G9SyncClientModelBuilder.MaxColumns; i++) t.column('C' + i, T.Int32);
    })
    .table('Empty.T', 'Bare', (t) => t.key('Id'))
    .build();
}

const text = { dateTimeStorage: G9ESqliteDateTimeStorage.Text, decimalStorage: G9ESqliteDecimalStorage.Real, dateTimeFormat: "yyyy-MM-dd'T'HH:mm:ss.fffffff" };
const exact = { dateTimeStorage: G9ESqliteDateTimeStorage.Ticks, decimalStorage: G9ESqliteDecimalStorage.Text, dateTimeFormat: "yyyy-MM-dd'T'HH:mm:ss.fffffff" };

describe('local schema (.NET golden)', () => {
  it('metadata script is identical', () => {
    expect(METADATA).toBe(vectors.model.metadata);
  });

  it('every table: model hash, CREATE TABLE in both storage modes, triggers', () => {
    const model = goldenModel();
    expect(model.tables.length).toBe(vectors.model.tables.length);
    model.tables.forEach((table, i) => {
      const v = vectors.model.tables[i];
      expect(table.localName).toBe(v.localName);
      expect(modelHash(table)).toBe(v.modelHash);
      expect(createTable(table, text)).toBe(v.createTable);
      expect(createTable(table, exact)).toBe(v.createTableExact);
      expect(createTriggers(table, v.tid)).toBe(v.createTriggers);
      expect(createCommandTriggers(table, v.tid)).toBe(v.createCommandTriggers);
      expect(dropTriggers(table)).toBe(v.dropTriggers);
    });
  });

  it('splits scripts into single statements, keeping trigger bodies whole', () => {
    const model = goldenModel();
    const pot = model.tables[0]!;
    expect(splitSql(createTriggers(pot, 7))).toHaveLength(3);
    expect(splitSql(createCommandTriggers(pot, 7))).toHaveLength(3);
    expect(splitSql(dropTriggers(pot))).toHaveLength(6);
    expect(splitSql(METADATA)).toHaveLength(splitSql(METADATA).filter((s) => s.length > 0).length);
    expect(splitSql("SELECT 'a;b'; SELECT [x;y] FROM t; -- c;\nSELECT 1")).toEqual(["SELECT 'a;b'", 'SELECT [x;y] FROM t', '-- c;\nSELECT 1']);
  });
});
