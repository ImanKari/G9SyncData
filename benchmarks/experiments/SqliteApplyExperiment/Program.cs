// SQLite bulk-apply experiment for the G9SyncData audit (read-only audit: this lives in the scratch directory only).
// Mirrors the store's real shapes: Pot-like app table (12 columns, TEXT GUID key, 2 secondary indexes), g9_rows (WITHOUT ROWID, PK (tid, pk),
// 2 indexes), g9_clock, g9_ctx, g9_stage, the 6 library triggers; WAL + synchronous=NORMAL, journal_size_limit 64 MiB, prepared statements, span binds.
using System.Diagnostics;
using System.Globalization;
using System.Text;
using SQLitePCL;

Batteries_V2.Init();
var argList = args.ToList();
int ArgInt(string name, int fallback) { var i = argList.IndexOf(name); return i >= 0 ? int.Parse(argList[i + 1], CultureInfo.InvariantCulture) : fallback; }
string ArgStr(string name, string fallback) { var i = argList.IndexOf(name); return i >= 0 ? argList[i + 1] : fallback; }
var baseRows = ArgInt("--rows", 500_000);
var runs = ArgInt("--runs", 2);
var experiments = ArgStr("--exp", "1,2,3,4,5,7").Split(',').ToHashSet();
var workDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "work");
workDir = Path.GetFullPath(ArgStr("--work", workDir));
Directory.CreateDirectory(workDir);
var resultsPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "results.md"));
var report = new StringBuilder();
void Say(string line) { Console.WriteLine(line); report.AppendLine(line); File.AppendAllText(resultsPath, line + Environment.NewLine); }

Say("");
Say($"## Run {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC - SQLite {raw.sqlite3_libversion().utf8_to_string()}, {baseRows:N0} base rows, best of {runs}, {Environment.ProcessorCount} cores, {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");

var siteA = Encoding.UTF8.GetBytes(new Guid("11111111-1111-4111-8111-111111111111").ToString("D"));
var siteB = Encoding.UTF8.GetBytes(new Guid("99999999-9999-4999-8999-999999999999").ToString("D"));
var farmA = Bench.Generate(baseRows, 1, siteA, 1);
Say($"generated {farmA.Length:N0} rows (sorted by key)");

if (experiments.Contains("1")) Experiments.CostSplit(workDir, farmA, runs, Say);
if (experiments.Contains("3")) Experiments.Slices(workDir, farmA, runs, Say);
if (experiments.Contains("4")) Experiments.PageSize(workDir, farmA, siteB, runs, Say);
if (experiments.Contains("5")) Experiments.Staging(workDir, runs, Say);
if (experiments.Contains("2")) Experiments.NonEmpty(workDir, farmA, siteB, runs, Say);
if (experiments.Contains("7")) Experiments.Readers(workDir, farmA, siteB, runs, Say);
if (experiments.Contains("8")) Experiments.Reapply(workDir, farmA, runs, Say);
Say("done");

internal enum Meta { PerRow, None, SetBasedAfter, IntSurrogate }

internal sealed record Opt(
    string Name,
    bool TriggersOff = true,
    bool DeferIndexes = false,
    bool DeferMetaIndexes = false,
    Meta Meta = Meta.PerRow,
    bool DataRows = true,
    int RowsPerCommit = 0,
    string Sync = "NORMAL",
    int AutoCheckpoint = 1000,
    int CacheKb = 0,
    long Mmap = 0,
    bool TempStoreMemory = false,
    bool KeyNoCase = false,
    bool MetaRowid = false,
    int PageSize = 4096,
    bool TempStaging = false,
    bool GuardedUpsert = false,
    string FinalCheckpoint = "TRUNCATE");

internal sealed class Row
{
    public byte[] Key = [];
    public byte[] Site = [];
    public byte[] Code = [];
    public int Status, RowNo, Col;
    public double Weight, Lat, Lon;
    public byte[] PlantedAt = [];
    public byte[]? Note;
    public long Version;
}

internal sealed record Result(double LoadMs, double CheckpointMs, long WalPeak, long FileBytes, string Extra = "");

internal static class Bench
{
    public const string Cols = "Id, SiteId, Code, Status, \"Row\", Col, Weight, Latitude, Longitude, PlantedAt, Note, IsDeleted";

    public static Row[] Generate(int rows, int seed, byte[] site, int firstOrdinal)
    {
        var random = new Random(seed);
        var result = new Row[rows];
        Span<byte> guid = stackalloc byte[16];
        for (var i = 0; i < rows; i++)
        {
            var g = firstOrdinal + i;
            random.NextBytes(guid);
            var id = new Guid(guid);
            result[i] = new Row
            {
                Key = Encoding.UTF8.GetBytes(id.ToString("D")),
                Site = site,
                Code = Encoding.UTF8.GetBytes("P" + g.ToString("0000000", CultureInfo.InvariantCulture)),
                Status = g % 7, RowNo = g / 400, Col = g % 400, Weight = g % 1000 / 10.0,
                Lat = 32 + g % 10000 / 100000.0, Lon = 51 + g % 10000 / 100000.0,
                PlantedAt = Encoding.UTF8.GetBytes(new DateTime(2024, 1, 1).AddMinutes(g % 500000).ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture)),
                Note = g % 5 == 0 ? Encoding.UTF8.GetBytes("note " + g.ToString(CultureInfo.InvariantCulture)) : null,
                Version = g
            };
        }

