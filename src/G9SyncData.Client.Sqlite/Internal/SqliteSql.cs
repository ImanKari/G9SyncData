using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using G9SyncData.Client.Model;

namespace G9SyncData.Client.Sqlite.Internal;

internal static class SqliteSql
{
    public const int EphemeralSlot = 63;
    public const string NowMs = "CAST((julianday('now') - 2440587.5) * 86400000 AS INTEGER)";
    public const string Applying = "(SELECT applying FROM g9_ctx WHERE id = 1) = 0";
    public const string Seq = "(SELECT seq FROM g9_clock WHERE id = 1)";

    public const string Metadata = """
        CREATE TABLE IF NOT EXISTS g9_meta (key TEXT NOT NULL PRIMARY KEY, value TEXT NULL) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS g9_clock (id INTEGER NOT NULL PRIMARY KEY CHECK (id = 1), seq INTEGER NOT NULL);
        INSERT OR IGNORE INTO g9_clock (id, seq) VALUES (1, 0);
        CREATE TABLE IF NOT EXISTS g9_ctx (id INTEGER NOT NULL PRIMARY KEY CHECK (id = 1), applying INTEGER NOT NULL DEFAULT 0);
        INSERT OR IGNORE INTO g9_ctx (id, applying) VALUES (1, 0);
        UPDATE g9_ctx SET applying = 0 WHERE id = 1 AND applying <> 0;
        CREATE TABLE IF NOT EXISTS g9_tables (
            tid INTEGER PRIMARY KEY AUTOINCREMENT,
            local_name TEXT NOT NULL UNIQUE,
            server_name TEXT NOT NULL,
            model_hash TEXT NOT NULL,
            server_table_id INTEGER NULL,
            negotiated_json TEXT NULL,
            synced_columns TEXT NULL);
        CREATE TABLE IF NOT EXISTS g9_rows (
            tid INTEGER NOT NULL,
            pk TEXT NOT NULL,
            server_ver INTEGER NOT NULL DEFAULT 0,
            local_seq INTEGER NOT NULL DEFAULT 0,
            dirty INTEGER NOT NULL DEFAULT 0,
            dirty_mask INTEGER NOT NULL DEFAULT 0,
            hlc_ms INTEGER NULL,
            subs INTEGER NOT NULL DEFAULT 0,
            rejected INTEGER NOT NULL DEFAULT 0,
            deleted INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY (tid, pk)) WITHOUT ROWID;
        CREATE INDEX IF NOT EXISTS g9_rows_dirty ON g9_rows (local_seq) WHERE dirty <> 0;
        CREATE INDEX IF NOT EXISTS g9_rows_feed ON g9_rows (tid, local_seq);
        CREATE TABLE IF NOT EXISTS g9_subscriptions (
            key TEXT NOT NULL PRIMARY KEY,
            name TEXT NOT NULL,
            params TEXT NOT NULL,
            slot INTEGER NOT NULL,
            ephemeral INTEGER NOT NULL,
            ttl_seconds INTEGER NULL,
            cursor TEXT NULL,
            definition_hash TEXT NULL,
            auth_hash TEXT NULL,
            state INTEGER NOT NULL DEFAULT 0,
            error_code TEXT NULL,
            move_out_policy INTEGER NULL,
            created_utc INTEGER NOT NULL,
            last_success_utc INTEGER NULL,
            last_reconcile_utc INTEGER NULL,
            last_access_utc INTEGER NULL) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS g9_ephemeral_rows (sub_key TEXT NOT NULL, tid INTEGER NOT NULL, pk TEXT NOT NULL, PRIMARY KEY (sub_key, tid, pk)) WITHOUT ROWID;
        CREATE INDEX IF NOT EXISTS g9_ephemeral_rows_row ON g9_ephemeral_rows (tid, pk);
        CREATE TABLE IF NOT EXISTS g9_stage_runs (run_id TEXT NOT NULL PRIMARY KEY, plan_json TEXT NOT NULL, backfills_json TEXT NULL, created_utc INTEGER NOT NULL, complete INTEGER NOT NULL DEFAULT 0, generation TEXT NULL, stage_seq INTEGER NOT NULL DEFAULT 0) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS g9_stage (run_id TEXT NOT NULL, seq INTEGER NOT NULL, subscription_key TEXT NULL, row_count INTEGER NOT NULL, continuation_json TEXT NULL, payload BLOB NULL, PRIMARY KEY (run_id, seq)) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS g9_rejections (
            tid INTEGER NOT NULL, pk TEXT NOT NULL, code TEXT NOT NULL, message TEXT NULL, sql_state TEXT NULL, constraint_name TEXT NULL,
            column_name TEXT NULL, details TEXT NULL, at_utc INTEGER NOT NULL, PRIMARY KEY (tid, pk)) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS g9_runs (run_id TEXT NOT NULL PRIMARY KEY, started_utc INTEGER NOT NULL, report_json TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS g9_stats (key TEXT NOT NULL PRIMARY KEY, value REAL NOT NULL, samples INTEGER NOT NULL) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS g9_commands (
            command_id TEXT NOT NULL PRIMARY KEY,
            seq INTEGER NOT NULL,
            name TEXT NOT NULL,
            version INTEGER NOT NULL,
            payload TEXT NOT NULL,
            created_utc INTEGER NOT NULL,
            state INTEGER NOT NULL DEFAULT 0,
            batch_id INTEGER NULL,
            code TEXT NULL,
            message TEXT NULL,
            result TEXT NULL,
            completed_utc INTEGER NULL) WITHOUT ROWID;
        CREATE INDEX IF NOT EXISTS g9_commands_pending ON g9_commands (seq) WHERE state = 0;
        CREATE TABLE IF NOT EXISTS g9_command_rows (command_id TEXT NOT NULL, tid INTEGER NOT NULL, pk TEXT NOT NULL, PRIMARY KEY (command_id, tid, pk)) WITHOUT ROWID;
        CREATE INDEX IF NOT EXISTS g9_command_rows_row ON g9_command_rows (tid, pk);
        CREATE TABLE IF NOT EXISTS g9_quarantine (
            tid INTEGER NOT NULL, pk TEXT NOT NULL, reason INTEGER NOT NULL, rule TEXT NULL, row_json TEXT NOT NULL,
            had_pending INTEGER NOT NULL, dirty_mask INTEGER NOT NULL, at_utc INTEGER NOT NULL, PRIMARY KEY (tid, pk)) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS g9_revoked (
            tid INTEGER NOT NULL, pk TEXT NOT NULL, disposition INTEGER NOT NULL, reason INTEGER NOT NULL, rule TEXT NULL, at_utc INTEGER NOT NULL,
            PRIMARY KEY (tid, pk)) WITHOUT ROWID;
        """;

