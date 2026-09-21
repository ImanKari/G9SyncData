using System.Reflection;
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

    /// <summary>
    ///     The version of THIS assembly, written to <c>install.library_version</c> on every initialisation: which build last set the schema
    ///     up is the first thing support asks. It was the literal '0.1.0' through every release up to 1.0.0, so the column said nothing.
    ///     Letters, digits, dots and dashes only by construction - the first other character (the '+commit' suffix) ends it - because
    ///     it is spliced into the script and must never be able to carry a quote.
    /// </summary>
    public static string LibraryVersion { get; } = ReadLibraryVersion();

    private static string ReadLibraryVersion()
    {
        var informational = typeof(PgCaptureSql).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var version = new string((informational ?? string.Empty).TakeWhile(c => char.IsAsciiDigit(c) || c is '.' or '-' || char.IsAsciiLetter(c)).ToArray());
        return version.Length is > 0 and <= 40 ? version : typeof(PgCaptureSql).Assembly.GetName().Version?.ToString(3) ?? "unknown";
    }
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
        CREATE TABLE IF NOT EXISTS {{schema}}.effects (
            effect_id uuid PRIMARY KEY,
            table_id integer NOT NULL,
            client_id uuid NOT NULL,
            batch_id bigint NOT NULL,
            user_id text NULL,
            upserted text[] NOT NULL DEFAULT '{}',
            deleted text[] NOT NULL DEFAULT '{}',
            attempts integer NOT NULL DEFAULT 0,
            next_attempt_at timestamptz NOT NULL DEFAULT now(),
            last_error text NULL,
            failed boolean NOT NULL DEFAULT false,
            created_at timestamptz NOT NULL DEFAULT now());
        CREATE INDEX IF NOT EXISTS {{PgNames.Quote("g9_effects_due")}} ON {{schema}}.effects (next_attempt_at) WHERE NOT failed;
        CREATE SEQUENCE IF NOT EXISTS {{schema}}.ver_seq;
        CREATE TABLE IF NOT EXISTS {{schema}}.wal_sync_points (
            nonce uuid PRIMARY KEY,
            seen_at timestamptz NOT NULL DEFAULT now());
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
        CREATE OR REPLACE FUNCTION {{schema}}.hash_mask(old bigint[], new bigint[])
            RETURNS bigint LANGUAGE sql IMMUTABLE AS $g9$
            SELECT CASE WHEN old IS NULL OR new IS NULL OR array_length(old, 1) IS DISTINCT FROM array_length(new, 1) THEN (-1)::bigint
                        ELSE coalesce(bit_or(CASE WHEN old[i] IS DISTINCT FROM new[i] THEN (1::bigint << (i - 1)) ELSE 0::bigint END), 0::bigint) END
            FROM generate_series(1, coalesce(array_length(new, 1), 0)) AS i
            $g9$;
        ALTER TABLE {{schema}}.install ADD COLUMN IF NOT EXISTS wal_slot text NULL;
        ALTER TABLE {{schema}}.install ADD COLUMN IF NOT EXISTS wal_rescan_pending boolean NOT NULL DEFAULT false;
        ALTER TABLE {{schema}}.tables ADD COLUMN IF NOT EXISTS wal_seeded boolean NOT NULL DEFAULT false;
        ALTER TABLE {{schema}}.install ADD COLUMN IF NOT EXISTS database_oid bigint NULL;
        ALTER TABLE {{schema}}.install ADD COLUMN IF NOT EXISTS epoch_changed_at timestamptz NULL;
        ALTER TABLE {{schema}}.install ADD COLUMN IF NOT EXISTS epoch_change_reason text NULL;
        CREATE UNLOGGED TABLE IF NOT EXISTS {{schema}}.key_sets (
            set_id bigserial PRIMARY KEY,
            table_id integer NOT NULL,
            identity text NOT NULL,
            build_snapshot pg_snapshot NOT NULL,
            row_count bigint NOT NULL DEFAULT 0,
            build_ms integer NOT NULL DEFAULT 0,
            created_at timestamptz NOT NULL DEFAULT now(),
            last_used_at timestamptz NOT NULL DEFAULT now(),
            UNIQUE (table_id, identity));
        CREATE INDEX IF NOT EXISTS key_sets_last_used ON {{schema}}.key_sets (last_used_at);
        CREATE TABLE IF NOT EXISTS {{schema}}.commands (
            client_id uuid NOT NULL,
            command_id uuid NOT NULL,
            sequence bigint NOT NULL,
            name text NOT NULL,
            version integer NOT NULL,
            status smallint NOT NULL,
            code text NULL,
            message text NULL,
            result text NULL,
            executed_at timestamptz NOT NULL DEFAULT now(),
            PRIMARY KEY (client_id, command_id));
        CREATE INDEX IF NOT EXISTS commands_executed_at ON {{schema}}.commands (executed_at);
        INSERT INTO {{schema}}.install (id, epoch, schema_version, library_version, database_oid)
            VALUES (1, gen_random_uuid(), {{SchemaVersion}}, '{{LibraryVersion}}', (SELECT oid::bigint FROM pg_database WHERE datname = current_database()))
            ON CONFLICT (id) DO NOTHING;
        UPDATE {{schema}}.install SET library_version = '{{LibraryVersion}}' WHERE id = 1 AND library_version IS DISTINCT FROM '{{LibraryVersion}}';
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
        var schemaLiteral = PgNames.Literal(model.Schema);
        var nameLiteral = PgNames.Literal(model.Name);

        // WAL mode (ADR-0028): the triggers only record G9SyncData's own pushes, which set g9sync.origin; every other
        // writer is recorded by the WAL consumer. The WHEN clause keeps the cost for those writers at ~1% (measured),
        // against ~100% for an unguarded tracking trigger.
        var walMode = options.CaptureMode == G9EPostgresCaptureMode.WriteAheadLog;
        var guard = walMode
            ? " WHEN (coalesce(current_setting('g9sync.origin', true), '') <> '')"
            : string.Empty;

        // WAL mode keeps per-column hashes of the tracked state: the consumer sees only new row images, so it derives the
        // changed-column mask by comparing hashes. Both writers use HashArray, so the two can never disagree.
        var insertHashes = walMode ? HashArray(table, "r") : "NULL::bigint[]";
        var updateHashes = walMode ? HashArray(table, "n") : "NULL::bigint[]";

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
            CREATE UNLOGGED TABLE IF NOT EXISTS {{table.KeySetName}} (
                set_id bigint NOT NULL,
                key {{table.KeyStagingType}} NOT NULL,
                PRIMARY KEY (set_id, key));
            CREATE TABLE IF NOT EXISTS {{tracking}} (
                {{key}} {{table.Key.DbType}} PRIMARY KEY,
                xid xid8 NOT NULL,
                ver bigint NOT NULL,
                deleted boolean NOT NULL DEFAULT false,
                origin_client uuid NULL,
                changed_at timestamptz NOT NULL DEFAULT now(),
                last_mask bigint NOT NULL DEFAULT -1,
                col_vers bigint[] NULL,
                col_hashes bigint[] NULL);
            ALTER TABLE {{tracking}} ADD COLUMN IF NOT EXISTS col_hashes bigint[] NULL;
            CREATE INDEX IF NOT EXISTS {{PgNames.Quote(bare + "_xid")}} ON {{tracking}} (xid);
            CREATE INDEX IF NOT EXISTS {{PgNames.Quote(bare + "_del")}} ON {{tracking}} (changed_at) WHERE deleted;

            CREATE OR REPLACE FUNCTION {{schema}}.{{PgNames.Quote(bare + "__ins")}}() RETURNS trigger LANGUAGE plpgsql AS $g9$
            DECLARE affected bigint;
            BEGIN
                INSERT INTO {{tracking}} AS k ({{key}}, xid, ver, deleted, origin_client, changed_at, last_mask, col_vers, col_hashes)
                SELECT s.{{key}}, pg_current_xact_id(), s.ver, false, {{Origin}}, now(), -1, {{fullVersions}}, s.hashes
                FROM (SELECT r.{{key}}, nextval({{sequence}}) AS ver, {{insertHashes}} AS hashes FROM g9_new AS r) AS s
                ON CONFLICT ({{key}}) DO UPDATE SET xid = EXCLUDED.xid, ver = EXCLUDED.ver, deleted = false,
                    origin_client = EXCLUDED.origin_client, changed_at = EXCLUDED.changed_at, last_mask = -1, col_vers = EXCLUDED.col_vers,
                    col_hashes = EXCLUDED.col_hashes;
                GET DIAGNOSTICS affected = ROW_COUNT;
                IF affected > 0 THEN {{notify}} END IF;
                RETURN NULL;
            END
            $g9$;

            CREATE OR REPLACE FUNCTION {{schema}}.{{PgNames.Quote(bare + "__upd")}}() RETURNS trigger LANGUAGE plpgsql AS $g9$
            DECLARE affected bigint;
            BEGIN
                INSERT INTO {{tracking}} AS k ({{key}}, xid, ver, deleted, origin_client, changed_at, last_mask, col_vers, col_hashes)
                SELECT s.{{key}}, pg_current_xact_id(), s.ver, false, {{Origin}}, now(), s.mask, {{mergeInsert}}, s.hashes
                FROM (SELECT n.{{key}}, m.mask, nextval({{sequence}}) AS ver, {{updateHashes}} AS hashes
                      FROM g9_new AS n
                      JOIN g9_old AS o ON o.{{key}} = n.{{key}}
                      CROSS JOIN LATERAL (SELECT {{mask}} AS mask) AS m
                      WHERE m.mask <> 0) AS s
                ON CONFLICT ({{key}}) DO UPDATE SET xid = EXCLUDED.xid, ver = EXCLUDED.ver, deleted = false,
                    origin_client = EXCLUDED.origin_client, changed_at = EXCLUDED.changed_at, last_mask = EXCLUDED.last_mask,
                    col_vers = {{mergeConflict}}, col_hashes = EXCLUDED.col_hashes;
                GET DIAGNOSTICS affected = ROW_COUNT;
                IF affected > 0 THEN {{notify}} END IF;
                RETURN NULL;
            END
            $g9$;

            CREATE OR REPLACE FUNCTION {{schema}}.{{PgNames.Quote(bare + "__del")}}() RETURNS trigger LANGUAGE plpgsql AS $g9$
            DECLARE affected bigint;
            BEGIN
                INSERT INTO {{tracking}} AS k ({{key}}, xid, ver, deleted, origin_client, changed_at, last_mask, col_vers, col_hashes)
                SELECT s.{{key}}, pg_current_xact_id(), s.ver, true, {{Origin}}, now(), -1, {{fullVersions}}, NULL
                FROM (SELECT r.{{key}}, nextval({{sequence}}) AS ver FROM g9_old AS r) AS s
                ON CONFLICT ({{key}}) DO UPDATE SET xid = EXCLUDED.xid, ver = EXCLUDED.ver, deleted = true,
                    origin_client = EXCLUDED.origin_client, changed_at = EXCLUDED.changed_at, last_mask = -1, col_vers = EXCLUDED.col_vers,
                    col_hashes = NULL;
                GET DIAGNOSTICS affected = ROW_COUNT;
                IF affected > 0 THEN {{notify}} END IF;
                RETURN NULL;
            END
            $g9$;

            CREATE OR REPLACE FUNCTION {{schema}}.{{PgNames.Quote(bare + "__trunc")}}() RETURNS trigger LANGUAGE plpgsql AS $g9$
            BEGIN
                -- TRUNCATE removes every row and fires no row trigger, and the WAL decoder is given a statement, not row images
                -- (ADR-0049). Recording it as this table's removal point puts every cursor older than it through the path a purged
                -- tombstone already takes - reconcile, or resync - and invalidates cached digests, without writing one tombstone per
                -- row. The tracking rows go with it: the keys they describe no longer exist, and a later delta must not offer them.
                TRUNCATE {{tracking}};
                UPDATE {{schema}}.tables SET max_removed_xid = pg_current_xact_id(), updated_at = now()
                    WHERE schema_name = {{schemaLiteral}} AND table_name = {{nameLiteral}};
                {{notify}}
                RETURN NULL;
            END
            $g9$;

            DROP TRIGGER IF EXISTS {{PgNames.Quote(triggerPrefix + "_trunc")}} ON {{table.BaseName}};
            CREATE TRIGGER {{PgNames.Quote(triggerPrefix + "_trunc")}} AFTER TRUNCATE ON {{table.BaseName}}
                FOR EACH STATEMENT EXECUTE FUNCTION {{schema}}.{{PgNames.Quote(bare + "__trunc")}}();
            DROP TRIGGER IF EXISTS {{PgNames.Quote(triggerPrefix + "_ins")}} ON {{table.BaseName}};
            CREATE TRIGGER {{PgNames.Quote(triggerPrefix + "_ins")}} AFTER INSERT ON {{table.BaseName}}
                REFERENCING NEW TABLE AS g9_new FOR EACH STATEMENT{{guard}} EXECUTE FUNCTION {{schema}}.{{PgNames.Quote(bare + "__ins")}}();
            DROP TRIGGER IF EXISTS {{PgNames.Quote(triggerPrefix + "_upd")}} ON {{table.BaseName}};
            CREATE TRIGGER {{PgNames.Quote(triggerPrefix + "_upd")}} AFTER UPDATE ON {{table.BaseName}}
                REFERENCING OLD TABLE AS g9_old NEW TABLE AS g9_new FOR EACH STATEMENT{{guard}} EXECUTE FUNCTION {{schema}}.{{PgNames.Quote(bare + "__upd")}}();
            DROP TRIGGER IF EXISTS {{PgNames.Quote(triggerPrefix + "_del")}} ON {{table.BaseName}};
            CREATE TRIGGER {{PgNames.Quote(triggerPrefix + "_del")}} AFTER DELETE ON {{table.BaseName}}
                REFERENCING OLD TABLE AS g9_old FOR EACH STATEMENT{{guard}} EXECUTE FUNCTION {{schema}}.{{PgNames.Quote(bare + "__del")}}();

            """;
    }

    /// <summary>The WAL consumer's write, and the rescan's: record the current state of the keys in <c>@g9_keys text[]</c>.</summary>
    public static string WalRecordKeys(PgTable table, string schema) =>
        WalRecordCurrentState(table, schema,
            $"SELECT DISTINCT u.key_text::{table.Key.DbType} AS key_value FROM unnest(@g9_keys::text[]) AS u(key_text)");

    /// <summary>The push pre-check (ADR-0028): record the current state of the keys the push will act on.</summary>
    public static string WalRecordStaged(PgTable table, string schema, string stage) =>
        WalRecordCurrentState(table, schema,
            $"SELECT DISTINCT s.g9_key::{table.Key.DbType} AS key_value FROM {stage} AS s WHERE s.g9_action = 1");

    /// <summary>
    ///     The one statement every WAL-mode writer uses (ADR-0028): for each key, record its CURRENT state - present with its
    ///     column hashes, or deleted - with the changed-column mask derived from the stored hashes, and skip keys whose synced
    ///     state did not change. Parameter: <c>@g9_changed_at</c>.
    ///     <list type="bullet">
    ///         <item>Keys are processed in sorted order, so concurrent writers (a push and the consumer) take tracking-row locks
    ///         in the same order and cannot deadlock.</item>
    ///         <item>A key present in neither the table nor tracking is skipped: it was inserted and deleted unseen (consumer),
    ///         or it is a brand-new row a device is about to insert (push) - neither is a change anyone must learn about.</item>
    ///         <item><c>origin_client</c> is NULL: this records writers that were not devices. A device's own writes are
    ///         recorded by the guarded triggers, with their origin.</item>
    ///     </list>
    /// </summary>
    private static string WalRecordCurrentState(PgTable table, string schema, string keysSql)
    {
        var model = table.Model;
        var key = table.Key.Quoted;
        var physical = model.PhysicalColumns.Count;
        var sequence = PgNames.Literal(schema + ".ver_seq");
        var conflictMask = $"CASE WHEN EXCLUDED.deleted OR k.deleted THEN (-1)::bigint ELSE {schema}.hash_mask(k.col_hashes, EXCLUDED.col_hashes) END";
        var insertVersions = model.TracksColumnVersions ? $"array_fill(s.ver, ARRAY[{physical}])" : "NULL::bigint[]";
        var conflictVersions = model.TracksColumnVersions ? $"{schema}.merge_col_vers(k.col_vers, {conflictMask}, EXCLUDED.ver, {physical})" : "NULL";

        return $"""
            INSERT INTO {table.TrackingName} AS k ({key}, xid, ver, deleted, origin_client, changed_at, last_mask, col_vers, col_hashes)
            SELECT s.key_value, pg_current_xact_id(), s.ver, NOT s.present, NULL, @g9_changed_at, -1, {insertVersions}, s.hashes
            FROM (SELECT c.key_value,
                         t.{key} IS NOT NULL AS present,
                         CASE WHEN t.{key} IS NULL THEN NULL ELSE {HashArray(table, "t")} END AS hashes,
                         nextval({sequence}) AS ver
                  FROM ({keysSql}) AS c
                  LEFT JOIN {table.BaseName} AS t ON t.{key} = c.key_value
                  WHERE t.{key} IS NOT NULL OR EXISTS (SELECT 1 FROM {table.TrackingName} AS e WHERE e.{key} = c.key_value)
                  ORDER BY c.key_value) AS s
            ON CONFLICT ({key}) DO UPDATE SET xid = EXCLUDED.xid, ver = EXCLUDED.ver, deleted = EXCLUDED.deleted,
                origin_client = NULL, changed_at = EXCLUDED.changed_at, last_mask = {conflictMask},
                col_vers = {conflictVersions}, col_hashes = EXCLUDED.col_hashes
            WHERE k.deleted IS DISTINCT FROM EXCLUDED.deleted
               OR (NOT EXCLUDED.deleted AND k.col_hashes IS DISTINCT FROM EXCLUDED.col_hashes)
            """;
    }

    /// <summary>
    ///     Seeding one key range (<c>@g9_after</c> exclusive, <c>@g9_upto</c> inclusive): column hashes for tracked rows that
    ///     lack them, and a baseline for rows never tracked. The baseline is version 0 - what devices already hold for such a
    ///     row - stamped with transaction id 3, which every snapshot sees as long committed, so it is never delivered as a
    ///     change. Nothing a device holds moves; the push pre-check simply gains the knowledge it needs to tell "unchanged"
    ///     from "changed but not yet consumed".
    /// </summary>
    public static string WalSeedRange(PgTable table, string schema)
    {
        var model = table.Model;
        var key = table.Key.Quoted;
        var zeroVersions = model.TracksColumnVersions ? $"array_fill(0::bigint, ARRAY[{model.PhysicalColumns.Count}])" : "NULL::bigint[]";
        var hashes = HashArray(table, "t");

        return $"""
            UPDATE {table.TrackingName} AS k SET col_hashes = {hashes}
            FROM {table.BaseName} AS t
            WHERE t.{key} = k.{key} AND NOT k.deleted AND k.col_hashes IS NULL
              AND (@g9_after::text IS NULL OR k.{key} > @g9_after) AND k.{key} <= @g9_upto;
            INSERT INTO {table.TrackingName} AS k ({key}, xid, ver, deleted, origin_client, changed_at, last_mask, col_vers, col_hashes)
            SELECT t.{key}, '3'::xid8, 0, false, NULL, now(), -1, {zeroVersions}, {hashes}
            FROM {table.BaseName} AS t
            WHERE (@g9_after::text IS NULL OR t.{key} > @g9_after) AND t.{key} <= @g9_upto
              AND NOT EXISTS (SELECT 1 FROM {table.TrackingName} AS e WHERE e.{key} = t.{key})
            ORDER BY t.{key}
            ON CONFLICT ({key}) DO NOTHING;
            """;
    }

    /// <summary>
    ///     <c>ARRAY[hash(col0), hash(col1), ...]</c> over the physical columns in capture order, where hash is the first 8
    ///     bytes of <c>md5</c> of the column's comparison expression as text (0 for NULL). Used by BOTH WAL-mode writers - the
    ///     guarded push triggers and the consumer - so their hashes are produced by the same SQL and cannot disagree.
    /// </summary>
    private static string HashArray(PgTable table, string alias)
    {
        var model = table.Model;
        if (model.PhysicalColumns.Count == 0) return "'{}'::bigint[]";

        var parts = new StringBuilder("ARRAY[");
        for (var i = 0; i < model.PhysicalColumns.Count; i++)
        {
            var column = model.PhysicalColumns[i];
            if (i > 0) parts.Append(", ");
            parts.Append("coalesce(('x' || left(md5((").Append(CompareExpression(column, table.Bound(column), alias))
                .Append(")::text), 16))::bit(64)::bigint, 0)");
        }

        return parts.Append("]::bigint[]").ToString();
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