        Array.Sort(result, (a, b) => a.Key.AsSpan().SequenceCompareTo(b.Key));
        return result;
    }

    public static Row[] Updates(Row[] source, int every)
    {
        var list = new List<Row>();
        for (var i = 0; i < source.Length; i += every)
        {
            var s = source[i];
            list.Add(new Row
            {
                Key = s.Key, Site = s.Site, Code = s.Code, Status = (s.Status + 1) % 7, RowNo = s.RowNo, Col = s.Col, Weight = s.Weight + 1,
                Lat = s.Lat, Lon = s.Lon, PlantedAt = s.PlantedAt, Note = Encoding.UTF8.GetBytes("edited"), Version = s.Version + 10_000_000
            });
        }

        return list.ToArray();
    }

    public static sqlite3 Open(string path, Opt o, bool create)
    {
        if (raw.sqlite3_open_v2(path, out var db, raw.SQLITE_OPEN_READWRITE | raw.SQLITE_OPEN_CREATE, null) != raw.SQLITE_OK) throw new InvalidOperationException("open " + path);
        if (create && o.PageSize != 4096) Exec(db, "PRAGMA page_size = " + o.PageSize);
        Exec(db, "PRAGMA journal_mode = WAL; PRAGMA foreign_keys = OFF; PRAGMA busy_timeout = 30000; PRAGMA journal_size_limit = 67108864;");
        Exec(db, "PRAGMA synchronous = " + o.Sync);
        Exec(db, "PRAGMA wal_autocheckpoint = " + o.AutoCheckpoint);
        if (o.CacheKb > 0) Exec(db, "PRAGMA cache_size = -" + o.CacheKb);
        if (o.Mmap > 0) Exec(db, "PRAGMA mmap_size = " + o.Mmap);
        if (o.TempStoreMemory) Exec(db, "PRAGMA temp_store = MEMORY");
        return db;
    }

    public static void Close(sqlite3 db)
    {
        raw.sqlite3_close_v2(db);
        db.Dispose();
    }

    public const string Indexes = "CREATE INDEX IX_Pot_SiteId ON Pot (SiteId); CREATE INDEX IX_Pot_Row_Col ON Pot (\"Row\", Col);";
    public const string DropIndexes = "DROP INDEX IX_Pot_SiteId; DROP INDEX IX_Pot_Row_Col;";
    public const string MetaIndexes = "CREATE INDEX g9_rows_dirty ON g9_rows (local_seq) WHERE dirty <> 0; CREATE INDEX g9_rows_feed ON g9_rows (tid, local_seq);";

    public static void CreateSchema(sqlite3 db, Opt o)
    {
        var nocase = o.KeyNoCase ? " COLLATE NOCASE" : "";
        Exec(db, $"""
            CREATE TABLE Pot (Id TEXT NOT NULL{nocase} PRIMARY KEY, SiteId TEXT NOT NULL{nocase}, Code TEXT NOT NULL, Status INTEGER NOT NULL, "Row" INTEGER NOT NULL,
                Col INTEGER NOT NULL, Weight REAL, Latitude REAL, Longitude REAL, PlantedAt TEXT, Note TEXT, IsDeleted INTEGER NOT NULL);
            CREATE TABLE g9_ctx (id INTEGER NOT NULL PRIMARY KEY CHECK (id = 1), applying INTEGER NOT NULL DEFAULT 0, command_id TEXT NULL);
            INSERT INTO g9_ctx (id, applying) VALUES (1, 0);
            CREATE TABLE g9_clock (id INTEGER NOT NULL PRIMARY KEY CHECK (id = 1), seq INTEGER NOT NULL);
            INSERT INTO g9_clock VALUES (1, 0);
            CREATE TABLE g9_command_rows (command_id TEXT NOT NULL, tid INTEGER NOT NULL, pk TEXT NOT NULL, PRIMARY KEY (command_id, tid, pk)) WITHOUT ROWID;
            CREATE TABLE g9_stage (run_id TEXT NOT NULL, seq INTEGER NOT NULL, subscription_key TEXT NULL, row_count INTEGER NOT NULL, continuation_json TEXT NULL, payload BLOB NULL, PRIMARY KEY (run_id, seq)) WITHOUT ROWID;
            """);
        if (o.Meta == Meta.IntSurrogate)
        {
            Exec(db, """
                CREATE TABLE g9_rows (rid INTEGER PRIMARY KEY, tid INTEGER NOT NULL, server_ver INTEGER NOT NULL DEFAULT 0, local_seq INTEGER NOT NULL DEFAULT 0,
                    dirty INTEGER NOT NULL DEFAULT 0, dirty_mask INTEGER NOT NULL DEFAULT 0, hlc_ms INTEGER NULL, subs INTEGER NOT NULL DEFAULT 0,
                    rejected INTEGER NOT NULL DEFAULT 0, deleted INTEGER NOT NULL DEFAULT 0);
                """);
        }
        else
        {
            Exec(db, $"""
                CREATE TABLE g9_rows (tid INTEGER NOT NULL, pk TEXT NOT NULL, server_ver INTEGER NOT NULL DEFAULT 0, local_seq INTEGER NOT NULL DEFAULT 0,
                    dirty INTEGER NOT NULL DEFAULT 0, dirty_mask INTEGER NOT NULL DEFAULT 0, hlc_ms INTEGER NULL, subs INTEGER NOT NULL DEFAULT 0,
                    rejected INTEGER NOT NULL DEFAULT 0, deleted INTEGER NOT NULL DEFAULT 0, PRIMARY KEY (tid, pk)){(o.MetaRowid ? "" : " WITHOUT ROWID")};
                """);
        }

        Exec(db, MetaIndexes);
        Exec(db, Indexes);
        Exec(db, Triggers);
    }

    private const string Applying = "(SELECT applying FROM g9_ctx WHERE id = 1) = 0";
    private const string CommandMode = "(SELECT applying FROM g9_ctx WHERE id = 1) = 2";
    private const string Seq = "(SELECT seq FROM g9_clock WHERE id = 1)";
    private const string NowMs = "CAST((julianday('now') - 2440587.5) * 86400000 AS INTEGER)";

    // The store's six triggers per table (SqliteSql.CreateTriggers + CreateCommandTriggers); only ever evaluated in the "triggers live" variants.
    private const string Triggers = $"""
        CREATE TRIGGER g9_Pot_ai AFTER INSERT ON Pot WHEN {Applying}
        BEGIN
            UPDATE g9_clock SET seq = seq + 1 WHERE id = 1;
            INSERT OR IGNORE INTO g9_rows (tid, pk) VALUES (2, lower(NEW.Id));
            UPDATE g9_rows SET local_seq = {Seq}, dirty = 1, dirty_mask = 4095, hlc_ms = {NowMs}, rejected = 0, deleted = 0 WHERE tid = 2 AND pk = lower(NEW.Id);
        END;
        CREATE TRIGGER g9_Pot_au AFTER UPDATE ON Pot WHEN {Applying} AND ((CASE WHEN OLD.SiteId IS NOT NEW.SiteId THEN 1 ELSE 0 END) | (CASE WHEN OLD.Code IS NOT NEW.Code THEN 2 ELSE 0 END)
            | (CASE WHEN OLD.Status IS NOT NEW.Status THEN 4 ELSE 0 END) | (CASE WHEN OLD."Row" IS NOT NEW."Row" THEN 8 ELSE 0 END) | (CASE WHEN OLD.Col IS NOT NEW.Col THEN 16 ELSE 0 END)
            | (CASE WHEN OLD.Weight IS NOT NEW.Weight THEN 32 ELSE 0 END) | (CASE WHEN OLD.Latitude IS NOT NEW.Latitude THEN 64 ELSE 0 END) | (CASE WHEN OLD.Longitude IS NOT NEW.Longitude THEN 128 ELSE 0 END)
            | (CASE WHEN OLD.PlantedAt IS NOT NEW.PlantedAt THEN 256 ELSE 0 END) | (CASE WHEN OLD.Note IS NOT NEW.Note THEN 512 ELSE 0 END) | (CASE WHEN OLD.IsDeleted IS NOT NEW.IsDeleted THEN 1024 ELSE 0 END)) <> 0
        BEGIN
            UPDATE g9_clock SET seq = seq + 1 WHERE id = 1;
            INSERT OR IGNORE INTO g9_rows (tid, pk) VALUES (2, lower(NEW.Id));
            UPDATE g9_rows SET local_seq = {Seq}, dirty = CASE WHEN dirty = 2 THEN 2 ELSE 1 END, dirty_mask = dirty_mask | 1, hlc_ms = {NowMs}, rejected = 0, deleted = 0 WHERE tid = 2 AND pk = lower(NEW.Id);
        END;
        CREATE TRIGGER g9_Pot_ad AFTER DELETE ON Pot WHEN {Applying}
        BEGIN
            UPDATE g9_clock SET seq = seq + 1 WHERE id = 1;
            UPDATE g9_rows SET local_seq = {Seq}, dirty = 2, dirty_mask = 0, hlc_ms = {NowMs}, rejected = 0 WHERE tid = 2 AND pk = lower(OLD.Id) AND NOT (server_ver = 0 AND subs = 0 AND dirty = 1);
        END;
        CREATE TRIGGER g9_Pot_ci AFTER INSERT ON Pot WHEN {CommandMode}
        BEGIN
            INSERT OR IGNORE INTO g9_command_rows (command_id, tid, pk) VALUES ((SELECT command_id FROM g9_ctx WHERE id = 1), 2, lower(NEW.Id));
        END;
        CREATE TRIGGER g9_Pot_cu AFTER UPDATE ON Pot WHEN {CommandMode}
        BEGIN
            INSERT OR IGNORE INTO g9_command_rows (command_id, tid, pk) VALUES ((SELECT command_id FROM g9_ctx WHERE id = 1), 2, lower(NEW.Id));
        END;
        CREATE TRIGGER g9_Pot_cd AFTER DELETE ON Pot WHEN {CommandMode}
        BEGIN
            INSERT OR IGNORE INTO g9_command_rows (command_id, tid, pk) VALUES ((SELECT command_id FROM g9_ctx WHERE id = 1), 2, lower(OLD.Id));
        END;
        """;

    private const string UpsertSet = "SiteId = excluded.SiteId, Code = excluded.Code, Status = excluded.Status, \"Row\" = excluded.\"Row\", Col = excluded.Col, " +
                                     "Weight = excluded.Weight, Latitude = excluded.Latitude, Longitude = excluded.Longitude, PlantedAt = excluded.PlantedAt, Note = excluded.Note, IsDeleted = excluded.IsDeleted";

    private const string Guard = " WHERE Pot.SiteId IS NOT excluded.SiteId OR Pot.Code IS NOT excluded.Code OR Pot.Status IS NOT excluded.Status OR Pot.\"Row\" IS NOT excluded.\"Row\" OR Pot.Col IS NOT excluded.Col " +
                                 "OR Pot.Weight IS NOT excluded.Weight OR Pot.Latitude IS NOT excluded.Latitude OR Pot.Longitude IS NOT excluded.Longitude OR Pot.PlantedAt IS NOT excluded.PlantedAt " +
                                 "OR Pot.Note IS NOT excluded.Note OR Pot.IsDeleted IS NOT excluded.IsDeleted";

    private const string MetaUpsert =
        "INSERT INTO g9_rows (tid, pk, server_ver, local_seq, subs, dirty, dirty_mask, rejected, deleted) VALUES (?1, ?2, ?3, ?4, ?5, 0, 0, 0, 0) " +
        "ON CONFLICT(tid, pk) DO UPDATE SET server_ver = excluded.server_ver, local_seq = excluded.local_seq, subs = subs | excluded.subs, dirty = 0, dirty_mask = 0, rejected = 0, deleted = 0";

    private const string MetaUpsertSurrogate =
        "INSERT INTO g9_rows (rid, tid, server_ver, local_seq, subs, dirty, dirty_mask, rejected, deleted) VALUES (?1, 2, ?3, ?4, ?5, 0, 0, 0, 0) " +
        "ON CONFLICT(rid) DO UPDATE SET server_ver = excluded.server_ver, local_seq = excluded.local_seq, subs = subs | excluded.subs, dirty = 0, dirty_mask = 0, rejected = 0, deleted = 0";

    /// <summary>The apply: BEGIN IMMEDIATE .. COMMIT (one or many), as G9CSqliteSyncStore.ApplyStagedPullAsync does it. Returns load ms and the WAL's peak size.</summary>
    public static (double LoadMs, long WalPeak) Apply(sqlite3 db, string path, Row[][] segments, Opt o, Action? afterEachCommit = null)
    {
        var walPath = path + "-wal";
        long walPeak = 0;
        void TrackWal()
        {
            var info = new FileInfo(walPath);
            if (info.Exists && info.Length > walPeak) walPeak = info.Length;
        }

        var watch = Stopwatch.StartNew();
        long clock = Convert.ToInt64(Scalar(db, "SELECT seq FROM g9_clock WHERE id = 1"), CultureInfo.InvariantCulture);
        var total = segments.Sum(s => s.Length);
        var all = segments.Length == 1 ? segments[0] : segments.SelectMany(s => s).ToArray();
        var perCommit = o.RowsPerCommit <= 0 ? int.MaxValue : o.RowsPerCommit;
        var first = true;

        for (var offset = 0; offset < total || first; offset += Math.Min(perCommit, total - offset))
        {
            var count = Math.Min(perCommit, total - offset);
            var last = offset + count >= total;
            if (o.TriggersOff && (raw.sqlite3_db_config(db, raw.SQLITE_DBCONFIG_ENABLE_TRIGGER, 0, out var off) != raw.SQLITE_OK || off != 0)) throw new InvalidOperationException("triggers");
            Exec(db, "BEGIN IMMEDIATE; UPDATE g9_ctx SET applying = 1 WHERE id = 1;");
            if (first && o.DeferIndexes) Exec(db, DropIndexes);
            if (first && o.DeferMetaIndexes) Exec(db, "DROP INDEX g9_rows_dirty; DROP INDEX g9_rows_feed;");
            first = false;

            if (o.TempStaging) ApplyThroughTempStaging(db, all, offset, count, ref clock);
            else ApplyRows(db, all, offset, count, o, ref clock);

            if (last && o.Meta == Meta.SetBasedAfter)
            {
                // Upper bound of what set-based metadata could buy: versions are NOT available in the data table, so a constant stands in.
                Exec(db, $"INSERT INTO g9_rows (tid, pk, server_ver, local_seq, subs) SELECT 2, Id, 1, {clock} + rowid, 1 FROM Pot ORDER BY Id");
                clock += total;
            }

            if (last && o.DeferIndexes) Exec(db, Indexes);
            if (last && o.DeferMetaIndexes) Exec(db, MetaIndexes);
            Exec(db, $"UPDATE g9_clock SET seq = {clock} WHERE id = 1; UPDATE g9_ctx SET applying = 0 WHERE id = 1; COMMIT;");
            if (o.TriggersOff) raw.sqlite3_db_config(db, raw.SQLITE_DBCONFIG_ENABLE_TRIGGER, 1, out _);
            TrackWal();
            afterEachCommit?.Invoke();
            if (total == 0) break;
        }

        return (watch.Elapsed.TotalMilliseconds, walPeak);
    }

    private static void ApplyRows(sqlite3 db, Row[] rows, int offset, int count, Opt o, ref long clock)
    {
        var sql = $"INSERT INTO Pot ({Cols}) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12) ON CONFLICT(Id) DO UPDATE SET {UpsertSet}" + (o.GuardedUpsert ? Guard : "") +
                  (o.Meta == Meta.IntSurrogate ? " RETURNING rowid" : "");
        Prepare(db, sql, out var upsert);
        Prepare(db, o.Meta == Meta.IntSurrogate ? MetaUpsertSurrogate : MetaUpsert, out var meta);
        try
        {
            for (var i = offset; i < offset + count; i++)
            {
                var row = rows[i];
                long rowid = 0;
                if (o.DataRows)
                {
                    BindRow(upsert, row);
                    var rc = raw.sqlite3_step(upsert);
                    if (rc == raw.SQLITE_ROW) rowid = raw.sqlite3_column_int64(upsert, 0);
                    else if (rc != raw.SQLITE_DONE) throw new InvalidOperationException(raw.sqlite3_errmsg(db).utf8_to_string());
                    raw.sqlite3_reset(upsert);
                }

                if (o.Meta is Meta.PerRow or Meta.IntSurrogate)
                {
                    if (o.Meta == Meta.IntSurrogate) raw.sqlite3_bind_int64(meta, 1, (2L << 40) | rowid);
                    else
                    {
                        raw.sqlite3_bind_int64(meta, 1, 2);
                        raw.sqlite3_bind_text(meta, 2, row.Key.AsSpan());
                    }

                    raw.sqlite3_bind_int64(meta, 3, row.Version);
                    raw.sqlite3_bind_int64(meta, 4, ++clock);
                    raw.sqlite3_bind_int64(meta, 5, 1);
                    Step(db, meta);
                }
            }
        }
        finally
        {
            raw.sqlite3_finalize(upsert);
            raw.sqlite3_finalize(meta);
        }
    }

    private static void ApplyThroughTempStaging(sqlite3 db, Row[] rows, int offset, int count, ref long clock)
    {
        Exec(db, "CREATE TEMP TABLE IF NOT EXISTS stage_pot (Id TEXT NOT NULL, SiteId TEXT, Code TEXT, Status INTEGER, \"Row\" INTEGER, Col INTEGER, Weight REAL, Latitude REAL, Longitude REAL, PlantedAt TEXT, Note TEXT, IsDeleted INTEGER, ver INTEGER NOT NULL)");
        Prepare(db, "INSERT INTO temp.stage_pot VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12, ?13)", out var insert);
        try
        {
            for (var i = offset; i < offset + count; i++)
            {
                BindRow(insert, rows[i]);
                raw.sqlite3_bind_int64(insert, 13, rows[i].Version);
                Step(db, insert);
            }
        }
        finally
        {
            raw.sqlite3_finalize(insert);
        }

        Exec(db, $"INSERT INTO Pot ({Cols}) SELECT {Cols} FROM temp.stage_pot WHERE true ORDER BY Id ON CONFLICT(Id) DO UPDATE SET {UpsertSet}");
        Exec(db, $"INSERT INTO g9_rows (tid, pk, server_ver, local_seq, subs, dirty, dirty_mask, rejected, deleted) SELECT 2, Id, ver, {clock} + rowid, 1, 0, 0, 0, 0 FROM temp.stage_pot WHERE true ORDER BY Id " +
                 "ON CONFLICT(tid, pk) DO UPDATE SET server_ver = excluded.server_ver, local_seq = excluded.local_seq, subs = subs | excluded.subs, dirty = 0, dirty_mask = 0, rejected = 0, deleted = 0");
        Exec(db, "DROP TABLE temp.stage_pot");
        clock += count;
    }

    public static void BindRow(sqlite3_stmt s, Row row)
    {
        raw.sqlite3_bind_text(s, 1, row.Key.AsSpan());
        raw.sqlite3_bind_text(s, 2, row.Site.AsSpan());
        raw.sqlite3_bind_text(s, 3, row.Code.AsSpan());
        raw.sqlite3_bind_int64(s, 4, row.Status);
        raw.sqlite3_bind_int64(s, 5, row.RowNo);
        raw.sqlite3_bind_int64(s, 6, row.Col);
        raw.sqlite3_bind_double(s, 7, row.Weight);
        raw.sqlite3_bind_double(s, 8, row.Lat);
        raw.sqlite3_bind_double(s, 9, row.Lon);
        raw.sqlite3_bind_text(s, 10, row.PlantedAt.AsSpan());
        if (row.Note is null) raw.sqlite3_bind_null(s, 11);
        else raw.sqlite3_bind_text(s, 11, row.Note.AsSpan());
        raw.sqlite3_bind_int64(s, 12, 0);
    }

    public static double Checkpoint(sqlite3 db, string mode)
    {
        var watch = Stopwatch.StartNew();
        Exec(db, $"PRAGMA wal_checkpoint({mode})");
        return watch.Elapsed.TotalMilliseconds;
    }

    public static void Exec(sqlite3 db, string sql)
    {
        if (raw.sqlite3_exec(db, sql, null, null, out var error) != raw.SQLITE_OK) throw new InvalidOperationException(error + " in: " + sql[..Math.Min(sql.Length, 160)]);
    }

    public static object? Scalar(sqlite3 db, string sql)
    {
        Prepare(db, sql, out var s);
        try
        {
            if (raw.sqlite3_step(s) != raw.SQLITE_ROW) return null;
            return raw.sqlite3_column_type(s, 0) switch
            {
                raw.SQLITE_INTEGER => raw.sqlite3_column_int64(s, 0),
                raw.SQLITE_FLOAT => raw.sqlite3_column_double(s, 0),
                raw.SQLITE_NULL => null,
                _ => raw.sqlite3_column_text(s, 0).utf8_to_string()
            };
        }
        finally
        {
            raw.sqlite3_finalize(s);
        }
    }

    public static void Prepare(sqlite3 db, string sql, out sqlite3_stmt statement)
    {
        if (raw.sqlite3_prepare_v2(db, sql, out statement) != raw.SQLITE_OK) throw new InvalidOperationException(raw.sqlite3_errmsg(db).utf8_to_string() + " in: " + sql[..Math.Min(sql.Length, 160)]);
    }

    public static void Step(sqlite3 db, sqlite3_stmt statement)
    {
        var rc = raw.sqlite3_step(statement);
        raw.sqlite3_reset(statement);
        if (rc != raw.SQLITE_DONE && rc != raw.SQLITE_ROW) throw new InvalidOperationException(raw.sqlite3_errmsg(db).utf8_to_string());
    }

    public static void DeleteDatabase(string path)
    {
        foreach (var file in new[] { path, path + "-wal", path + "-shm" })
        {
            try { File.Delete(file); } catch (IOException) { }
        }
    }

    public static long DbBytes(string path) => new FileInfo(path).Length;

    public static int Checksum(ReadOnlySpan<byte> bytes)
    {
        var hash = new HashCode();
        hash.AddBytes(bytes);
        return hash.ToHashCode();
    }

    /// <summary>Runs a variant on a fresh (empty) database.</summary>
    public static Result RunEmpty(string dir, Row[][] segments, Opt o, int runs)
    {
        Result? best = null;
        for (var attempt = 0; attempt < runs; attempt++)
        {
            var path = Path.Combine(dir, $"e{Guid.NewGuid():N}.db");
            var db = Open(path, o, create: true);
            try
            {
                CreateSchema(db, o);
                var (load, wal) = Apply(db, path, segments, o);
                var checkpoint = Checkpoint(db, o.FinalCheckpoint);
                var result = new Result(load, checkpoint, wal, DbBytes(path));
                if (best is null || result.LoadMs + result.CheckpointMs < best.LoadMs + best.CheckpointMs) best = result;
            }
            finally
            {
                Close(db);
                DeleteDatabase(path);
            }
        }

        return best!;
    }

    /// <summary>Builds the 500k-row base database the way the store's fast path does (variant N) and checkpoints it.</summary>
    public static string BuildBase(string dir, Row[] rows, Opt schema, string name)
    {
        var path = Path.Combine(dir, name);
        DeleteDatabase(path);
        var o = schema with { DeferIndexes = true };
        var db = Open(path, o, create: true);
        try
        {
            CreateSchema(db, o);
            Apply(db, path, [rows], o);
            Checkpoint(db, "TRUNCATE");
            Exec(db, "PRAGMA optimize");
        }
        finally
        {
            Close(db);
        }

        return path;
    }

    /// <summary>Runs a variant on a COPY of the base database.</summary>
    public static Result RunOnBase(string dir, string basePath, Row[][] segments, Opt o, int runs, Func<string, (Action Stop, Func<string> Report)>? reader = null)
    {
        Result? best = null;
        for (var attempt = 0; attempt < runs; attempt++)
        {
            var path = Path.Combine(dir, $"n{Guid.NewGuid():N}.db");
            File.Copy(basePath, path);
            var db = Open(path, o, create: false);
            try
            {
                Scalar(db, "SELECT count(*) FROM g9_ctx"); // touch the file; the OS cache holds the copy anyway
                var running = reader?.Invoke(path);
                var (load, wal) = Apply(db, path, segments, o);
                var extra = "";
                if (running is not null)
                {
                    // The WAL as the application sees it while the reader is still there.
                    var during = Checkpoint(db, "PASSIVE");
                    var walDuring = new FileInfo(path + "-wal").Length;
                    running.Value.Stop();
                    extra = $"{running.Value.Report()}; PASSIVE checkpoint with reader {during:N0} ms, WAL then {walDuring / 1048576d:N1} MB";
                }

                var checkpoint = Checkpoint(db, o.FinalCheckpoint);
                var result = new Result(load, checkpoint, wal, DbBytes(path), extra);
                if (best is null || result.LoadMs + result.CheckpointMs < best.LoadMs + best.CheckpointMs) best = result;
            }
            finally
            {
                Close(db);
                DeleteDatabase(path);
            }
        }

        return best!;
    }
}

