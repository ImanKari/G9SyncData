using System.Diagnostics;
using System.Globalization;
using System.Text;
using SQLitePCL;

namespace G9SyncData.Benchmarks;

/// <summary>
///     What makes a large first sync slow on the DEVICE, measured one factor at a time: the same Pot-shaped rows (12 columns, GUID key,
///     two secondary indexes, the store's capture triggers and membership table) loaded into SQLite in every candidate way — from a
///     faithful copy of today's apply (upsert per row, triggers present, membership upsert per row, random key order) to a server-built
///     snapshot file attached and copied natively. Run with <c>--sqlite-apply [--rows N]</c>; writes <c>results/sqlite-apply-latest.md</c>.
/// </summary>
internal static class SqliteApplyBenchmark
{
    private const long Tid = 2;

    private sealed record Variant(
        string Name,
        string Why,
        bool RandomOrder = false,
        bool Upsert = false,
        bool LiveTriggers = true,
        bool DeferIndexes = false,
        bool MembershipSetBased = false,
        int RowsPerStatement = 1,
        bool BlobKeys = false,
        bool WithoutRowid = false,
        bool SnapshotAttach = false,
        bool BigCache = false,
        bool TriggersOffForConnection = false);

    /// <summary>One variant's best run: load and checkpoint time, file size, and the process CPU time and managed allocations it took.</summary>
    internal sealed record VariantResult(string Name, string Why, TimeSpan Load, double RowsPerSecond, TimeSpan Checkpoint, long Bytes, TimeSpan Cpu, long AllocatedBytes);

    private sealed record Row(Guid Id, string Code, int Status, int RowNo, int Col, double Weight, double Latitude, double Longitude, string PlantedAt, string? Note,
        long Version);

