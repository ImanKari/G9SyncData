using System.Diagnostics;
using System.IO.Compression;
using G9SyncData.Client.Sqlite.SnapshotFiles;
using G9SyncData.Protocol;
using G9SyncData.Protocol.RowBatch;
using SQLitePCL;

namespace G9SyncData.Benchmarks;

/// <summary>
///     What a snapshot file (ADR-0072) puts on the wire against the rows it replaces: the same AgriPad-shaped pots as frames (layout 2,
///     Brotli q4 - what the server streams) and as the file the server builds with the real <see cref="G9CSnapshotFileWriter" />, in two
///     version-table layouts (keyed by the row's key text, keyed by the data table's rowid) and through several segment codecs. A file
///     is built once and served to every device of its scope, so the server can afford a slower, smaller codec than a frame streamed
///     once. Run with <c>--snapshot-file-codec [--rows N]</c>.
/// </summary>
internal static class SnapshotFileCodecBenchmark
{
    private sealed record Codec(string Name, int Quality, int Window, int SegmentBytes);

    public static void Run(int rows, Action<string>? log = null)
    {
        log ??= Console.WriteLine;
        var pots = CompressionBenchmark.Generate(rows);
        var frames = CompressionBenchmark.Frames(pots, version: 2, quality: 4);
        var frameBytes = frames.Sum(f => (long)f.Length);
        log($"Snapshot file codecs: {rows:N0} AgriPad-shaped pots");
        log($"Rows on the wire (G9RowBatch layout 2, Brotli q4, 2,000 rows per frame): {frameBytes / 1048576d:N2} MB");

        var directory = Path.Combine(Path.GetTempPath(), "g9sync-file-codec-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var byRowid = Path.Combine(directory, "by-rowid.sqlite");
            var stopwatch = Stopwatch.StartNew();
            using (var writer = new G9CSnapshotFileWriter(byRowid, new G9DtSnapshotFileStorage()))
            {
                writer.AddTable("farm.Pot", Shape, FrameColumns);
                foreach (var frame in frames) writer.WriteFrame(frame);
                writer.Complete(verify: false);
            }

            log($"File built by the real writer in {stopwatch.ElapsedMilliseconds:N0} ms");
            // The layout before 2026-10-12 for comparison: the version table keyed by the row's key text (a third copy of every key).
            var byKey = Path.Combine(directory, "by-key.sqlite");
            File.Copy(byRowid, byKey);
            Exec(byKey, """
                PRAGMA journal_mode = OFF;
                CREATE TABLE g9snap_key_2 (pk TEXT NOT NULL PRIMARY KEY, ver INTEGER NOT NULL) WITHOUT ROWID;
                INSERT INTO g9snap_key_2 (pk, ver) SELECT lower(p.Id), v.ver FROM Pot AS p JOIN g9snap_ver_2 AS v ON v.rid = p.rowid;
                DROP TABLE g9snap_ver_2;
                VACUUM;
                """);

            // The floor: the device's table and indexes only, no versions at all.
            var bare = Path.Combine(directory, "bare.sqlite");
            File.Copy(byRowid, bare);
            Exec(bare, "PRAGMA journal_mode = OFF; DROP TABLE g9snap_ver_2; VACUUM;");

            // Index pages are derivable: what does the device pay to build them instead of receiving them?
            var tableSql = ScalarText(byRowid, "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'Pot'");
            var indexSql = ScalarText(byRowid, "SELECT sql FROM sqlite_master WHERE type = 'index' AND name = 'IX_Pot_RidgeId'");
            var noSecondary = Path.Combine(directory, "no-secondary.sqlite");
            File.Copy(byRowid, noSecondary);
            Exec(noSecondary, "PRAGMA journal_mode = OFF; DROP INDEX IX_Pot_RidgeId; VACUUM;");
            var noIndexes = Path.Combine(directory, "no-indexes.sqlite");
            Exec(noIndexes, $"""
                PRAGMA journal_mode = OFF; PRAGMA page_size = 4096;
                {tableSql.Replace(" PRIMARY KEY", string.Empty, StringComparison.Ordinal)};
                CREATE TABLE g9snap_ver_2 (rid INTEGER PRIMARY KEY, ver INTEGER NOT NULL);
                ATTACH DATABASE '{byRowid.Replace("'", "''", StringComparison.Ordinal)}' AS a;
                BEGIN;
                INSERT INTO Pot SELECT * FROM a.Pot ORDER BY rowid;
                INSERT INTO g9snap_ver_2 SELECT rid, ver FROM a.g9snap_ver_2;
                COMMIT;
                DETACH DATABASE a;
                """);

            log("");
            log("Copy into the device's empty table on this machine (one transaction, WAL, synchronous NORMAL; best of 3):");
            log("| File | Device work | Copy + index build |");
            log("|---|---|---|");
            log($"| table + key index + IX_Pot_RidgeId (the format) | transfer optimisation copies all three b-trees | {CopyMs(byRowid, tableSql, indexSql, dropIndex: false):N0} ms |");
            log($"| table + key index | transfer copy, then CREATE INDEX IX_Pot_RidgeId | {CopyMs(noSecondary, tableSql, indexSql, dropIndex: true):N0} ms |");
            log($"| table only (no PRIMARY KEY in the file) | row-by-row INSERT … SELECT in key order (key index appended), then CREATE INDEX | {CopyMs(noIndexes, tableSql, indexSql, dropIndex: true):N0} ms |");

            foreach (var (label, path) in new[]
                     {
                         ("version table by rowid (the format)", byRowid), ("version table by key (before 2026-10-12)", byKey), ("no version table (the floor)", bare),
                         ("the format without IX_Pot_RidgeId", noSecondary), ("the format without any index pages", noIndexes)
                     })
            {
                var bytes = File.ReadAllBytes(path);
                log("");
                log($"### {label}: {bytes.Length / 1048576d:N2} MB raw — {BtreeSizes(path)}");
                log($"| Codec | Wire | vs rows | Compress (server, {Environment.ProcessorCount} threads) | Decompress (client, 1 thread) |");
                log("|---|---|---|---|---|");
                foreach (var codec in Codecs)
                {
                    var (wire, compress, decompress) = Measure(bytes, codec);
                    log($"| {codec.Name} | {wire / 1048576d:N2} MB | {wire / (double)frameBytes:P0} | {compress.TotalMilliseconds:N0} ms | {decompress.TotalMilliseconds:N0} ms |");
                }
            }
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // a late handle; the temp folder is disposable
            }
        }
    }

    private static readonly Codec[] Codecs =
    [
        new("Brotli q5, window 22, 1 MiB segments (the 1.2.2 default)", 5, 22, 1 << 20),
        new("Brotli q11, window 22, 1 MiB segments", 11, 22, 1 << 20),
        new("Brotli q11, window 24, 4 MiB segments", 11, 24, 4 << 20)
    ];

    private static readonly G9DtColumnModel[] FrameColumns =
    [
        new() { Name = "Index", Type = G9ESyncColumnType.Int32, Nullable = false },
        new() { Name = "Point", Type = G9ESyncColumnType.String, Nullable = true },
        new() { Name = "Gender", Type = G9ESyncColumnType.Int32, Nullable = false },
        new() { Name = "RidgeId", Type = G9ESyncColumnType.Guid, Nullable = false },
        new() { Name = "VarietyId", Type = G9ESyncColumnType.Guid, Nullable = false },
        new() { Name = "CreatedTime", Type = G9ESyncColumnType.DateTime, Nullable = false },
        new() { Name = "UpdatedTime", Type = G9ESyncColumnType.DateTime, Nullable = false },
        new() { Name = "UpdatedBy", Type = G9ESyncColumnType.Guid, Nullable = false },
        new() { Name = "CreatedBy", Type = G9ESyncColumnType.Guid, Nullable = true }
    ];

    private static readonly G9DtSnapshotFileTable Shape = new()
    {
        TableId = 2, LocalName = "Pot", KeyColumn = "Id", KeyType = G9ESyncColumnType.Guid, Columns = [.. FrameColumns],
        Indexes = [new G9DtSnapshotFileIndex { Name = "IX_Pot_RidgeId", Columns = ["RidgeId"] }]
    };

    private static (long Wire, TimeSpan Compress, TimeSpan Decompress) Measure(byte[] file, Codec codec)
    {
        // Segments are independent: the server compresses them in parallel (G9CSnapshotFileOptions.CompressionParallelism).
        var count = (file.Length + codec.SegmentBytes - 1) / codec.SegmentBytes;
        var segments = new (byte[] Data, int Length)[count];
        var stopwatch = Stopwatch.StartNew();
        Parallel.For(0, count, i =>
        {
            var offset = i * codec.SegmentBytes;
            var length = Math.Min(codec.SegmentBytes, file.Length - offset);
            var buffer = new byte[BrotliEncoder.GetMaxCompressedLength(length)];
            if (!BrotliEncoder.TryCompress(file.AsSpan(offset, length), buffer, out var written, codec.Quality, codec.Window))
                throw new InvalidOperationException("Brotli failed.");
            segments[i] = (buffer[..written], length);
        });

        var compress = stopwatch.Elapsed;
        var restored = new byte[codec.SegmentBytes];
        stopwatch.Restart();
        foreach (var (data, length) in segments)
        {
            if (!BrotliDecoder.TryDecompress(data, restored.AsSpan(0, length), out var written) || written != length) throw new InvalidOperationException("Brotli failed.");
        }

        return (segments.Sum(s => (long)s.Data.Length), compress, stopwatch.Elapsed);
    }

    /// <summary>A copy of the file into an empty device table, as the store does it; the best of three, in milliseconds.</summary>
    private static double CopyMs(string file, string tableSql, string indexSql, bool dropIndex)
    {
        var best = double.MaxValue;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var device = Path.Combine(Path.GetDirectoryName(file)!, $"device-{Guid.NewGuid():N}.db");
            try
            {
                Exec(device, $"PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; {tableSql}; {indexSql};");
                if (raw.sqlite3_open(device, out var db) != raw.SQLITE_OK) throw new InvalidOperationException("Cannot open " + device);
                using (db)
                {
                    Run(db, $"PRAGMA synchronous = NORMAL; ATTACH DATABASE '{file.Replace("'", "''", StringComparison.Ordinal)}' AS snap");
                    var stopwatch = Stopwatch.StartNew();
                    Run(db, "BEGIN IMMEDIATE");
                    if (dropIndex) Run(db, "DROP INDEX IX_Pot_RidgeId");
                    Run(db, "INSERT INTO main.Pot SELECT * FROM snap.Pot");
                    if (dropIndex) Run(db, indexSql);
                    Run(db, "COMMIT");
                    best = Math.Min(best, stopwatch.Elapsed.TotalMilliseconds);
                    Run(db, "DETACH DATABASE snap");
                    raw.sqlite3_close_v2(db);
                }
            }
            finally
            {
                foreach (var leftover in new[] { device, device + "-wal", device + "-shm" })
                {
                    if (File.Exists(leftover)) File.Delete(leftover);
                }
            }
        }

        return best;
    }

    private static void Run(sqlite3 db, string sql)
    {
        if (raw.sqlite3_exec(db, sql, null, null, out var error) != raw.SQLITE_OK) throw new InvalidOperationException(error + " — " + sql);
    }

    private static string ScalarText(string path, string sql)
    {
        if (raw.sqlite3_open_v2(path, out var db, raw.SQLITE_OPEN_READONLY, null) != raw.SQLITE_OK) throw new InvalidOperationException("Cannot open " + path);
        using (db)
        {
            if (raw.sqlite3_prepare_v2(db, sql, out var statement) != raw.SQLITE_OK) throw new InvalidOperationException(raw.sqlite3_errmsg(db).utf8_to_string());
            using (statement)
            {
                return raw.sqlite3_step(statement) == raw.SQLITE_ROW ? raw.sqlite3_column_text(statement, 0).utf8_to_string() : throw new InvalidOperationException("No row: " + sql);
            }
        }
    }

    /// <summary>Bytes per b-tree (dbstat), largest first; "dbstat unavailable" when the SQLite build lacks it.</summary>
    private static string BtreeSizes(string path)
    {
        if (raw.sqlite3_open_v2(path, out var db, raw.SQLITE_OPEN_READONLY, null) != raw.SQLITE_OK) return "cannot open";
        using (db)
        {
            if (raw.sqlite3_prepare_v2(db, "SELECT name, sum(pgsize) FROM dbstat GROUP BY name ORDER BY 2 DESC", out var statement) != raw.SQLITE_OK)
                return "dbstat unavailable";
            using (statement)
            {
                var parts = new List<string>();
                while (raw.sqlite3_step(statement) == raw.SQLITE_ROW)
                    parts.Add($"{raw.sqlite3_column_text(statement, 0).utf8_to_string()} {raw.sqlite3_column_int64(statement, 1) / 1048576d:N2} MB");
                return string.Join(", ", parts);
            }
        }
    }

    private static void Exec(string path, string sql)
    {
        if (raw.sqlite3_open(path, out var db) != raw.SQLITE_OK) throw new InvalidOperationException("Cannot open " + path);
        using (db)
        {
            if (raw.sqlite3_exec(db, sql, null, null, out var error) != raw.SQLITE_OK) throw new InvalidOperationException(error);
        }
    }
}
