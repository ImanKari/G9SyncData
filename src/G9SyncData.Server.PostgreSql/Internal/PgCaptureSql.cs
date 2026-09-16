using System.Text;
using G9SyncData.Server.Model;

namespace G9SyncData.Server.PostgreSql.Internal;

/// <summary>
///     Generates the capture DDL. Proven by the design spike (AiGuides/06-Server-PostgreSql.md):
///     statement-level triggers with transition tables stamp <c>pg_current_xact_id()</c> and a per-row monotonic version,
///     record which captured columns changed, skip no-op updates, and notify at commit.
/// </summary>
internal static class PgCaptureSql
{
    public const int SchemaVersion = 1;
    private const string Origin = "nullif(current_setting('g9sync.origin', true), '')::uuid";

    public static string Base(string schema) => $$"""
        CREATE SCHEMA IF NOT EXISTS {{schema}};
        CREATE TABLE IF NOT EXISTS {{schema}}.install (
            id integer PRIMARY KEY CHECK (id = 1),
            epoch uuid NOT NULL,
            schema_version integer NOT NULL,
            library_version text NOT NULL,
            installed_at timestamptz NOT NULL DEFAULT now());
        CREATE TABLE IF NOT EXISTS {{schema}}.tables (
            table_id serial PRIMARY KEY,
            schema_name text NOT NULL,
            table_name text NOT NULL,
            columns text[] NOT NULL DEFAULT '{}',
            capture_hash text NULL,
            max_removed_xid xid8 NULL,
            updated_at timestamptz NOT NULL DEFAULT now(),
            UNIQUE (schema_name, table_name));
        CREATE TABLE IF NOT EXISTS {{schema}}.clients (
            client_id uuid PRIMARY KEY,
            user_id text NULL,
            app_version text NULL,
            last_push_batch bigint NOT NULL DEFAULT 0,
            last_push_result bytea NULL,
            first_seen timestamptz NOT NULL DEFAULT now(),
            last_seen timestamptz NOT NULL DEFAULT now());
        CREATE SEQUENCE IF NOT EXISTS {{schema}}.ver_seq;
        CREATE OR REPLACE FUNCTION {{schema}}.merge_col_vers(old bigint[], mask bigint, ver bigint, n integer)
            RETURNS bigint[] LANGUAGE sql IMMUTABLE AS $g9$
            SELECT coalesce(array_agg(CASE WHEN (mask & (1::bigint << (i - 1))) <> 0 THEN ver ELSE coalesce(old[i], 0) END ORDER BY i), '{}'::bigint[])
            FROM generate_series(1, n) AS i
            $g9$;
        CREATE OR REPLACE FUNCTION {{schema}}.changed_mask(col_vers bigint[], base bigint, idx integer[])
            RETURNS bigint LANGUAGE sql IMMUTABLE AS $g9$
            SELECT CASE WHEN col_vers IS NULL THEN (-1)::bigint
                        ELSE coalesce(bit_or(CASE WHEN coalesce(col_vers[idx[i]], 0) > base THEN (1::bigint << (i - 1)) ELSE 0::bigint END), 0::bigint) END
            FROM generate_series(1, coalesce(array_length(idx, 1), 0)) AS i
            $g9$;
        INSERT INTO {{schema}}.install (id, epoch, schema_version, library_version)
            VALUES (1, gen_random_uuid(), {{SchemaVersion}}, '0.1.0') ON CONFLICT (id) DO NOTHING;
        """;