    /// <summary>Command mode (ADR-0025): local effects of a command being recorded.</summary>
    public const string CommandMode = "(SELECT applying FROM g9_ctx WHERE id = 1) = 2";

    public static string Quote(string identifier) => "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";

    public static long Bit(int slot) => slot >= 63 ? long.MinValue : 1L << slot;

    public static long AllColumnsMask(int count) => count >= 63 ? -1L : (1L << count) - 1;

    public static string Literal(long value) => value.ToString(CultureInfo.InvariantCulture);

    public static string ColumnType(G9ESyncColumnType type, G9CSqliteSyncStoreOptions options) => type switch
    {
        G9ESyncColumnType.Boolean or G9ESyncColumnType.Int32 or G9ESyncColumnType.Int64 or G9ESyncColumnType.TimeSpan => "INTEGER",
        G9ESyncColumnType.Double => "REAL",
        G9ESyncColumnType.Decimal => options.DecimalStorage == G9ESqliteDecimalStorage.Real ? "REAL" : "TEXT",
        G9ESyncColumnType.DateTime => options.DateTimeStorage == G9ESqliteDateTimeStorage.Ticks ? "INTEGER" : "TEXT",
        G9ESyncColumnType.Bytes => "BLOB",
        _ => "TEXT"
    };

    public static string DefaultLiteral(G9ESyncColumnType type, G9CSqliteSyncStoreOptions options) => ColumnType(type, options) switch
    {
        "INTEGER" => "0",
        "REAL" => "0",
        "BLOB" => "x''",
        _ => type switch
        {
            G9ESyncColumnType.Guid => "'00000000-0000-0000-0000-000000000000'",
            G9ESyncColumnType.DateTime => "'" + DateTime.MinValue.ToString(options.DateTimeFormat, CultureInfo.InvariantCulture) + "'",
            G9ESyncColumnType.Decimal => "'0'",
            _ => "''"
        }
    };