internal static class Experiments
{
    private static string Line(string name, Result r, Result? baseline, int rows) =>
        $"| {name} | {r.LoadMs:N0} | {r.CheckpointMs:N0} | {r.LoadMs + r.CheckpointMs:N0} | {(baseline is null ? "1.00" : ((r.LoadMs + r.CheckpointMs) / (baseline.LoadMs + baseline.CheckpointMs)).ToString("N2", CultureInfo.InvariantCulture))} | " +
        $"{rows / (r.LoadMs / 1000):N0} | {r.WalPeak / 1048576d:N1} | {r.FileBytes / 1048576d:N1} | {r.Extra} |";

    private const string Header = "| Variant | Load ms | Final checkpoint ms | Total ms | Total vs baseline | Rows/s (load) | WAL peak MB | DB MB | Notes |\n|---|---|---|---|---|---|---|---|---|";

    // ---------------------------------------------------------------- 1
    public static void CostSplit(string dir, Row[] farm, int runs, Action<string> say)
    {
        say("");
        say($"### Experiment 1 - cost split on the EMPTY-table fast path (variant N shape), {farm.Length:N0} rows");
        say(Header);
        var variants = new[]
        {
            new Opt("1a. baseline = store fast path N (triggers off, Pot indexes deferred, per-row g9_rows upsert)", DeferIndexes: true),
            new Opt("1b. data rows only (no g9_rows write)", DeferIndexes: true, Meta: Meta.None),
            new Opt("1c. g9_rows only (no data rows)", DeferIndexes: true, DataRows: false),
            new Opt("1d. 1a with g9_rows as a ROWID table + PK index", DeferIndexes: true, MetaRowid: true),
            new Opt("1e. 1a with g9_rows indexes (dirty, feed) dropped and rebuilt after the load", DeferIndexes: true, DeferMetaIndexes: true),
            new Opt("1f. metadata set-based after the load (INSERT..SELECT from Pot, constant version = upper bound)", DeferIndexes: true, Meta: Meta.SetBasedAfter),
            new Opt("1g. 1f with g9_rows indexes rebuilt after", DeferIndexes: true, Meta: Meta.SetBasedAfter, DeferMetaIndexes: true),
            new Opt("1h. integer surrogate: g9_rows(rid INTEGER PRIMARY KEY = tid<<40|rowid), upsert RETURNING rowid", DeferIndexes: true, Meta: Meta.IntSurrogate),
            new Opt("1i. 1a with COLLATE NOCASE on Id/SiteId (what the store declares for tables IT creates)", DeferIndexes: true, KeyNoCase: true),
            new Opt("1j. 1a with triggers LIVE (a foreign trigger exists => switch refused)", DeferIndexes: true, TriggersOff: false),
            new Opt("1k. 1a with live Pot indexes (table not empty => no deferral) ", DeferIndexes: false),
            new Opt("1l. 1a with triggers LIVE and live indexes (no fast path at all)", DeferIndexes: false, TriggersOff: false)
        };
        Result? baseline = null;
        foreach (var v in variants)
        {
            var r = Bench.RunEmpty(dir, [farm], v, runs);
            baseline ??= r;
            say(Line(v.Name, r, baseline, farm.Length));
        }
    }

