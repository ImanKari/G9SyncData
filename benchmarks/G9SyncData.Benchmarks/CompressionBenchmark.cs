using System.Diagnostics;
using System.IO.Compression;
using G9SyncData.Protocol.RowBatch;
using SQLitePCL;
using ZstdSharp;

namespace G9SyncData.Benchmarks;

/// <summary>
///     Frame compression on real sync pages: AgriPad-shaped pots (GUID text keys and references, a WKT point, timestamps, a nullable column),
///     2,000 rows per page like the server sends. For each codec: bytes on the wire, server compression time, client decompression time and
///     allocations, and the resulting time to move the pages over a few network speeds. The same rows are also written to a SQLite file and
///     compressed whole — what a server-built snapshot download (D-06) would put on the wire. Run with <c>--compression [--rows N]</c>, or on a
///     device through the Android test app (decompression is what the device pays).
/// </summary>
internal static class CompressionBenchmark
{
    internal sealed record Result(string Codec, long Bytes, double Ratio, TimeSpan Compress, TimeSpan Decompress, long DecompressAllocated);

    private sealed record Codec(string Name, Func<byte[], byte[]> Compress, Func<byte[], int, byte[]> Decompress);

    private readonly record struct PotRow(Guid Id, long Version, int Index, string Point, int Gender, Guid Ridge, DateTime Created, DateTime Updated, bool NullCreator);

    private static readonly Guid Variety = Guid.Parse("6f1e2d3c-4b5a-4978-8a6b-5c4d3e2f1a0b");
    private static readonly Guid User = Guid.Parse("0b1a2f3e-4d5c-4b6a-8978-1a2b3c4d5e6f");

    public static IReadOnlyList<Result> Run(int rows, Action<string>? log = null)
    {
        log ??= Console.WriteLine;
        var pots = Generate(rows);
        var bodies = BuildBodies(pots);
        var raw = bodies.Sum(b => (long)b.Length);
        log($"Compression benchmark: {rows:N0} rows in {bodies.Count} pages, {raw / 1048576d:N1} MB uncompressed");
        log("| Codec | Wire | Ratio | Compress (server) | Decompress (client) | Decompress allocated | 2 Mbit/s | 10 Mbit/s | 50 Mbit/s |");
        log("|---|---|---|---|---|---|---|---|---|");

        var codecs = new List<Codec>();
        foreach (var quality in new[] { 1, 2, 4, 5, 6 }) codecs.Add(new Codec($"Brotli q{quality}", body => Brotli(body, quality, 22), BrotliDecompress));
        foreach (var level in new[] { 1, 3, 6, 9, 12 }) codecs.Add(new Codec($"Zstandard {level}", body => Zstd(body, level), ZstdDecompress));
        codecs.Add(new Codec("none", body => body, (compressed, _) => compressed));

        // Layout experiment: the same values grouped by column (keys, versions, then each column), Brotli q4. Decompression here does not
        // rebuild rows, so only its size is meaningful.
        codecs.Add(new Codec("column-grouped + Brotli q4", body => Brotli(Columnar(body), 4, 22), (compressed, length) => new byte[length]));
        codecs.Add(new Codec("column-grouped + Brotli q5", body => Brotli(Columnar(body), 5, 22), (compressed, length) => new byte[length]));

        var results = new List<Result>();
        foreach (var codec in codecs) results.Add(Measure(codec, bodies, raw, log));

        // The real frames through the public writer and reader: v2 includes regrouping on both ends (ADR-0040).
        foreach (var (version, quality) in new[] { (1, 4), (2, 4), (2, 5) }) results.Add(MeasureFrames(pots, version, quality, raw, log));

        // D-06: the same rows as a server-built SQLite file (table only; the device builds indexes after copying, variant K of
        // --sqlite-apply), compressed as ONE stream with a large window. The "ratio" column is relative to the raw file here.
        var file = BuildSnapshotFile(pots);
        log($"Snapshot file: {file.Length / 1048576d:N1} MB raw (the frames above: {raw / 1048576d:N1} MB raw)");
        foreach (var codec in new[]
                 {
                     new Codec("snapshot file + Brotli q4 (window 24)", body => Brotli(body, 4, 24), BrotliDecompress),
                     new Codec("snapshot file + Brotli q5 (window 24)", body => Brotli(body, 5, 24), BrotliDecompress),
                     new Codec("snapshot file + Zstandard 3", body => Zstd(body, 3), ZstdDecompress),
                     new Codec("snapshot file + Zstandard 9", body => Zstd(body, 9), ZstdDecompress)
                 })
        {
            results.Add(Measure(codec, [file], file.Length, log));
        }

        return results;
    }