    public static string Capture(PgTable table, string schema, G9CPostgresSyncOptions options)
    {
        var model = table.Model;
        var tracking = table.TrackingName;
        var key = table.Key.Quoted;
        var bare = table.TrackingBare;
        var physical = model.PhysicalColumns.Count;
        var sequence = PgNames.Literal(schema + ".ver_seq");
        var notify = $"PERFORM pg_notify({PgNames.Literal(options.NotificationChannel)}, {PgNames.Literal(model.LogicalName)});";
        var fullVersions = model.TracksColumnVersions ? $"array_fill(s.ver, ARRAY[{physical}])" : "NULL::bigint[]";
        var mergeInsert = model.TracksColumnVersions ? $"{schema}.merge_col_vers(NULL, s.mask, s.ver, {physical})" : "NULL::bigint[]";
        var mergeConflict = model.TracksColumnVersions ? $"{schema}.merge_col_vers(k.col_vers, EXCLUDED.last_mask, EXCLUDED.ver, {physical})" : "NULL";
        var triggerPrefix = options.Schema;

        var mask = new StringBuilder();
        for (var i = 0; i < physical; i++)
        {
            var column = model.PhysicalColumns[i];
            var bound = table.Bound(column);
            if (i > 0) mask.Append(" | ");
            mask.Append("(CASE WHEN (").Append(CompareExpression(column, bound, "n")).Append(") IS DISTINCT FROM (")
                .Append(CompareExpression(column, bound, "o")).Append(") THEN ").Append(PgNames.Bits(i)).Append("::bigint ELSE 0::bigint END)");
        }

        if (physical == 0) mask.Append("0::bigint");

        return $$"""
            CREATE TABLE IF NOT EXISTS {{tracking}} (
                {{key}} {{table.Key.DbType}} PRIMARY KEY,
                xid xid8 NOT NULL,
                ver bigint NOT NULL,
                deleted boolean NOT NULL DEFAULT false,
                origin_client uuid NULL,
                changed_at timestamptz NOT NULL DEFAULT now(),
                last_mask bigint NOT NULL DEFAULT -1,
                col_vers bigint[] NULL);
            CREATE INDEX IF NOT EXISTS {{PgNames.Quote(bare + "_xid")}} ON {{tracking}} (xid);
            CREATE INDEX IF NOT EXISTS {{PgNames.Quote(bare + "_del")}} ON {{tracking}} (changed_at) WHERE deleted;

            CREATE OR REPLACE FUNCTION {{schema}}.{{PgNames.Quote(bare + "__ins")}}() RETURNS trigger LANGUAGE plpgsql AS $g9$
            DECLARE affected bigint;
            BEGIN
                INSERT INTO {{tracking}} AS k ({{key}}, xid, ver, deleted, origin_client, changed_at, last_mask, col_vers)
                SELECT s.{{key}}, pg_current_xact_id(), s.ver, false, {{Origin}}, now(), -1, {{fullVersions}}
                FROM (SELECT r.{{key}}, nextval({{sequence}}) AS ver FROM g9_new AS r) AS s
                ON CONFLICT ({{key}}) DO UPDATE SET xid = EXCLUDED.xid, ver = EXCLUDED.ver, deleted = false,
                    origin_client = EXCLUDED.origin_client, changed_at = EXCLUDED.changed_at, last_mask = -1, col_vers = EXCLUDED.col_vers;
                GET DIAGNOSTICS affected = ROW_COUNT;
                IF affected > 0 THEN {{notify}} END IF;
                RETURN NULL;
            END
            $g9$;

            CREATE OR REPLACE FUNCTION {{schema}}.{{PgNames.Quote(bare + "__upd")}}() RETURNS trigger LANGUAGE plpgsql AS $g9$
            DECLARE affected bigint;
            BEGIN
                INSERT INTO {{tracking}} AS k ({{key}}, xid, ver, deleted, origin_client, changed_at, last_mask, col_vers)
                SELECT s.{{key}}, pg_current_xact_id(), s.ver, false, {{Origin}}, now(), s.mask, {{mergeInsert}}
                FROM (SELECT n.{{key}}, m.mask, nextval({{sequence}}) AS ver
                      FROM g9_new AS n
                      JOIN g9_old AS o ON o.{{key}} = n.{{key}}
                      CROSS JOIN LATERAL (SELECT {{mask}} AS mask) AS m
                      WHERE m.mask <> 0) AS s
                ON CONFLICT ({{key}}) DO UPDATE SET xid = EXCLUDED.xid, ver = EXCLUDED.ver, deleted = false,
                    origin_client = EXCLUDED.origin_client, changed_at = EXCLUDED.changed_at, last_mask = EXCLUDED.last_mask,
                    col_vers = {{mergeConflict}};
                GET DIAGNOSTICS affected = ROW_COUNT;
                IF affected > 0 THEN {{notify}} END IF;
                RETURN NULL;
            END
            $g9$;

            CREATE OR REPLACE FUNCTION {{schema}}.{{PgNames.Quote(bare + "__del")}}() RETURNS trigger LANGUAGE plpgsql AS $g9$
            DECLARE affected bigint;
            BEGIN
                INSERT INTO {{tracking}} AS k ({{key}}, xid, ver, deleted, origin_client, changed_at, last_mask, col_vers)
                SELECT s.{{key}}, pg_current_xact_id(), s.ver, true, {{Origin}}, now(), -1, {{fullVersions}}
                FROM (SELECT r.{{key}}, nextval({{sequence}}) AS ver FROM g9_old AS r) AS s
                ON CONFLICT ({{key}}) DO UPDATE SET xid = EXCLUDED.xid, ver = EXCLUDED.ver, deleted = true,
                    origin_client = EXCLUDED.origin_client, changed_at = EXCLUDED.changed_at, last_mask = -1, col_vers = EXCLUDED.col_vers;
                GET DIAGNOSTICS affected = ROW_COUNT;
                IF affected > 0 THEN {{notify}} END IF;
                RETURN NULL;
            END
            $g9$;

            DROP TRIGGER IF EXISTS {{PgNames.Quote(triggerPrefix + "_ins")}} ON {{table.BaseName}};
            CREATE TRIGGER {{PgNames.Quote(triggerPrefix + "_ins")}} AFTER INSERT ON {{table.BaseName}}
                REFERENCING NEW TABLE AS g9_new FOR EACH STATEMENT EXECUTE FUNCTION {{schema}}.{{PgNames.Quote(bare + "__ins")}}();
            DROP TRIGGER IF EXISTS {{PgNames.Quote(triggerPrefix + "_upd")}} ON {{table.BaseName}};
            CREATE TRIGGER {{PgNames.Quote(triggerPrefix + "_upd")}} AFTER UPDATE ON {{table.BaseName}}
                REFERENCING OLD TABLE AS g9_old NEW TABLE AS g9_new FOR EACH STATEMENT EXECUTE FUNCTION {{schema}}.{{PgNames.Quote(bare + "__upd")}}();
            DROP TRIGGER IF EXISTS {{PgNames.Quote(triggerPrefix + "_del")}} ON {{table.BaseName}};
            CREATE TRIGGER {{PgNames.Quote(triggerPrefix + "_del")}} AFTER DELETE ON {{table.BaseName}}
                REFERENCING OLD TABLE AS g9_old FOR EACH STATEMENT EXECUTE FUNCTION {{schema}}.{{PgNames.Quote(bare + "__del")}}();

            """;
    }