    public static string ColumnDefinition(G9CSyncClientColumn column, G9CSqliteSyncStoreOptions options) =>
        Quote(column.Name) + " " + ColumnType(column.Type, options) +
        (column.Nullable ? string.Empty : " NOT NULL DEFAULT " + DefaultLiteral(column.Type, options));

    public static string CreateTable(G9CSyncClientTable table, G9CSqliteSyncStoreOptions options)
    {
        var key = Quote(table.KeyColumn) + " " + (table.KeyType is G9ESyncColumnType.Int32 or G9ESyncColumnType.Int64 ? "INTEGER" : "TEXT") +
                  " NOT NULL" + (table.KeyType == G9ESyncColumnType.Guid ? " COLLATE NOCASE" : string.Empty) + " PRIMARY KEY";
        return "CREATE TABLE IF NOT EXISTS " + Quote(table.LocalName) + " (" + key +
               string.Concat(table.Columns.Select(c => ", " + ColumnDefinition(c, options))) + ")";
    }

    /// <summary>Canonical key text of a row alias (<c>NEW</c>, <c>OLD</c>, a table).</summary>
    public static string KeyExpression(G9CSyncClientTable table, string alias) =>
        table.KeyType == G9ESyncColumnType.Guid
            ? "lower(" + alias + "." + Quote(table.KeyColumn) + ")"
            : "CAST(" + alias + "." + Quote(table.KeyColumn) + " AS TEXT)";

    public static string TriggerName(G9CSyncClientTable table, string suffix) => Quote("g9_" + table.LocalName + "_" + suffix);

    public static string DropTriggers(G9CSyncClientTable table) =>
        $"DROP TRIGGER IF EXISTS {TriggerName(table, "ai")}; DROP TRIGGER IF EXISTS {TriggerName(table, "au")}; DROP TRIGGER IF EXISTS {TriggerName(table, "ad")}; " +
        $"DROP TRIGGER IF EXISTS {TriggerName(table, "ci")}; DROP TRIGGER IF EXISTS {TriggerName(table, "cu")}; DROP TRIGGER IF EXISTS {TriggerName(table, "cd")};";

    /// <summary>
    ///     Command-mode triggers (ADR-0025), on EVERY table regardless of direction: a command may change a table the device cannot
    ///     upload. They record which rows the command touched, and nothing else.
    /// </summary>
    public static string CreateCommandTriggers(G9CSyncClientTable table, long tid)
    {
        var name = Quote(table.LocalName);
        var tidLiteral = Literal(tid);
        const string command = "(SELECT command_id FROM g9_ctx WHERE id = 1)";
        return $"""
            CREATE TRIGGER {TriggerName(table, "ci")} AFTER INSERT ON {name} WHEN {CommandMode}
            BEGIN
                INSERT OR IGNORE INTO g9_command_rows (command_id, tid, pk) VALUES ({command}, {tidLiteral}, {KeyExpression(table, "NEW")});
            END;
            CREATE TRIGGER {TriggerName(table, "cu")} AFTER UPDATE ON {name} WHEN {CommandMode}
            BEGIN
                INSERT OR IGNORE INTO g9_command_rows (command_id, tid, pk) VALUES ({command}, {tidLiteral}, {KeyExpression(table, "NEW")});
                INSERT OR IGNORE INTO g9_command_rows (command_id, tid, pk) VALUES ({command}, {tidLiteral}, {KeyExpression(table, "OLD")});
            END;
            CREATE TRIGGER {TriggerName(table, "cd")} AFTER DELETE ON {name} WHEN {CommandMode}
            BEGIN
                INSERT OR IGNORE INTO g9_command_rows (command_id, tid, pk) VALUES ({command}, {tidLiteral}, {KeyExpression(table, "OLD")});
            END;
            """;
    }