    private static Result Measure(Codec codec, List<byte[]> bodies, long raw, Action<string> log)
    {
        var compressed = new List<byte[]>(bodies.Count);
        var stopwatch = Stopwatch.StartNew();
        foreach (var body in bodies) compressed.Add(codec.Compress(body));
        var compress = stopwatch.Elapsed;

        // Warm the decompressor, then measure.
        codec.Decompress(compressed[0], bodies[0].Length);
        var allocated = GC.GetTotalAllocatedBytes(precise: true);
        stopwatch.Restart();
        for (var i = 0; i < compressed.Count; i++)
        {
            var restored = codec.Decompress(compressed[i], bodies[i].Length);
            if (restored.Length != bodies[i].Length) throw new InvalidOperationException(codec.Name + " did not restore the page.");
        }

        var decompress = stopwatch.Elapsed;
        var decompressAllocated = GC.GetTotalAllocatedBytes(precise: true) - allocated;
        var bytes = compressed.Sum(c => (long)c.Length);
        var result = new Result(codec.Name, bytes, raw / (double)bytes, compress, decompress, decompressAllocated);
        log($"| {codec.Name} | {bytes / 1048576d:N2} MB | {result.Ratio:N2} | {compress.TotalMilliseconds:N0} ms | {decompress.TotalMilliseconds:N0} ms | " +
            $"{decompressAllocated / 1048576d:N1} MB | {Transfer(result, 2):N1} s | {Transfer(result, 10):N1} s | {Transfer(result, 50):N1} s |");
        return result;
    }

    private static Result MeasureFrames(PotRow[] pots, int version, int quality, long raw, Action<string> log)
    {
        const int PageRows = 2_000;
        var name = $"G9RowBatch v{version} frame, Brotli q{quality}";
        var frames = new List<byte[]>();
        var compress = TimeSpan.Zero;
        using (var writer = new G9CRowBatchWriter())
        {
            for (var start = 0; start < pots.Length; start += PageRows)
            {
                writer.Reset();
                WritePage(writer, pots.AsSpan(start, Math.Min(PageRows, pots.Length - start)));
                var stopwatch = Stopwatch.StartNew();
                frames.Add(writer.ToFrame(G9ECompression.Brotli, quality, version));
                compress += stopwatch.Elapsed;
            }
        }

        using (G9CRowBatchReader.Open(frames[0])) { }
        var allocated = GC.GetTotalAllocatedBytes(precise: true);
        var decode = Stopwatch.StartNew();
        foreach (var frame in frames)
        {
            using var reader = G9CRowBatchReader.Open(frame);
        }

        var decompress = decode.Elapsed;
        var decompressAllocated = GC.GetTotalAllocatedBytes(precise: true) - allocated;
        var bytes = frames.Sum(f => (long)f.Length);
        var result = new Result(name, bytes, raw / (double)bytes, compress, decompress, decompressAllocated);
        log($"| {name} | {bytes / 1048576d:N2} MB | {result.Ratio:N2} | {compress.TotalMilliseconds:N0} ms | {decompress.TotalMilliseconds:N0} ms | " +
            $"{decompressAllocated / 1048576d:N1} MB | {Transfer(result, 2):N1} s | {Transfer(result, 10):N1} s | {Transfer(result, 50):N1} s |");
        return result;
    }

    /// <summary>Compression + transmission + decompression of every page, in seconds, on a link of <paramref name="megabits" />.</summary>
    public static double Transfer(Result result, double megabits) =>
        result.Compress.TotalSeconds + (result.Bytes * 8 / (megabits * 1_000_000)) + result.Decompress.TotalSeconds;