    // ---------------------------------------------------------------- 3
    public static void Slices(string dir, Row[] farm, int runs, Action<string> say)
    {
        say("");
        say($"### Experiment 3 - one transaction vs committed slices, EMPTY start, {farm.Length:N0} rows, triggers off");
        say(Header);
        var variants = new[]
        {
            new Opt("3a. one transaction, indexes deferred (fast path N)", DeferIndexes: true),
            new Opt("3b. one transaction, live indexes"),
            new Opt("3c. 2,000 rows/commit, live indexes, NORMAL, autocheckpoint 1000", RowsPerCommit: 2000),
            new Opt("3d. 3c with synchronous=FULL", RowsPerCommit: 2000, Sync: "FULL"),
            new Opt("3e. 3c with wal_autocheckpoint=0 + one TRUNCATE checkpoint at the end", RowsPerCommit: 2000, AutoCheckpoint: 0),
            new Opt("3f. 3d (FULL) with wal_autocheckpoint=0 + one checkpoint at the end", RowsPerCommit: 2000, Sync: "FULL", AutoCheckpoint: 0),
            new Opt("3g. 20,000 rows/commit, live indexes, NORMAL", RowsPerCommit: 20000),
            new Opt("3h. 3c with a 64 MB page cache", RowsPerCommit: 2000, CacheKb: 65536),
            new Opt("3i. 3b with a 64 MB page cache", CacheKb: 65536),
            new Opt("3j. 3a with final checkpoint PASSIVE instead of TRUNCATE", DeferIndexes: true, FinalCheckpoint: "PASSIVE")
        };
        Result? baseline = null;
        foreach (var v in variants)
        {
            var r = Bench.RunEmpty(dir, [farm], v, runs);
            baseline ??= r;
            say(Line(v.Name, r, baseline, farm.Length));
        }
    }