    /// <summary>Registers the table and resets column versions when the captured column list changed (script mode).</summary>
    public static string Register(PgTable table, string schema, string captureHash)
    {
        var columns = "ARRAY[" + string.Join(", ", table.Model.PhysicalColumns.Select(c => PgNames.Literal(c.Name))) + "]::text[]";
        var schemaLiteral = PgNames.Literal(table.Model.Schema);
        var nameLiteral = PgNames.Literal(table.Model.Name);
        return $"""
            UPDATE {table.TrackingName} SET col_vers = NULL
                WHERE col_vers IS NOT NULL
                  AND (SELECT columns FROM {schema}.tables WHERE schema_name = {schemaLiteral} AND table_name = {nameLiteral}) IS DISTINCT FROM {columns};
            INSERT INTO {schema}.tables (schema_name, table_name, columns, capture_hash)
                VALUES ({schemaLiteral}, {nameLiteral}, {columns}, {PgNames.Literal(captureHash)})
                ON CONFLICT (schema_name, table_name) DO UPDATE SET columns = EXCLUDED.columns, capture_hash = EXCLUDED.capture_hash, updated_at = now();

            """;
    }

    private static string CompareExpression(G9CSyncServerColumn column, PgBoundColumn bound, string alias)
    {
        if (column.ReadSql is not null) return G9CSqlTemplate.Bind(column.ReadSql, alias, string.Empty, EmptyNames.Instance);
        var db = bound.Db!;
        var expression = alias + "." + db.Quoted;
        return PgTypes.NeedsTextComparison(db.TypeName) ? expression + "::text" : expression;
    }
}

internal static class EmptyNames
{
    public static readonly IReadOnlySet<string> Instance = new HashSet<string>(StringComparer.Ordinal);
}