    private static PotRow[] Generate(int rows)
    {
        var pots = new PotRow[rows];
        var ridge = Guid.NewGuid();
        const int PageRows = 2_000;
        for (var start = 0; start < rows; start += PageRows)
        {
            // Keys are random but each page is in key order, as the server pages them.
            var keys = Enumerable.Range(0, Math.Min(PageRows, rows - start)).Select(_ => Guid.NewGuid()).Order().ToArray();
            for (var i = 0; i < keys.Length; i++)
            {
                var g = start + i + 1;
                if (g % 1000 == 1) ridge = Guid.NewGuid();
                pots[start + i] = new PotRow(keys[i], g, g, FormattableString.Invariant($"POINT({51.3 + (g % 1000) / 100000.0} {35.7 + g / 10000000.0})"),
                    g % 3, ridge, new DateTime(2024, 1, 1).AddMinutes(g % 500000), new DateTime(2025, 6, 1).AddSeconds(g), g % 4 == 0);
            }
        }

        return pots;
    }

    private static List<byte[]> BuildBodies(PotRow[] pots)
    {
        const int PageRows = 2_000;
        var bodies = new List<byte[]>();
        using var writer = new G9CRowBatchWriter();
        for (var start = 0; start < pots.Length; start += PageRows)
        {
            writer.Reset();
            WritePage(writer, pots.AsSpan(start, Math.Min(PageRows, pots.Length - start)));
            var frame = writer.ToFrame(G9ECompression.None);
            bodies.Add(frame[10..]);   // the 10-byte G9RowBatch header: magic, version, compression, body length
        }

        return bodies;
    }

    private static readonly G9ESyncColumnType[] PotTypes =
    [
        G9ESyncColumnType.Int32, G9ESyncColumnType.String, G9ESyncColumnType.Int32, G9ESyncColumnType.Guid, G9ESyncColumnType.Guid,
        G9ESyncColumnType.DateTime, G9ESyncColumnType.DateTime, G9ESyncColumnType.Guid, G9ESyncColumnType.Guid
    ];

    private static void WritePage(G9CRowBatchWriter writer, ReadOnlySpan<PotRow> page)
    {
        writer.BeginSection(G9ERowBatchOp.Upsert, 2, G9ESyncColumnType.Guid, PotTypes);
        {
            foreach (var pot in page)
            {
                writer.BeginRow(pot.Id, pot.Version);
                writer.WriteInt32(pot.Index);
                writer.WriteString(pot.Point);
                writer.WriteInt32(pot.Gender);
                writer.WriteGuid(pot.Ridge);
                writer.WriteGuid(Variety);
                writer.WriteDateTime(pot.Created);
                writer.WriteDateTime(pot.Updated);
                writer.WriteGuid(User);
                if (pot.NullCreator) writer.WriteNull();
                else writer.WriteGuid(User);
                writer.EndRow();
            }

            writer.EndSection();
        }
    }