    // ---------------------------------------------------------------- 4
    public static void PageSize(string dir, Row[] farm, byte[] siteB, int runs, Action<string> say)
    {
        say("");
        say($"### Experiment 4 - page_size, (i) empty fast path N with {farm.Length:N0} rows, (ii) as-is apply of 150k new + 50k updates on that base");
        say(Header);
        var second = Bench.Generate(150_000, 2, siteB, farm.Length + 1);
        var updates = Bench.Updates(farm, 10);
        Result? baselineEmpty = null, baselineNonEmpty = null;
        foreach (var size in new[] { 4096, 8192, 16384 })
        {
            var o = new Opt($"4-{size}. empty load, fast path N", DeferIndexes: true, PageSize: size);
            var r = Bench.RunEmpty(dir, [farm], o, runs);
            baselineEmpty ??= r;
            say(Line(o.Name, r, baselineEmpty, farm.Length));

            var basePath = Bench.BuildBase(dir, farm, new Opt("base", PageSize: size), $"base-{size}.db");
            var n = new Opt($"4-{size}. non-empty as-is (live indexes, default cache_size -2000 KiB)", PageSize: size);
            var rn = Bench.RunOnBase(dir, basePath, [second, updates], n, runs);
            baselineNonEmpty ??= rn;
            say(Line(n.Name, rn, baselineNonEmpty, second.Length + updates.Length));
            var n2 = new Opt($"4-{size}. non-empty with 64 MB cache", PageSize: size, CacheKb: 65536);
            var rn2 = Bench.RunOnBase(dir, basePath, [second, updates], n2, runs);
            say(Line(n2.Name, rn2, baselineNonEmpty, second.Length + updates.Length));
            if (size != 4096) Bench.DeleteDatabase(basePath);
        }
    }