    public static async Task<IReadOnlyList<VariantResult>> RunAsync(string resultsDirectory, int rows, IReadOnlyCollection<string>? only = null,
        Action<string>? log = null, string? platform = null)
    {
        log ??= Console.WriteLine;
        var results = new List<VariantResult>();
        var variants = new[]
        {
            new Variant("A. today: upsert, live triggers, per-row membership, random order", "the v1/v2 store's apply, before any optimisation",
                RandomOrder: true, Upsert: true),
            new Variant("B. A in key order", "pages arrive ordered by key (key sets page by key)", Upsert: true),
            new Variant("C. B with plain INSERT", "rows the device does not have yet need no upsert", LiveTriggers: true),
            new Variant("D. C with triggers dropped for the load", "a library write needs no capture; DDL is transactional", LiveTriggers: false),
            new Variant("E. D with secondary indexes built after the load", "one sorted index build instead of N random inserts",
                LiveTriggers: false, DeferIndexes: true),
            new Variant("F. E with membership rows set-based", "g9_rows filled by one INSERT…SELECT from staged versions",
                LiveTriggers: false, DeferIndexes: true, MembershipSetBased: true),
            new Variant("G. F with 64 rows per statement", "fewer sqlite3_step / reset / native transitions",
                LiveTriggers: false, DeferIndexes: true, MembershipSetBased: true, RowsPerStatement: 64),
            new Variant("H. G with a 64 MB page cache", "fewer page evictions during the load",
                LiveTriggers: false, DeferIndexes: true, MembershipSetBased: true, RowsPerStatement: 64, BigCache: true),
            new Variant("I. G with 16-byte BLOB keys", "what storing GUIDs as blobs would buy (app tables would have to change)",
                LiveTriggers: false, DeferIndexes: true, MembershipSetBased: true, RowsPerStatement: 64, BlobKeys: true),
            new Variant("J. G as a WITHOUT ROWID table", "one B-tree instead of table + key index",
                LiveTriggers: false, DeferIndexes: true, MembershipSetBased: true, RowsPerStatement: 64, WithoutRowid: true),
            new Variant("K. server-built snapshot file, attached and copied", "no per-row work in managed code at all",
                LiveTriggers: false, DeferIndexes: true, MembershipSetBased: true, SnapshotAttach: true),
            new Variant("L. A in random order with triggers dropped", "isolates the trigger cost on today's path",
                RandomOrder: true, Upsert: true, LiveTriggers: false),
            new Variant("M. B with triggers off for the connection only", "SQLITE_DBCONFIG_ENABLE_TRIGGER: no DDL, other connections keep triggers (ADR-0034)",
                Upsert: true, TriggersOffForConnection: true),
            new Variant("N. M with secondary indexes built after the load", "what the store now does for an empty table (ADR-0034)",
                Upsert: true, TriggersOffForConnection: true, DeferIndexes: true)
        }.Where(v => only is null || only.Count == 0 || only.Contains(v.Name[..1], StringComparer.OrdinalIgnoreCase)).ToArray();

        log($"SQLite apply benchmark: {rows:N0} rows, SQLite {raw.sqlite3_libversion().utf8_to_string()}");
        var data = Generate(rows);
        var sorted = data.OrderBy(r => r.Id.ToString("D"), StringComparer.Ordinal).ToArray();
        var random = data.OrderBy(_ => Random.Shared.Next()).ToArray();

        var directory = Path.Combine(Path.GetTempPath(), "g9sync-apply-bench-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);
        var report = new StringBuilder();
        report.AppendLine("# SQLite apply benchmark").AppendLine();
        report.AppendLine(CultureInfo.InvariantCulture, $"Rows: {rows:N0} · SQLite {raw.sqlite3_libversion().utf8_to_string()} · {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC · " +
                                                        $"{Environment.ProcessorCount} logical cores, {platform ?? System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
        report.AppendLine().AppendLine("WAL, synchronous=NORMAL, one transaction. \"Load\" = BEGIN…COMMIT; \"Checkpoint\" = the WAL copied into the database afterwards.")
            .AppendLine().AppendLine("| Variant | Load | Rows/s | Checkpoint | File | CPU | Allocated | Why |").AppendLine("|---|---|---|---|---|---|---|---|");

        string? snapshot = null;
        try
        {
            foreach (var variant in variants)
            {
                // Two runs, the faster one reported: the first run of a variant also pays cold file-system caches.
                (TimeSpan Load, TimeSpan Checkpoint, long Bytes, TimeSpan Cpu, long Allocated) best = (TimeSpan.MaxValue, TimeSpan.Zero, 0, TimeSpan.Zero, 0);
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    if (variant.SnapshotAttach) snapshot ??= BuildSnapshotFile(directory, sorted);
                    var path = Path.Combine(directory, $"v{Guid.NewGuid():N}.db");
                    var cpuBefore = Process.GetCurrentProcess().TotalProcessorTime;
                    var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
                    var result = Run(path, variant, variant.RandomOrder ? random : sorted, snapshot);
                    var cpu = Process.GetCurrentProcess().TotalProcessorTime - cpuBefore;
                    var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
                    if (result.Load < best.Load) best = (result.Load, result.Checkpoint, result.Bytes, cpu, allocated);
                    DeleteDatabase(path);
                }

                var line = $"| {variant.Name} | {best.Load.TotalMilliseconds:N0} ms | {rows / best.Load.TotalSeconds:N0} | {best.Checkpoint.TotalMilliseconds:N0} ms | " +
                           $"{best.Bytes / 1024d / 1024:N1} MB | {best.Cpu.TotalMilliseconds:N0} ms | {best.Allocated / 1024d / 1024:N1} MB | {variant.Why} |";
                log(line);
                report.AppendLine(line);
                results.Add(new VariantResult(variant.Name, variant.Why, best.Load, rows / best.Load.TotalSeconds, best.Checkpoint, best.Bytes, best.Cpu, best.Allocated));
            }
        }
        finally
        {
            if (snapshot is not null) DeleteDatabase(snapshot);
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }

        Directory.CreateDirectory(resultsDirectory);
        await File.WriteAllTextAsync(Path.Combine(resultsDirectory, "sqlite-apply-latest.md"), report.ToString());
        return results;
    }

    private static Row[] Generate(int rows) =>
        Enumerable.Range(1, rows).Select(g => new Row(
            Guid.NewGuid(), "P" + g.ToString("0000000", CultureInfo.InvariantCulture), g % 7, g / 400, g % 400, (g % 1000) / 10.0,
            32 + (g % 10000) / 100000.0, 51 + (g % 10000) / 100000.0,
            new DateTime(2024, 1, 1).AddMinutes(g % 500000).ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
            g % 5 == 0 ? "note " + g.ToString(CultureInfo.InvariantCulture) : null, g)).ToArray();

    private static (TimeSpan Load, TimeSpan Checkpoint, long Bytes) Run(string path, Variant variant, Row[] rows, string? snapshot)
    {
        var db = Open(path);
        try
        {
            Exec(db, "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA foreign_keys = OFF;");
            if (variant.BigCache) Exec(db, "PRAGMA cache_size = -65536; PRAGMA temp_store = MEMORY;");
            var keyType = variant.BlobKeys ? "BLOB" : "TEXT";
            Exec(db, $"""
                CREATE TABLE Pot (Id {keyType} NOT NULL PRIMARY KEY, SiteId TEXT NOT NULL, Code TEXT NOT NULL, Status INTEGER NOT NULL, "Row" INTEGER NOT NULL,
                    Col INTEGER NOT NULL, Weight REAL, Latitude REAL, Longitude REAL, PlantedAt TEXT, Note TEXT, IsDeleted INTEGER NOT NULL){(variant.WithoutRowid ? " WITHOUT ROWID" : "")};
                CREATE TABLE g9_ctx (id INTEGER NOT NULL PRIMARY KEY, applying INTEGER NOT NULL DEFAULT 0);
                INSERT INTO g9_ctx VALUES (1, 0);
                CREATE TABLE g9_clock (id INTEGER NOT NULL PRIMARY KEY, seq INTEGER NOT NULL);
                INSERT INTO g9_clock VALUES (1, 0);
                CREATE TABLE g9_rows (tid INTEGER NOT NULL, pk {keyType} NOT NULL, server_ver INTEGER NOT NULL DEFAULT 0, local_seq INTEGER NOT NULL DEFAULT 0,
                    dirty INTEGER NOT NULL DEFAULT 0, dirty_mask INTEGER NOT NULL DEFAULT 0, hlc_ms INTEGER NULL, subs INTEGER NOT NULL DEFAULT 0,
                    rejected INTEGER NOT NULL DEFAULT 0, deleted INTEGER NOT NULL DEFAULT 0, PRIMARY KEY (tid, pk)) WITHOUT ROWID;
                CREATE INDEX g9_rows_dirty ON g9_rows (local_seq) WHERE dirty <> 0;
                CREATE INDEX g9_rows_feed ON g9_rows (tid, local_seq);
                """);
            if (!variant.DeferIndexes) Exec(db, Indexes);
            Exec(db, Triggers);

            var watch = Stopwatch.StartNew();
            if (variant.TriggersOffForConnection && (raw.sqlite3_db_config(db, raw.SQLITE_DBCONFIG_ENABLE_TRIGGER, 0, out var on) != raw.SQLITE_OK || on != 0))
            {
                throw new InvalidOperationException("SQLite did not switch triggers off.");
            }

            Exec(db, "BEGIN IMMEDIATE; UPDATE g9_ctx SET applying = 1 WHERE id = 1;");
            if (!variant.LiveTriggers) Exec(db, "DROP TRIGGER g9_pot_ai; DROP TRIGGER g9_pot_au; DROP TRIGGER g9_pot_ad;");

            if (variant.SnapshotAttach)
            {
                Exec(db, $"ATTACH DATABASE '{snapshot!.Replace("'", "''", StringComparison.Ordinal)}' AS snap");
                Exec(db, """
                    INSERT INTO main.Pot (Id, SiteId, Code, Status, "Row", Col, Weight, Latitude, Longitude, PlantedAt, Note, IsDeleted)
                        SELECT Id, SiteId, Code, Status, "Row", Col, Weight, Latitude, Longitude, PlantedAt, Note, IsDeleted FROM snap.Pot;
                    INSERT INTO g9_rows (tid, pk, server_ver, local_seq, subs) SELECT 2, Id, _ver, rowid, 1 FROM snap.Pot;
                    """);
            }
            else
            {
                LoadRows(db, variant, rows);
            }

            if (variant.DeferIndexes) Exec(db, Indexes);
            if (!variant.LiveTriggers) Exec(db, Triggers);
            Exec(db, "UPDATE g9_ctx SET applying = 0 WHERE id = 1; COMMIT;");
            if (variant.SnapshotAttach) Exec(db, "DETACH DATABASE snap");
            var load = watch.Elapsed;

            watch.Restart();
            Exec(db, "PRAGMA wal_checkpoint(TRUNCATE)");
            var checkpoint = watch.Elapsed;
            return (load, checkpoint, new FileInfo(path).Length);
        }
        finally
        {
            raw.sqlite3_close_v2(db);
            db.Dispose();
        }
    }

    private const string Indexes = """CREATE INDEX IX_Pot_SiteId ON Pot (SiteId); CREATE INDEX IX_Pot_Row_Col ON Pot ("Row", Col);""";

    private const string Triggers = """
        CREATE TRIGGER g9_pot_ai AFTER INSERT ON Pot WHEN (SELECT applying FROM g9_ctx WHERE id = 1) = 0
        BEGIN
            UPDATE g9_clock SET seq = seq + 1 WHERE id = 1;
            INSERT OR IGNORE INTO g9_rows (tid, pk) VALUES (2, NEW.Id);
            UPDATE g9_rows SET local_seq = (SELECT seq FROM g9_clock WHERE id = 1), dirty = 1, dirty_mask = 4095 WHERE tid = 2 AND pk = NEW.Id;
        END;
        CREATE TRIGGER g9_pot_au AFTER UPDATE ON Pot WHEN (SELECT applying FROM g9_ctx WHERE id = 1) = 0 AND (OLD.Code IS NOT NEW.Code OR OLD.Status IS NOT NEW.Status
            OR OLD.Note IS NOT NEW.Note OR OLD.Weight IS NOT NEW.Weight)
        BEGIN
            UPDATE g9_clock SET seq = seq + 1 WHERE id = 1;
            UPDATE g9_rows SET local_seq = (SELECT seq FROM g9_clock WHERE id = 1), dirty = 1 WHERE tid = 2 AND pk = NEW.Id;
        END;
        CREATE TRIGGER g9_pot_ad AFTER DELETE ON Pot WHEN (SELECT applying FROM g9_ctx WHERE id = 1) = 0
        BEGIN
            UPDATE g9_rows SET dirty = 2 WHERE tid = 2 AND pk = OLD.Id;
        END;
        """;

    private static void LoadRows(sqlite3 db, Variant variant, Row[] rows)
    {
        const int Columns = 12;
        var site = Guid.NewGuid().ToString("D");
        var per = variant.RowsPerStatement;
        var values = "(" + string.Join(", ", Enumerable.Repeat("?", Columns)) + ")";
        string Sql(int count) =>
            "INSERT INTO Pot (Id, SiteId, Code, Status, \"Row\", Col, Weight, Latitude, Longitude, PlantedAt, Note, IsDeleted) VALUES " +
            string.Join(", ", Enumerable.Repeat(values, count)) +
            (variant.Upsert
                ? " ON CONFLICT(Id) DO UPDATE SET SiteId = excluded.SiteId, Code = excluded.Code, Status = excluded.Status, \"Row\" = excluded.\"Row\", Col = excluded.Col, " +
                  "Weight = excluded.Weight, Latitude = excluded.Latitude, Longitude = excluded.Longitude, PlantedAt = excluded.PlantedAt, Note = excluded.Note, IsDeleted = excluded.IsDeleted"
                : string.Empty);

        Prepare(db, Sql(per), out var full);
        Prepare(db, Sql(1), out var single);
        Prepare(db, variant.MembershipSetBased
            ? "INSERT INTO temp.g9_versions (pk, ver) VALUES (?, ?)"
            : "INSERT INTO g9_rows (tid, pk, server_ver, local_seq, subs, dirty, dirty_mask, rejected, deleted) VALUES (2, ?, ?, ?, 1, 0, 0, 0, 0) " +
              "ON CONFLICT(tid, pk) DO UPDATE SET server_ver = excluded.server_ver, local_seq = excluded.local_seq, subs = subs | excluded.subs, dirty = 0, dirty_mask = 0, rejected = 0, deleted = 0",
            out var membership, createTemp: variant.MembershipSetBased ? $"CREATE TEMP TABLE g9_versions (pk {(variant.BlobKeys ? "BLOB" : "TEXT")} NOT NULL, ver INTEGER NOT NULL)" : null);
        try
        {
            var clock = 0L;
            for (var offset = 0; offset < rows.Length; offset += per)
            {
                var count = Math.Min(per, rows.Length - offset);
                var statement = count == per ? full : single;
                if (count != per)
                {
                    // The tail: row by row through the single-row statement.
                    for (var i = 0; i < count; i++)
                    {
                        BindRow(single, 0, rows[offset + i], site, variant.BlobKeys);
                        Step(db, single);
                    }
                }
                else
                {
                    for (var i = 0; i < count; i++) BindRow(statement, i * Columns, rows[offset + i], site, variant.BlobKeys);
                    Step(db, statement);
                }

                for (var i = 0; i < count; i++)
                {
                    var row = rows[offset + i];
                    BindKey(membership, 1, row.Id, variant.BlobKeys);
                    raw.sqlite3_bind_int64(membership, 2, row.Version);
                    if (!variant.MembershipSetBased) raw.sqlite3_bind_int64(membership, 3, ++clock);
                    Step(db, membership);
                }
            }

            if (variant.MembershipSetBased)
            {
                Exec(db, "INSERT INTO g9_rows (tid, pk, server_ver, local_seq, subs) SELECT 2, pk, ver, rowid, 1 FROM temp.g9_versions ORDER BY pk; DROP TABLE temp.g9_versions;");
            }
        }
        finally
        {
            raw.sqlite3_finalize(full);
            raw.sqlite3_finalize(single);
            raw.sqlite3_finalize(membership);
        }
    }

    private static void BindRow(sqlite3_stmt statement, int offset, Row row, string site, bool blobKey)
    {
        BindKey(statement, offset + 1, row.Id, blobKey);
        raw.sqlite3_bind_text(statement, offset + 2, site);
        raw.sqlite3_bind_text(statement, offset + 3, row.Code);
        raw.sqlite3_bind_int64(statement, offset + 4, row.Status);
        raw.sqlite3_bind_int64(statement, offset + 5, row.RowNo);
        raw.sqlite3_bind_int64(statement, offset + 6, row.Col);
        raw.sqlite3_bind_double(statement, offset + 7, row.Weight);
        raw.sqlite3_bind_double(statement, offset + 8, row.Latitude);
        raw.sqlite3_bind_double(statement, offset + 9, row.Longitude);
        raw.sqlite3_bind_text(statement, offset + 10, row.PlantedAt);
        if (row.Note is null) raw.sqlite3_bind_null(statement, offset + 11);
        else raw.sqlite3_bind_text(statement, offset + 11, row.Note);
        raw.sqlite3_bind_int64(statement, offset + 12, 0);
    }

    private static void BindKey(sqlite3_stmt statement, int index, Guid id, bool blob)
    {
        if (!blob)
        {
            raw.sqlite3_bind_text(statement, index, id.ToString("D"));
            return;
        }

        Span<byte> bytes = stackalloc byte[16];
        id.TryWriteBytes(bytes, bigEndian: true, out _);   // big-endian: byte order = text order = server order
        raw.sqlite3_bind_blob(statement, index, bytes);
    }

    private static string BuildSnapshotFile(string directory, Row[] sorted)
    {
        var path = Path.Combine(directory, "snapshot.db");
        var db = Open(path);
        try
        {
            Exec(db, """
                PRAGMA journal_mode = OFF; PRAGMA synchronous = OFF;
                CREATE TABLE Pot (Id TEXT NOT NULL, SiteId TEXT NOT NULL, Code TEXT NOT NULL, Status INTEGER NOT NULL, "Row" INTEGER NOT NULL, Col INTEGER NOT NULL,
                    Weight REAL, Latitude REAL, Longitude REAL, PlantedAt TEXT, Note TEXT, IsDeleted INTEGER NOT NULL, _ver INTEGER NOT NULL);
                BEGIN;
                """);
            Prepare(db, "INSERT INTO Pot VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)", out var insert);
            var site = Guid.NewGuid().ToString("D");
            foreach (var row in sorted)
            {
                BindRow(insert, 0, row, site, blobKey: false);
                raw.sqlite3_bind_int64(insert, 13, row.Version);
                Step(db, insert);
            }

            raw.sqlite3_finalize(insert);
            Exec(db, "COMMIT");
        }
        finally
        {
            raw.sqlite3_close_v2(db);
            db.Dispose();
        }

        return path;
    }

    private static sqlite3 Open(string path)
    {
        if (raw.sqlite3_open_v2(path, out var db, raw.SQLITE_OPEN_READWRITE | raw.SQLITE_OPEN_CREATE, null) != raw.SQLITE_OK)
        {
            throw new InvalidOperationException("Cannot open " + path);
        }

        return db;
    }

    private static void Exec(sqlite3 db, string sql)
    {
        if (raw.sqlite3_exec(db, sql, null, null, out var error) != raw.SQLITE_OK) throw new InvalidOperationException(error + " in: " + sql[..Math.Min(sql.Length, 200)]);
    }

    private static void Prepare(sqlite3 db, string sql, out sqlite3_stmt statement, string? createTemp = null)
    {
        if (createTemp is not null) Exec(db, createTemp);
        if (raw.sqlite3_prepare_v2(db, sql, out statement) != raw.SQLITE_OK) throw new InvalidOperationException(raw.sqlite3_errmsg(db).utf8_to_string());
    }

    private static void Step(sqlite3 db, sqlite3_stmt statement)
    {
        var rc = raw.sqlite3_step(statement);
        raw.sqlite3_reset(statement);
        if (rc != raw.SQLITE_DONE) throw new InvalidOperationException(raw.sqlite3_errmsg(db).utf8_to_string());
    }

    private static void DeleteDatabase(string path)
    {
        foreach (var file in new[] { path, path + "-wal", path + "-shm" })
        {
            try { File.Delete(file); } catch (IOException) { }
        }
    }
}