    /// <summary>
    ///     The rows as the device stores them (sqlite-net shapes: GUID and date text, WKT text), plus the version column a snapshot would
    ///     carry, in key order, without secondary indexes. Built in a temporary file and read back whole.
    /// </summary>
    private static byte[] BuildSnapshotFile(PotRow[] pots)
    {
        var path = Path.Combine(Path.GetTempPath(), $"g9sync-snapshot-{Guid.NewGuid():N}.db");
        try
        {
            if (raw.sqlite3_open(path, out var db) != raw.SQLITE_OK) throw new InvalidOperationException("Cannot create " + path);
            using (db)
            {
                Exec(db, """
                    PRAGMA journal_mode = OFF; PRAGMA synchronous = OFF; PRAGMA page_size = 4096;
                    CREATE TABLE Pot (Id TEXT NOT NULL, "Index" INTEGER NOT NULL, Point TEXT, Gender INTEGER NOT NULL, RidgeId TEXT NOT NULL,
                        VarietyId TEXT NOT NULL, CreatedTime TEXT NOT NULL, UpdatedTime TEXT NOT NULL, UpdatedBy TEXT NOT NULL, CreatedBy TEXT, _ver INTEGER NOT NULL);
                    BEGIN;
                    """);
                if (raw.sqlite3_prepare_v2(db, "INSERT INTO Pot VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11)", out var insert) != raw.SQLITE_OK)
                    throw new InvalidOperationException(raw.sqlite3_errmsg(db).utf8_to_string());
                using (insert)
                {
                    var variety = Variety.ToString("D");
                    var user = User.ToString("D");
                    foreach (var pot in pots)
                    {
                        raw.sqlite3_bind_text(insert, 1, pot.Id.ToString("D"));
                        raw.sqlite3_bind_int64(insert, 2, pot.Index);
                        raw.sqlite3_bind_text(insert, 3, pot.Point);
                        raw.sqlite3_bind_int64(insert, 4, pot.Gender);
                        raw.sqlite3_bind_text(insert, 5, pot.Ridge.ToString("D"));
                        raw.sqlite3_bind_text(insert, 6, variety);
                        raw.sqlite3_bind_text(insert, 7, pot.Created.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", System.Globalization.CultureInfo.InvariantCulture));
                        raw.sqlite3_bind_text(insert, 8, pot.Updated.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", System.Globalization.CultureInfo.InvariantCulture));
                        raw.sqlite3_bind_text(insert, 9, user);
                        if (pot.NullCreator) raw.sqlite3_bind_null(insert, 10);
                        else raw.sqlite3_bind_text(insert, 10, user);
                        raw.sqlite3_bind_int64(insert, 11, pot.Version);
                        if (raw.sqlite3_step(insert) != raw.SQLITE_DONE) throw new InvalidOperationException(raw.sqlite3_errmsg(db).utf8_to_string());
                        raw.sqlite3_reset(insert);
                    }
                }

                Exec(db, "COMMIT");
                raw.sqlite3_close_v2(db);
            }

            return File.ReadAllBytes(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void Exec(sqlite3 db, string sql)
    {
        if (raw.sqlite3_exec(db, sql, null, null, out var error) != raw.SQLITE_OK) throw new InvalidOperationException(error);
    }

    /// <summary>The page's rows rewritten column by column: keys, versions, each column's null flags and values, in that order.</summary>
    private static byte[] Columnar(byte[] body)
    {
        var frame = new byte[body.Length + 10];
        "G9RB"u8.CopyTo(frame);
        frame[4] = 1;
        frame[5] = 0;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(6), body.Length);
        body.CopyTo(frame, 10);
        using var reader = G9CRowBatchReader.Open(frame);
        var keys = new MemoryStream();
        var versions = new MemoryStream();
        MemoryStream[]? columns = null;
        while (reader.NextSection())
        {
            columns ??= Enumerable.Range(0, reader.ColumnCount).Select(_ => new MemoryStream()).ToArray();
            while (reader.ReadRow())
            {
                keys.Write(reader.KeyGuid.ToByteArray(bigEndian: true));
                versions.Write(BitConverter.GetBytes(reader.Version));
                for (var c = 0; c < reader.ColumnCount; c++)
                {
                    if (reader.IsNull(c))
                    {
                        columns[c].WriteByte(0);
                        continue;
                    }

                    columns[c].WriteByte(1);
                    switch (reader.ColumnTypes[c])
                    {
                        case G9ESyncColumnType.Int32: columns[c].Write(BitConverter.GetBytes(reader.GetInt32(c))); break;
                        case G9ESyncColumnType.String: columns[c].Write(reader.GetUtf8(c)); columns[c].WriteByte(0); break;
                        case G9ESyncColumnType.Guid: columns[c].Write(reader.GetGuid(c).ToByteArray(bigEndian: true)); break;
                        case G9ESyncColumnType.DateTime: columns[c].Write(BitConverter.GetBytes(reader.GetDateTime(c).Ticks)); break;
                    }
                }
            }
        }

        var all = new MemoryStream();
        keys.WriteTo(all);
        versions.WriteTo(all);
        foreach (var column in columns ?? []) column.WriteTo(all);
        return all.ToArray();
    }

    private static byte[] Brotli(byte[] body, int quality, int window)
    {
        var buffer = new byte[BrotliEncoder.GetMaxCompressedLength(body.Length)];
        if (!BrotliEncoder.TryCompress(body, buffer, out var written, quality, window)) throw new InvalidOperationException("Brotli failed.");
        return buffer[..written];
    }

    private static byte[] BrotliDecompress(byte[] compressed, int length)
    {
        var body = new byte[length];
        if (!BrotliDecoder.TryDecompress(compressed, body, out var written) || written != length) throw new InvalidOperationException("Brotli failed.");
        return body;
    }

    private static byte[] Zstd(byte[] body, int level)
    {
        using var compressor = new Compressor(level);
        return compressor.Wrap(body).ToArray();
    }

    [ThreadStatic] private static Decompressor? _decompressor;

    private static byte[] ZstdDecompress(byte[] compressed, int length)
    {
        _decompressor ??= new Decompressor();
        var body = new byte[length];
        var written = _decompressor.Unwrap(compressed, body);
        if (written != length) throw new InvalidOperationException("Zstandard failed.");
        return body;
    }
}