    // ---------------------------------------------------------------- 2
    public static void NonEmpty(string dir, Row[] farm, byte[] siteB, int runs, Action<string> say)
    {
        var basePath = Path.Combine(dir, "base-4096.db");
        if (!File.Exists(basePath)) basePath = Bench.BuildBase(dir, farm, new Opt("base"), "base-4096.db");
        var second = Bench.Generate(150_000, 2, siteB, farm.Length + 1);
        var updates = Bench.Updates(farm, 10);
        var rows = second.Length + updates.Length;

        say("");
        say($"### Experiment 2 - NON-EMPTY table: {farm.Length:N0} rows present, apply {second.Length:N0} new (second farm, keys interleave) + {updates.Length:N0} updates, one transaction");
        say(Header);
        var variants = new[]
        {
            new Opt("2a. as-is: upsert in key order, live indexes, triggers off, default cache (-2000 KiB)"),
            new Opt("2b. 2a with triggers LIVE (foreign trigger present)", TriggersOff: false),
            new Opt("2c. 2a + cache_size 16 MB", CacheKb: 16384),
            new Opt("2d. 2a + cache_size 64 MB", CacheKb: 65536),
            new Opt("2e. 2a + cache_size 256 MB", CacheKb: 262144),
            new Opt("2f. 2a + mmap_size 512 MB (default cache)", Mmap: 536870912),
            new Opt("2g. 2a + cache 64 MB + mmap 512 MB", CacheKb: 65536, Mmap: 536870912),
            new Opt("2h. drop + rebuild Pot indexes anyway (default cache)", DeferIndexes: true),
            new Opt("2i. 2h + cache 64 MB + temp_store=MEMORY", DeferIndexes: true, CacheKb: 65536, TempStoreMemory: true),
            new Opt("2j. TEMP staging table, then INSERT..SELECT ORDER BY key upsert + set-based g9_rows (default cache)", TempStaging: true),
            new Opt("2k. 2j + cache 64 MB + temp_store=MEMORY", TempStaging: true, CacheKb: 65536, TempStoreMemory: true),
            new Opt("2l. 2d + g9_rows indexes dropped/rebuilt", CacheKb: 65536, DeferMetaIndexes: true)
        };
        Result? baseline = null;
        foreach (var v in variants)
        {
            var r = Bench.RunOnBase(dir, basePath, [second, updates], v, runs);
            baseline ??= r;
            say(Line(v.Name, r, baseline, rows));
        }

        say("");
        say("#### 2-ratio: when does drop+rebuild beat live index maintenance? (new rows only, keys interleave; 500k existing)");
        say("| New rows | Ratio new/existing | live, default cache ms | live, 64 MB cache ms | drop+rebuild, default cache ms | drop+rebuild, 64 MB + temp_store=MEMORY ms |");
        say("|---|---|---|---|---|---|");
        foreach (var count in new[] { 25_000, 50_000, 150_000, 500_000 })
        {
            var extra = count == second.Length ? second : Bench.Generate(count, 3, siteB, farm.Length + 1);
            double Total(Opt o)
            {
                var r = Bench.RunOnBase(dir, basePath, [extra], o, runs);
                return r.LoadMs + r.CheckpointMs;
            }

            say($"| {count:N0} | {count / (double)farm.Length:P0} | {Total(new Opt("live")):N0} | {Total(new Opt("live64", CacheKb: 65536)):N0} | " +
                $"{Total(new Opt("rebuild", DeferIndexes: true)):N0} | {Total(new Opt("rebuild64", DeferIndexes: true, CacheKb: 65536, TempStoreMemory: true)):N0} |");
        }
    }