    public static string CreateTriggers(G9CSyncClientTable table, long tid)
    {
        var name = Quote(table.LocalName);
        var tidLiteral = Literal(tid);
        var newKey = KeyExpression(table, "NEW");
        var oldKey = KeyExpression(table, "OLD");
        var all = Literal(AllColumnsMask(table.Columns.Count));
        var mask = table.Columns.Count == 0
            ? "0"
            : string.Join(" | ", table.Columns.Select((c, i) =>
                $"(CASE WHEN OLD.{Quote(c.Name)} IS NOT NEW.{Quote(c.Name)} THEN {Literal(1L << i)} ELSE 0 END)"));

        return $"""
            CREATE TRIGGER {TriggerName(table, "ai")} AFTER INSERT ON {name} WHEN {Applying}
            BEGIN
                UPDATE g9_clock SET seq = seq + 1 WHERE id = 1;
                INSERT OR IGNORE INTO g9_rows (tid, pk) VALUES ({tidLiteral}, {newKey});
                UPDATE g9_rows SET local_seq = {Seq}, dirty = 1, dirty_mask = {all}, hlc_ms = {NowMs}, rejected = 0, deleted = 0
                    WHERE tid = {tidLiteral} AND pk = {newKey};
            END;
            CREATE TRIGGER {TriggerName(table, "au")} AFTER UPDATE ON {name} WHEN {Applying} AND ({mask}) <> 0
            BEGIN
                UPDATE g9_clock SET seq = seq + 1 WHERE id = 1;
                INSERT OR IGNORE INTO g9_rows (tid, pk) VALUES ({tidLiteral}, {newKey});
                UPDATE g9_rows SET local_seq = {Seq}, dirty = CASE WHEN dirty = 2 THEN 2 ELSE 1 END, dirty_mask = dirty_mask | ({mask}),
                    hlc_ms = {NowMs}, rejected = 0, deleted = 0
                    WHERE tid = {tidLiteral} AND pk = {newKey};
            END;
            CREATE TRIGGER {TriggerName(table, "ad")} AFTER DELETE ON {name} WHEN {Applying}
            BEGIN
                UPDATE g9_clock SET seq = seq + 1 WHERE id = 1;
                UPDATE g9_rows SET local_seq = {Seq}, dirty = 2, dirty_mask = 0, hlc_ms = {NowMs}, rejected = 0
                    WHERE tid = {tidLiteral} AND pk = {oldKey} AND NOT (server_ver = 0 AND subs = 0 AND dirty = 1);
                DELETE FROM g9_rows WHERE tid = {tidLiteral} AND pk = {oldKey} AND server_ver = 0 AND subs = 0 AND dirty = 1;
            END;
            """;
    }

    public static string ModelHash(G9CSyncClientTable table)
    {
        var text = new StringBuilder("v1|").Append(table.ServerName).Append('|').Append(table.LocalName).Append('|').Append(table.KeyColumn).Append(':').Append(table.KeyType);
        foreach (var column in table.Columns) text.Append('|').Append(column.Name).Append(':').Append(column.Type).Append(':').Append(column.Nullable);
        foreach (var index in table.Indexes) text.Append("|ix:").Append(index.Name).Append(':').Append(index.Unique).Append(':').AppendJoin(',', index.Columns);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())).AsSpan(0, 16));
    }
}