    // ---------------------------------------------------------------- 8
    public static void Reapply(string dir, Row[] farm, int runs, Action<string> say)
    {
        var basePath = Path.Combine(dir, "base-4096.db");
        if (!File.Exists(basePath)) basePath = Bench.BuildBase(dir, farm, new Opt("base"), "base-4096.db");
        var same = farm.Take(200_000).ToArray();
        say("");
        say($"### Experiment 8 - re-applying {same.Length:N0} IDENTICAL rows (interrupted pull pulled again / reconcile fetch), table holds {farm.Length:N0}");
        say(Header);
        Result? baseline = null;
        foreach (var v in new[]
                 {
                     new Opt("8a. as-is upsert (rewrites the row and both index entries)"),
                     new Opt("8b. upsert guarded with WHERE any column IS NOT excluded.column", GuardedUpsert: true),
                     new Opt("8c. 8a + 64 MB cache", CacheKb: 65536),
                     new Opt("8d. 8b + 64 MB cache", GuardedUpsert: true, CacheKb: 65536)
                 })
        {
            var r = Bench.RunOnBase(dir, basePath, [same], v, runs);
            baseline ??= r;
            say(Line(v.Name, r, baseline, same.Length));
        }
    }

    // ---------------------------------------------------------------- 5
    public static void Staging(string dir, int runs, Action<string> say)
    {
        const int Chunks = 250;
        const int ChunkBytes = 80 * 1024;
        say("");
        say($"### Experiment 5 - the staging double-write: {Chunks} incompressible blobs of {ChunkBytes / 1024} KB ({Chunks * (long)ChunkBytes / 1048576d:N1} MB)");
        say("| Variant | Stage write ms | Read back ms | Delete + commit ms | Final checkpoint ms | Total ms | WAL file MB after staging | WAL file MB after delete | DB file after MB |");
        say("|---|---|---|---|---|---|---|---|---|");
        var random = new Random(5);
        var blobs = Enumerable.Range(0, Chunks).Select(_ => { var b = new byte[ChunkBytes]; random.NextBytes(b); return b; }).ToArray();

        foreach (var (name, sync, autoCheckpoint, pragmasPerChunk) in new[]
                 {
                     ("5a. g9_stage, commit per chunk, NORMAL, autocheckpoint 1000, store's per-call PRAGMAs", "NORMAL", 1000, true),
                     ("5b. 5a without the per-call PRAGMAs", "NORMAL", 1000, false),
                     ("5c. 5a with synchronous=FULL (SynchronousNormalForDownloads=false)", "FULL", 1000, true),
                     ("5d. 5a with wal_autocheckpoint=0 (to count every WAL frame)", "NORMAL", 0, true)
                 })
        {
            (double Write, double Read, double Delete, double Checkpoint, long WalStage, long WalDelete, long File)? best = null;
            for (var attempt = 0; attempt < runs; attempt++)
            {
                var path = Path.Combine(dir, $"s{Guid.NewGuid():N}.db");
                var o = new Opt(name, Sync: "FULL", AutoCheckpoint: autoCheckpoint);   // the app's handle default is FULL; DownloadDurability lowers it per call
                var db = Bench.Open(path, o, create: true);
                try
                {
                    Bench.CreateSchema(db, o);
                    Bench.Checkpoint(db, "TRUNCATE");
                    long walAfterStage = 0;
                    var watch = Stopwatch.StartNew();
                    Bench.Prepare(db, "INSERT OR REPLACE INTO g9_stage (run_id, seq, subscription_key, row_count, continuation_json, payload) VALUES (?1, ?2, ?3, ?4, ?5, ?6)", out var insert);
                    for (var i = 0; i < Chunks; i++)
                    {
                        if (pragmasPerChunk)
                        {
                            Bench.Exec(db, "PRAGMA busy_timeout = 30000; PRAGMA foreign_keys = OFF; PRAGMA journal_size_limit = 67108864;");
                            Bench.Scalar(db, "PRAGMA journal_mode");
                            Bench.Scalar(db, "PRAGMA synchronous");
                        }

                        Bench.Exec(db, "PRAGMA synchronous = " + sync);
                        Bench.Exec(db, "BEGIN IMMEDIATE");
                        raw.sqlite3_bind_text(insert, 1, "run-1");
                        raw.sqlite3_bind_int64(insert, 2, i);
                        raw.sqlite3_bind_text(insert, 3, "sub");
                        raw.sqlite3_bind_int64(insert, 4, 2000);
                        raw.sqlite3_bind_text(insert, 5, "{\"p\":1,\"after\":\"0000\"}");
                        raw.sqlite3_bind_blob(insert, 6, blobs[i]);
                        Bench.Step(db, insert);
                        Bench.Exec(db, "COMMIT");
                        if (pragmasPerChunk) Bench.Exec(db, "PRAGMA synchronous = FULL");
                    }

                    raw.sqlite3_finalize(insert);
                    var write = watch.Elapsed.TotalMilliseconds;
                    walAfterStage = new FileInfo(path + "-wal").Length;

                    watch.Restart();
                    Bench.Exec(db, "PRAGMA synchronous = NORMAL; BEGIN IMMEDIATE");
                    Bench.Prepare(db, "SELECT payload, subscription_key FROM g9_stage WHERE run_id = ?1 AND seq = ?2", out var select);
                    long bytes = 0;
                    for (var i = 0; i < Chunks; i++)
                    {
                        raw.sqlite3_bind_text(select, 1, "run-1");
                        raw.sqlite3_bind_int64(select, 2, i);
                        if (raw.sqlite3_step(select) == raw.SQLITE_ROW) bytes += raw.sqlite3_column_blob(select, 0).ToArray().Length;
                        raw.sqlite3_reset(select);
                    }

                    raw.sqlite3_finalize(select);
                    var read = watch.Elapsed.TotalMilliseconds;
                    if (bytes != Chunks * (long)ChunkBytes) throw new InvalidOperationException("read back");

                    watch.Restart();
                    Bench.Exec(db, "DELETE FROM g9_stage WHERE run_id = 'run-1'; COMMIT");
                    var delete = watch.Elapsed.TotalMilliseconds;
                    var walAfterDelete = new FileInfo(path + "-wal").Length;
                    var checkpoint = Bench.Checkpoint(db, "TRUNCATE");
                    var candidate = (write, read, delete, checkpoint, walAfterStage, walAfterDelete, Bench.DbBytes(path));
                    if (best is null || write + read + delete + checkpoint < best.Value.Write + best.Value.Read + best.Value.Delete + best.Value.Checkpoint) best = candidate;
                }
                finally
                {
                    Bench.Close(db);
                    Bench.DeleteDatabase(path);
                }
            }

            var b = best!.Value;
            say($"| {name} | {b.Write:N0} | {b.Read:N0} | {b.Delete:N0} | {b.Checkpoint:N0} | {b.Write + b.Read + b.Delete + b.Checkpoint:N0} | {b.WalStage / 1048576d:N1} | {b.WalDelete / 1048576d:N1} | {b.File / 1048576d:N1} |");
        }

        foreach (var (name, flushEach, flushEnd) in new[]
                 {
                     ("5e. side file, append + fsync per chunk (Flush(true))", true, false),
                     ("5f. side file, append, ONE fsync at the end", false, true),
                     ("5g. side file, append, no fsync (OS cache only)", false, false)
                 })
        {
            double bestWrite = double.MaxValue, bestRead = 0, bestDelete = 0;
            for (var attempt = 0; attempt < runs; attempt++)
            {
                var path = Path.Combine(dir, $"s{Guid.NewGuid():N}.stage");
                var watch = Stopwatch.StartNew();
                using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1, FileOptions.SequentialScan))
                {
                    Span<byte> header = stackalloc byte[16];
                    for (var i = 0; i < Chunks; i++)
                    {
                        BitConverter.TryWriteBytes(header, (long)i);
                        BitConverter.TryWriteBytes(header[8..], blobs[i].Length);
                        BitConverter.TryWriteBytes(header[12..], Bench.Checksum(blobs[i]));
                        stream.Write(header);
                        stream.Write(blobs[i]);
                        if (flushEach) stream.Flush(flushToDisk: true);
                    }

                    if (flushEnd) stream.Flush(flushToDisk: true);
                }

                var write = watch.Elapsed.TotalMilliseconds;
                watch.Restart();
                long bytes = 0;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan))
                {
                    var header = new byte[16];
                    for (var i = 0; i < Chunks; i++)
                    {
                        stream.ReadExactly(header);
                        var length = BitConverter.ToInt32(header, 8);
                        var buffer = new byte[length];
                        stream.ReadExactly(buffer);
                        if (Bench.Checksum(buffer) != BitConverter.ToInt32(header, 12)) throw new InvalidOperationException("crc");
                        bytes += length;
                    }
                }

                var read = watch.Elapsed.TotalMilliseconds;
                watch.Restart();
                File.Delete(path);
                var delete = watch.Elapsed.TotalMilliseconds;
                if (write + read + delete < bestWrite + bestRead + bestDelete) (bestWrite, bestRead, bestDelete) = (write, read, delete);
            }

            say($"| {name} | {bestWrite:N0} | {bestRead:N0} | {bestDelete:N0} | 0 | {bestWrite + bestRead + bestDelete:N0} | 0 | 0 | +0 |");
        }
    }

    // ---------------------------------------------------------------- 7
    public static void Readers(string dir, Row[] farm, byte[] siteB, int runs, Action<string> say)
    {
        var basePath = Path.Combine(dir, "base-4096.db");
        if (!File.Exists(basePath)) basePath = Bench.BuildBase(dir, farm, new Opt("base"), "base-4096.db");
        var second = Bench.Generate(150_000, 2, siteB, farm.Length + 1);
        var updates = Bench.Updates(farm, 10);
        var rows = second.Length + updates.Length;

        say("");
        say($"### Experiment 7 - a reader on a second connection during the apply (non-empty base, {rows:N0} rows applied, live indexes, 64 MB cache off)");
        say(Header);

        Func<string, (Action Stop, Func<string> Report)> ShortReads(int sleepMs) => path =>
        {
            var stop = new ManualResetEventSlim();
            long queries = 0, maxMs = 0, rowsRead = 0;
            var thread = new Thread(() =>
            {
                raw.sqlite3_open_v2(path, out var db, raw.SQLITE_OPEN_READONLY, null);
                Bench.Exec(db, "PRAGMA busy_timeout = 30000");
                Bench.Prepare(db, "SELECT Id, Latitude, Longitude, Status FROM Pot WHERE \"Row\" BETWEEN ?1 AND ?2", out var select);
                var random = new Random(7);
                while (!stop.IsSet)
                {
                    var watch = Stopwatch.StartNew();
                    var from = random.Next(0, 1200);
                    raw.sqlite3_bind_int64(select, 1, from);
                    raw.sqlite3_bind_int64(select, 2, from + 4);
                    while (raw.sqlite3_step(select) == raw.SQLITE_ROW) rowsRead++;
                    raw.sqlite3_reset(select);
                    queries++;
                    maxMs = Math.Max(maxMs, watch.ElapsedMilliseconds);
                    if (sleepMs > 0) Thread.Sleep(sleepMs);
                }

                raw.sqlite3_finalize(select);
                Bench.Close(db);
            }) { IsBackground = true };
            thread.Start();
            return (() => { stop.Set(); thread.Join(); }, () => $"reader ran {queries:N0} queries ({rowsRead:N0} rows), slowest {maxMs} ms");
        };

        Func<string, (Action Stop, Func<string> Report)> LongRead() => path =>
        {
            var stop = new ManualResetEventSlim();
            var started = new ManualResetEventSlim();
            var thread = new Thread(() =>
            {
                raw.sqlite3_open_v2(path, out var db, raw.SQLITE_OPEN_READONLY, null);
                Bench.Exec(db, "PRAGMA busy_timeout = 30000; BEGIN");
                Bench.Scalar(db, "SELECT count(*) FROM Pot WHERE \"Row\" = 1");   // takes the read snapshot and holds it
                started.Set();
                stop.Wait();
                Bench.Exec(db, "COMMIT");
                Bench.Close(db);
            }) { IsBackground = true };
            thread.Start();
            started.Wait();
            return (() => { stop.Set(); thread.Join(); }, () => "reader held ONE read transaction for the whole apply");
        };

        var cases = new (Opt Opt, Func<string, (Action Stop, Func<string> Report)>? Reader)[]
        {
            (new Opt("7a. one transaction, no reader"), null),
            (new Opt("7b. one transaction, reader looping short bbox-like SELECTs (no pause)"), ShortReads(0)),
            (new Opt("7c. one transaction, reader holding one read transaction open"), LongRead()),
            (new Opt("7d. 2,000 rows/commit, no reader", RowsPerCommit: 2000), null),
            (new Opt("7e. 2,000 rows/commit, reader looping short SELECTs (no pause)", RowsPerCommit: 2000), ShortReads(0)),
            (new Opt("7f. 2,000 rows/commit, reader holding one read transaction open (checkpoint starvation)", RowsPerCommit: 2000), LongRead())
        };
        Result? baseline = null;
        foreach (var (o, reader) in cases)
        {
            var r = Bench.RunOnBase(dir, basePath, [second, updates], o, runs, reader);
            baseline ??= r;
            say(Line(o.Name, r, baseline, rows));
        }
    }
}

