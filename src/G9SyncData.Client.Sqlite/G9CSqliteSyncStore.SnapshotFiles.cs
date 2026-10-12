using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using G9SyncData.Client.Sqlite.Internal;
using G9SyncData.Client.Sqlite.Native;
using G9SyncData.Client.Sqlite.SnapshotFiles;
using G9SyncData.Client.Storage;
using G9SyncData.Protocol;
using Microsoft.Extensions.Logging;

namespace G9SyncData.Client.Sqlite;

/// <summary>
///     Snapshot files (ADR-0072): a large first download arrives as a SQLite file built on the server with this store's own DDL, is staged
///     segment by segment beside the database, and is applied by copying its tables in SQL - one statement per table, the transfer
///     optimisation when the table is empty - instead of one upsert per row.
/// </summary>
public sealed partial class G9CSqliteSyncStore
{
    /// <summary>The most files one plan may carry: each is ATTACHed during the apply, and SQLite attaches at most 10 databases.</summary>
    private const int MaxSnapshotFilesPerPlan = 8;

    /// <summary>Segments written since the last durable resume point, per staged file (under the write lock).</summary>
    private readonly Dictionary<(string RunId, string FileId), PendingSegments> _pendingSegments = new();

    /// <summary>
    ///     The device's offer for <see cref="G9DtPullRequest.SnapshotFiles" />: its storage forms and, for every table it receives, the shape
    ///     it creates the table with. Null when snapshot files are switched off or nothing is downloadable.
    /// </summary>
    public G9DtSnapshotFileRequest? DescribeSnapshotFiles(IReadOnlyList<G9DtNegotiatedTable> negotiated)
    {
        ArgumentNullException.ThrowIfNull(negotiated);
        if (!_options.AcceptSnapshotFiles || _model is null) return null;
        var tables = new List<G9DtSnapshotFileTable>();
        foreach (var table in negotiated)
        {
            if (table.Direction == G9ESyncDirection.UploadOnly) continue;
            var local = FindByServerName(table.Name);
            if (local is not null) tables.Add(G9CSnapshotFileShapes.ShapeOf(local.Model, table.TableId));
        }

        return tables.Count == 0
            ? null
            : new G9DtSnapshotFileRequest
            {
                FormatVersion = G9SyncProtocol.SnapshotFileFormatVersion,
                Storage = G9CSnapshotFileShapes.StorageOf(_options),
                Tables = tables,
                MinRows = _options.SnapshotFileMinRows,
                MaxBytes = _options.SnapshotFileMaxBytes,
                MaxFiles = MaxSnapshotFilesPerPlan
            };
    }

    /// <summary>Where staged snapshot files live.</summary>
    internal string SnapshotFileDirectory => _options.SnapshotFileDirectory ?? SqliteConnection.DataSourceOf(_connectionString) + ".g9files";

    // ================================================================ staging

    /// <summary>
    ///     Writes one segment of a snapshot file to its place in the staged file. The segment decompresses on its own and is checked against
    ///     its SHA-256 BEFORE it touches the file. Progress becomes durable only at checkpoints - every
    ///     <see cref="G9CSqliteSyncStoreOptions.SnapshotFileCheckpointBytes" /> and at the last segment - and in that order: the file is
    ///     flushed to disk first, then the stage row records the resume point. A crash can therefore lose segments written after the last
    ///     checkpoint (they are downloaded again), but never leave a resume point past data that is not on disk.
    /// </summary>
    private void StageFileSegment(SqliteConnection connection, string runId, G9DtSyncChunk chunk)
    {
        var segment = chunk.File!;
        var key = (runId, segment.FileId);
        var staged = ReadStagedFile(connection, null, runId, segment.FileId);
        if (segment.Manifest is { } manifest)
        {
            ValidateManifest(manifest, segment.FileId);
            if (staged is null || !string.Equals(staged.Manifest.Sha256, manifest.Sha256, StringComparison.Ordinal))
            {
                // A new file - or, after a resume that reached another server instance, a different build of it: the segments already on
                // disk belong to the other build, so the file starts over.
                var directory = SnapshotFileDirectory;
                Directory.CreateDirectory(directory);
                var path = staged?.Path ?? Path.Combine(directory, runId + "-" + SafeFileName(segment.FileId) + ".sqlite");
                using (var handle = File.OpenHandle(path, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    RandomAccess.SetLength(handle, manifest.Bytes);
                }

                Exec(connection, null,
                    "INSERT INTO g9_stage_files (run_id, file_id, path, manifest_json, subscription_key, first_seq, segments_done, bytes_received) " +
                    "VALUES ($r, $f, $p, $m, $k, $s, 0, 0) ON CONFLICT(run_id, file_id) DO UPDATE SET manifest_json = excluded.manifest_json, " +
                    "segments_done = 0, bytes_received = 0",
                    ("$r", runId), ("$f", segment.FileId), ("$p", path),
                    ("$m", JsonSerializer.Serialize(manifest, G9SyncJsonContext.Default.G9DtSnapshotFileManifest)),
                    ("$k", chunk.SubscriptionKey), ("$s", chunk.Sequence));
                _pendingSegments.Remove(key);
                staged = ReadStagedFile(connection, null, runId, segment.FileId);
            }
        }

        if (staged is null) throw Corrupt($"Segment {segment.Index} of snapshot file {segment.FileId} arrived without its manifest.");
        var layout = staged.Manifest;
        var pending = _pendingSegments.TryGetValue(key, out var found) ? found : _pendingSegments[key] = new PendingSegments();
        var expected = staged.SegmentsDone + pending.Segments;
        if (segment.Index != expected) throw Corrupt($"Snapshot file {segment.FileId}: segment {segment.Index} arrived where {expected} was expected.");
        if (segment.Offset != (long)segment.Index * layout.SegmentBytes || segment.Length <= 0 || segment.Length > layout.SegmentBytes
            || segment.Offset + segment.Length > layout.Bytes || segment.Index >= layout.Segments)
        {
            throw Corrupt($"Snapshot file {segment.FileId}: segment {segment.Index} does not fit the manifest.");
        }

        var payload = chunk.Payload ?? throw Corrupt($"Snapshot file {segment.FileId}: segment {segment.Index} has no payload.");
        var raw = ArrayPool<byte>.Shared.Rent(segment.Length);
        try
        {
            var body = raw.AsSpan(0, segment.Length);
            if (!BrotliDecoder.TryDecompress(payload, body, out var written) || written != segment.Length)
            {
                throw Corrupt($"Snapshot file {segment.FileId}: segment {segment.Index} does not decompress to {segment.Length} bytes.");
            }

            Span<byte> hash = stackalloc byte[32];
            SHA256.HashData(body, hash);
            if (!string.Equals(Convert.ToHexStringLower(hash), segment.Sha256, StringComparison.Ordinal))
            {
                throw Corrupt($"Snapshot file {segment.FileId}: segment {segment.Index} fails its SHA-256.");
            }

            var last = segment.Index == layout.Segments - 1;
            using var file = new FileStream(staged.Path, FileMode.Open, FileAccess.Write, FileShare.None, bufferSize: 0);
            RandomAccess.Write(file.SafeFileHandle, body, segment.Offset);
            pending.Segments++;
            pending.Rows += chunk.RowCount;
            pending.Bytes += payload.Length;
            pending.Unsynced += segment.Length;
            if (!last && pending.Unsynced < Math.Max(1, _options.SnapshotFileCheckpointBytes)) return;

            // The checkpoint: data first, then the record that it is there.
            file.Flush(flushToDisk: true);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(raw);
        }

        using var transaction = connection.BeginTransaction(deferred: false);
        Exec(connection, transaction,
            "INSERT OR REPLACE INTO g9_stage (run_id, seq, subscription_key, row_count, continuation_json, payload) VALUES ($r, $s, $k, $n, $c, NULL)",
            ("$r", runId), ("$s", chunk.Sequence), ("$k", chunk.SubscriptionKey), ("$n", pending.Rows),
            ("$c", chunk.Continuation is null ? null : JsonSerializer.Serialize(chunk.Continuation, G9SyncJsonContext.Default.G9DtPullContinuation)));
        Exec(connection, transaction,
            "UPDATE g9_stage_files SET segments_done = segments_done + $n, bytes_received = bytes_received + $b WHERE run_id = $r AND file_id = $f",
            ("$n", pending.Segments), ("$b", pending.Bytes), ("$r", runId), ("$f", segment.FileId));
        transaction.Commit();
        _pendingSegments.Remove(key);
    }

    private static void ValidateManifest(G9DtSnapshotFileManifest manifest, string fileId)
    {
        if (!string.Equals(manifest.FileId, fileId, StringComparison.Ordinal)) throw Corrupt($"Snapshot file {fileId} came with the manifest of {manifest.FileId}.");
        if (manifest.FormatVersion is < 1 or > G9SyncProtocol.SnapshotFileFormatVersion)
        {
            throw Corrupt($"Snapshot file {fileId} has format {manifest.FormatVersion}; this build reads up to {G9SyncProtocol.SnapshotFileFormatVersion}.");
        }

        if (!string.Equals(manifest.Compression, "br", StringComparison.Ordinal)) throw Corrupt($"Snapshot file {fileId} uses the unknown codec '{manifest.Compression}'.");
        if (manifest.Bytes <= 0 || manifest.SegmentBytes <= 0 || manifest.Segments <= 0
            || (long)manifest.Segments * manifest.SegmentBytes < manifest.Bytes || (long)(manifest.Segments - 1) * manifest.SegmentBytes >= manifest.Bytes
            || manifest.Sha256.Length != 64)
        {
            throw Corrupt($"Snapshot file {fileId} has an inconsistent manifest.");
        }
    }

    /// <summary>Forgets the segments written since the last checkpoint of a run: a resumed stream starts at the checkpoint.</summary>
    private void ForgetPendingSegments(string runId)
    {
        foreach (var key in _pendingSegments.Keys.Where(k => string.Equals(k.RunId, runId, StringComparison.Ordinal)).ToList()) _pendingSegments.Remove(key);
    }

    private static StagedFile? ReadStagedFile(SqliteConnection connection, SqliteTransaction? transaction, string runId, string fileId)
    {
        using var command = Command(connection, transaction,
            "SELECT file_id, path, manifest_json, subscription_key, first_seq, segments_done FROM g9_stage_files WHERE run_id = $r AND file_id = $f");
        command.Parameters.AddWithValue("$r", runId);
        command.Parameters.AddWithValue("$f", fileId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadStagedFileRow(reader) : null;
    }

    private static List<StagedFile> ReadStagedFiles(SqliteConnection connection, SqliteTransaction? transaction, string runId)
    {
        var files = new List<StagedFile>();
        using var command = Command(connection, transaction,
            "SELECT file_id, path, manifest_json, subscription_key, first_seq, segments_done FROM g9_stage_files WHERE run_id = $r ORDER BY first_seq");
        command.Parameters.AddWithValue("$r", runId);
        using var reader = command.ExecuteReader();
        while (reader.Read()) files.Add(ReadStagedFileRow(reader));
        return files;
    }

    private static StagedFile ReadStagedFileRow(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1),
        JsonSerializer.Deserialize(reader.GetString(2), G9SyncJsonContext.Default.G9DtSnapshotFileManifest)
        ?? throw Corrupt("A staged snapshot file has no manifest."),
        reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetInt64(4), reader.GetInt64(5));

    /// <summary>
    ///     The staged files of the runs a statement is about to delete: their rows go in the same transaction, the files after it commits
    ///     (<see cref="DeleteFiles" />). A file that survives a crash in between is swept at the next initialisation.
    /// </summary>
    private static List<string> TakeStagedFiles(SqliteConnection connection, SqliteTransaction transaction, string runFilterSql, params (string Name, object? Value)[] parameters)
    {
        var paths = new List<string>();
        using (var command = Command(connection, transaction, "SELECT path FROM g9_stage_files WHERE run_id IN (" + runFilterSql + ")"))
        {
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            using var reader = command.ExecuteReader();
            while (reader.Read()) paths.Add(reader.GetString(0));
        }

        Exec(connection, transaction, "DELETE FROM g9_stage_files WHERE run_id IN (" + runFilterSql + ")", parameters);
        return paths;
    }

    private void DeleteFiles(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException exception)
            {
                LogSnapshotFileNotDeleted(_logger, path, exception.Message);
            }
            catch (UnauthorizedAccessException exception)
            {
                LogSnapshotFileNotDeleted(_logger, path, exception.Message);
            }
        }
    }

    /// <summary>Deletes staged files no stage refers to any more (a crash between a stage's commit and its files' deletion).</summary>
    private void SweepOrphanSnapshotFiles(SqliteConnection connection)
    {
        var directory = SnapshotFileDirectory;
        if (!Directory.Exists(directory)) return;
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var command = Command(connection, null, "SELECT path FROM g9_stage_files"))
        {
            using var reader = command.ExecuteReader();
            while (reader.Read()) referenced.Add(Path.GetFullPath(reader.GetString(0)));
        }

        // Only names this store writes (<run id>-<file id>.sqlite and SQLite's companions of it): the directory may be shared with other
        // files - even the database itself - and a sweep must never touch what it did not create.
        DeleteFiles(Directory.EnumerateFiles(directory)
            .Where(path => StagedFileOf(Path.GetFileName(path)) is { } main && !referenced.Contains(Path.GetFullPath(Path.Combine(directory, main))))
            .ToList());
    }

    /// <summary>
    ///     The staged file a name belongs to - <c>&lt;32 hex run id&gt;-&lt;file id&gt;.sqlite</c> itself, or its <c>-journal</c>,
    ///     <c>-wal</c> or <c>-shm</c> companion - or null for any other name.
    /// </summary>
    internal static string? StagedFileOf(string name)
    {
        var main = name;
        foreach (var suffix in (ReadOnlySpan<string>)["-journal", "-wal", "-shm"])
        {
            if (!name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
            main = name[..^suffix.Length];
            break;
        }

        const string extension = ".sqlite";
        if (!main.EndsWith(extension, StringComparison.OrdinalIgnoreCase) || main.Length < 32 + 1 + 1 + extension.Length || main[32] != '-') return null;
        for (var i = 0; i < 32; i++)
        {
            if (!char.IsAsciiHexDigit(main[i])) return null;
        }

        for (var i = 33; i < main.Length - extension.Length; i++)
        {
            if (!char.IsAsciiLetterOrDigit(main[i]) && main[i] != '_') return null;
        }

        return main;
    }

    // ================================================================ apply

    /// <summary>
    ///     Checks a fully downloaded file before it is attached: every segment arrived, the size is the manifest's, and it is a snapshot
    ///     file (the SQLite header and the "G9SF" application id). Each segment was already checked against its SHA-256 when it was written.
    /// </summary>
    private static void VerifyStagedFile(StagedFile file)
    {
        var manifest = file.Manifest;
        if (file.SegmentsDone != manifest.Segments) throw Corrupt($"Snapshot file {file.FileId} has {file.SegmentsDone} of {manifest.Segments} segments.");
        var info = new FileInfo(file.Path);
        if (!info.Exists || info.Length != manifest.Bytes) throw Corrupt($"Snapshot file {file.FileId} is missing or not {manifest.Bytes} bytes.");

        Span<byte> header = stackalloc byte[100];
        using (var handle = File.OpenHandle(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (RandomAccess.Read(handle, header, 0) != header.Length) throw Corrupt($"Snapshot file {file.FileId} has no SQLite header.");
        }

        if (!header[..16].SequenceEqual("SQLite format 3\0"u8)
            || BinaryPrimitives.ReadInt32BigEndian(header.Slice(68, 4)) != G9CSnapshotFileWriter.ApplicationId)
        {
            throw Corrupt($"Snapshot file {file.FileId} is not a G9SyncData snapshot file.");
        }
    }

    /// <summary>
    ///     Copies one attached snapshot file into the database, table by table, inside the apply's transaction. Per table:
    ///     <list type="bullet">
    ///         <item><b>copy</b> - the table is empty and nothing of it is protected: <c>INSERT INTO t SELECT * FROM file.t</c>. With identical
    ///         DDL and trigger execution off, SQLite moves raw records and index b-trees (the transfer optimisation): no value is decoded,
    ///         no index is built. Indexes the file cannot supply are dropped first and rebuilt at the end of the apply.</item>
    ///         <item><b>columns</b> - the same, by column name, when the local table's columns differ from the file's.</item>
    ///         <item><b>merge</b> - the table holds rows, or rows of it are protected (pending changes, commands, rows newer than the stage):
    ///         only rows that are new or differ are written, protected rows only gain membership - the per-row rules, set-based.</item>
    ///     </list>
    ///     Metadata follows set-based from the file's version table: one <c>g9_rows</c> statement per table, each written row numbered on
    ///     the store's clock so the change feed sees it exactly as it would have seen the rows arriving one by one.
    /// </summary>
    private void ApplySnapshotFile(ApplyContext context, string schema, StagedFile file)
    {
        var started = Stopwatch.GetTimestamp();
        var slot = -1;
        if (file.SubscriptionKey is not null && context.Subscriptions.TryGetValue(file.SubscriptionKey, out var subscription))
        {
            if (subscription.Ephemeral) throw Corrupt($"Snapshot file {file.FileId} is for an ephemeral subscription.");
            slot = subscription.Slot;
        }

        var bit = slot >= 0 ? SqliteSql.Bit(slot) : 0L;
        var diagnostics = context.Report.Diagnostics;
        foreach (var info in file.Manifest.Tables)
        {
            if (!_byServerId.TryGetValue(info.TableId, out var table) || table.Negotiated is null) continue;
            var tableStarted = Stopwatch.GetTimestamp();
            var source = schema + "." + SqliteSql.Quote(info.Name);
            // (pk, ver, r) of every row in the file, in arrival order: the version table is keyed by the data table's rowid.
            var sourceKey = SqliteSql.KeyExpression(table.Model, "s");
            var pairs = $"(SELECT {sourceKey} AS pk, v.ver AS ver, s.rowid AS r FROM {source} AS s JOIN {schema}.{SqliteSql.Quote(info.VersionTable)} AS v ON v.rid = s.rowid)";
            var sourceColumns = TableColumns(context, info.Name, schema);
            if (sourceColumns.Count == 0) throw Corrupt($"Snapshot file {file.FileId} does not contain {info.Name}.");
            var versionColumns = TableColumns(context, info.VersionTable, schema);
            if (!versionColumns.Contains("rid", StringComparer.OrdinalIgnoreCase) || !versionColumns.Contains("ver", StringComparer.OrdinalIgnoreCase))
            {
                // Not this format's version table (rid → ver): a file this build cannot number - rows instead.
                throw Corrupt($"Snapshot file {file.FileId}: {info.VersionTable} is not a version table of format {G9SyncProtocol.SnapshotFileFormatVersion}.");
            }
            var localColumns = TableColumns(context, table.Model.LocalName, "main");
            var shared = sourceColumns.Where(c => localColumns.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();
            if (!shared.Contains(table.Model.KeyColumn, StringComparer.OrdinalIgnoreCase)) throw Corrupt($"Snapshot file {file.FileId}: {info.Name} has no key column.");

            var isProtected = context.HasDirty(table.Tid);
            var empty = Scalar(context.Connection, context.Transaction, $"SELECT 1 FROM {table.Quoted} LIMIT 1") is null;
            long rows;
            string mode;
            if (empty && !isProtected)
            {
                PrepareIndexesForCopy(context, table, info.Name, schema);
                var identical = sourceColumns.SequenceEqual(localColumns, StringComparer.OrdinalIgnoreCase);
                mode = identical ? G9CApplyDiagnostics.FileModeCopy : G9CApplyDiagnostics.FileModeColumns;
                var list = string.Join(", ", shared.Select(SqliteSql.Quote));
                rows = Exec(context.Connection, context.Transaction, identical
                    ? $"INSERT INTO main.{table.Quoted} SELECT * FROM {source}"
                    : $"INSERT INTO main.{table.Quoted} ({list}) SELECT {list} FROM {source}");

                // Every row is new to the table: each gets its membership, its server version and the next number on the clock.
                var numbered = Exec(context.Connection, context.Transaction,
                    "INSERT INTO g9_rows (tid, pk, server_ver, local_seq, subs, dirty, dirty_mask, rejected, deleted) " +
                    $"SELECT $t, v.pk, v.ver, $base + row_number() OVER (ORDER BY v.r), $bit, 0, 0, 0, 0 FROM {pairs} AS v WHERE true " +
                    "ON CONFLICT(tid, pk) DO UPDATE SET server_ver = excluded.server_ver, local_seq = excluded.local_seq, subs = subs | excluded.subs, " +
                    "dirty = 0, dirty_mask = 0, rejected = 0, deleted = 0",
                    ("$t", table.Tid), ("$base", context.Clock), ("$bit", bit));
                context.Clock += numbered;
                if (context.WantsChangedKey(table.Model.LocalName)) AddChangedKeys(context, table, $"SELECT {sourceKey} FROM {source} AS s ORDER BY s.rowid");
            }
            else
            {
                mode = G9CApplyDiagnostics.FileModeMerge;
                rows = MergeSnapshotTable(context, table, source, pairs, shared, bit, isProtected);
            }

            if (context.HasRevoked && slot >= 0)
            {
                Exec(context.Connection, context.Transaction,
                    $"DELETE FROM g9_revoked WHERE tid = $t AND pk IN (SELECT {sourceKey} FROM {source} AS s); " +
                    $"DELETE FROM g9_rejections WHERE tid = $t AND code = $code AND pk IN (SELECT {sourceKey} FROM {source} AS s)",
                    ("$t", table.Tid), ("$code", RevokedRejectionCode));
            }

            context.Report.Upserts += rows;
            context.Advance(rows);
            diagnostics.FileRows += rows;
            diagnostics.FileTables[table.Model.LocalName] = diagnostics.FileTables.TryGetValue(table.Model.LocalName, out var before) && !before.Split('+').Contains(mode)
                ? before + "+" + mode
                : before ?? mode;
            context.AddTableTime(table.Model.LocalName, rows, Stopwatch.GetElapsedTime(tableStarted));
            LogSnapshotTableApplied(_logger, table.Model.LocalName, rows, mode);
        }

        diagnostics.SnapshotFiles++;
        diagnostics.FileCopyMs += (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    /// <summary>
    ///     The merge of a file table into a table that already holds rows (a second site in tables the first one filled) or has protected
    ///     rows. The rules are the per-row apply's: a protected row is not written but gains membership; a row identical to the stored one
    ///     is not written and keeps its place in the change feed (ADR-0058); everything else is upserted and numbered. Returns the rows
    ///     delivered (upserted or unchanged; protected rows are counted as skipped).
    /// </summary>
    private long MergeSnapshotTable(ApplyContext context, LocalTable table, string source, string pairs, List<string> columns, long bit, bool isProtected)
    {
        var connection = context.Connection;
        var transaction = context.Transaction;
        var key = SqliteSql.Quote(table.Model.KeyColumn);
        var sourceKey = SqliteSql.KeyExpression(table.Model, "s");

        // The file's (pk, ver, r) by key, once: the statements below look versions up by key.
        const string versions = "temp.g9_file_versions";
        Exec(connection, transaction,
            "DROP TABLE IF EXISTS temp.g9_file_versions; CREATE TEMP TABLE g9_file_versions (pk TEXT NOT NULL PRIMARY KEY, ver INTEGER NOT NULL, r INTEGER NOT NULL) WITHOUT ROWID; " +
            $"INSERT OR REPLACE INTO temp.g9_file_versions (pk, ver, r) SELECT pk, ver, r FROM {pairs}");

        Exec(connection, transaction, "DROP TABLE IF EXISTS temp.g9_file_protect; CREATE TEMP TABLE g9_file_protect (pk TEXT NOT NULL PRIMARY KEY) WITHOUT ROWID");
        if (isProtected)
        {
            using var insert = new SqliteRowStatement(connection, "INSERT OR IGNORE INTO temp.g9_file_protect (pk) VALUES (?1)");
            foreach (var (tid, pk) in context.Dirty)
            {
                if (tid != table.Tid) continue;
                insert.BindText(1, pk);
                insert.Execute();
            }
        }

        // Rows that are new, differ, or have no metadata yet - and are not protected: by the source's rowid (to read them) and key (to number them).
        var differs = string.Join(" OR ", columns.Where(c => !string.Equals(c, table.Model.KeyColumn, StringComparison.OrdinalIgnoreCase))
            .Select(c => "m." + SqliteSql.Quote(c) + " IS NOT s." + SqliteSql.Quote(c)));
        Exec(connection, transaction, "DROP TABLE IF EXISTS temp.g9_file_changed; CREATE TEMP TABLE g9_file_changed (r INTEGER PRIMARY KEY, pk TEXT NOT NULL UNIQUE)");
        Exec(connection, transaction,
            $"INSERT INTO temp.g9_file_changed (r, pk) SELECT s.rowid, {sourceKey} FROM {source} AS s LEFT JOIN main.{table.Quoted} AS m ON m.{key} = s.{key} " +
            $"WHERE NOT EXISTS (SELECT 1 FROM temp.g9_file_protect AS p WHERE p.pk = {sourceKey}) " +
            $"AND (m.{key} IS NULL{(differs.Length == 0 ? string.Empty : " OR " + differs)} " +
            $"OR NOT EXISTS (SELECT 1 FROM g9_rows AS r WHERE r.tid = $t AND r.pk = {sourceKey} AND r.deleted = 0))",
            ("$t", table.Tid));

        var list = string.Join(", ", columns.Select(SqliteSql.Quote));
        var updates = columns.Where(c => !string.Equals(c, table.Model.KeyColumn, StringComparison.OrdinalIgnoreCase))
            .Select(c => SqliteSql.Quote(c) + " = excluded." + SqliteSql.Quote(c)).ToList();
        Exec(connection, transaction,
            $"INSERT INTO main.{table.Quoted} ({list}) SELECT {list} FROM {source} AS s WHERE s.rowid IN (SELECT r FROM temp.g9_file_changed) " +
            (updates.Count == 0 ? $"ON CONFLICT({key}) DO NOTHING" : $"ON CONFLICT({key}) DO UPDATE SET {string.Join(", ", updates)}"));

        // Written rows: a new number on the clock. Unchanged rows: version and membership only, and only where they differ - their place in
        // the change feed stays where it was, so a projection does not redo work for a row that did not change (ADR-0058).
        var numbered = Exec(connection, transaction,
            "INSERT INTO g9_rows (tid, pk, server_ver, local_seq, subs, dirty, dirty_mask, rejected, deleted) " +
            $"SELECT $t, v.pk, v.ver, $base + row_number() OVER (ORDER BY v.r), $bit, 0, 0, 0, 0 FROM {versions} AS v " +
            "WHERE v.pk IN (SELECT pk FROM temp.g9_file_changed) " +
            "ON CONFLICT(tid, pk) DO UPDATE SET server_ver = excluded.server_ver, local_seq = excluded.local_seq, subs = subs | excluded.subs, " +
            "dirty = 0, dirty_mask = 0, rejected = 0, deleted = 0",
            ("$t", table.Tid), ("$base", context.Clock), ("$bit", bit));
        context.Clock += numbered;

        Exec(connection, transaction,
            $"UPDATE g9_rows SET server_ver = (SELECT v.ver FROM {versions} AS v WHERE v.pk = g9_rows.pk), subs = subs | $bit, " +
            "dirty = 0, dirty_mask = 0, rejected = 0, deleted = 0 " +
            $"WHERE tid = $t AND pk IN (SELECT pk FROM {versions}) " +
            "AND pk NOT IN (SELECT pk FROM temp.g9_file_changed) AND pk NOT IN (SELECT pk FROM temp.g9_file_protect) " +
            $"AND (server_ver IS NOT (SELECT v.ver FROM {versions} AS v WHERE v.pk = g9_rows.pk) OR (subs | $bit) <> subs " +
            "OR dirty <> 0 OR dirty_mask <> 0 OR rejected <> 0 OR deleted <> 0)",
            ("$t", table.Tid), ("$bit", bit));

        // Protected rows keep their local state; they still belong to this subscription.
        var skipped = 0L;
        if (isProtected)
        {
            skipped = Convert.ToInt64(Scalar(connection, transaction,
                $"SELECT count(*) FROM temp.g9_file_protect AS p WHERE p.pk IN (SELECT pk FROM {versions})"), CultureInfo.InvariantCulture);
            if (bit != 0)
            {
                Exec(connection, transaction,
                    $"UPDATE g9_rows SET subs = subs | $bit WHERE tid = $t AND deleted = 0 AND pk IN (SELECT p.pk FROM temp.g9_file_protect AS p WHERE p.pk IN (SELECT pk FROM {versions}))",
                    ("$t", table.Tid), ("$bit", bit));
            }
        }

        var total = Convert.ToInt64(Scalar(connection, transaction, $"SELECT count(*) FROM {versions}"), CultureInfo.InvariantCulture);
        context.Report.SkippedDirty += skipped;
        context.Report.UnchangedRows += total - skipped - numbered;
        if (context.WantsChangedKey(table.Model.LocalName))
        {
            AddChangedKeys(context, table, "SELECT pk FROM temp.g9_file_changed ORDER BY r");
        }

        Exec(connection, transaction, "DROP TABLE IF EXISTS temp.g9_file_changed; DROP TABLE IF EXISTS temp.g9_file_protect; DROP TABLE IF EXISTS temp.g9_file_versions");
        return total - skipped;
    }

    private static void AddChangedKeys(ApplyContext context, LocalTable table, string sql)
    {
        using var command = Command(context.Connection, context.Transaction, sql + " LIMIT $limit");
        command.Parameters.AddWithValue("$limit", context.MaxChangedKeys);
        using var reader = command.ExecuteReader();
        while (reader.Read() && context.WantsChangedKey(table.Model.LocalName)) context.AddChanged(table.Model.LocalName, reader.GetString(0));
    }

    /// <summary>
    ///     Before a copy into an empty table: drops each index the file cannot supply - SQLite's transfer optimisation copies an index
    ///     b-tree only when the source has the same index, and refuses the whole optimisation for a table with an index it cannot match.
    ///     The dropped ones are rebuilt from their exact statements at the end of the apply, like deferred indexes (ADR-0034). An index
    ///     matches when its uniqueness, partiality and every column entry (column, order, collation) are the same.
    /// </summary>
    private void PrepareIndexesForCopy(ApplyContext context, LocalTable table, string sourceTable, string schema)
    {
        var available = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, unique, partial) in ListIndexes(context, sourceTable, schema))
        {
            if (!partial) available.Add(IndexSignature(context, name, unique, schema));
        }

        var dropped = 0;
        foreach (var (name, unique, partial) in ListIndexes(context, table.Model.LocalName, "main"))
        {
            if (!partial && available.Contains(IndexSignature(context, name, unique, "main"))) continue;
            if (context.DeferredIndexes.Any(index => string.Equals(index.Name, name, StringComparison.OrdinalIgnoreCase))) continue;
            if (Scalar(context.Connection, context.Transaction, "SELECT sql FROM main.sqlite_master WHERE type = 'index' AND name = $n", ("$n", name)) is not string sql)
            {
                continue;
            }

            Exec(context.Connection, context.Transaction, "DROP INDEX main." + SqliteSql.Quote(name));
            context.DeferredIndexes.Add((name, sql));
            dropped++;
        }

        if (dropped > 0) context.Report.Diagnostics.DeferredIndexes[table.Model.LocalName] = context.Report.Diagnostics.DeferredIndexes.GetValueOrDefault(table.Model.LocalName) + dropped;
    }

    private static List<(string Name, bool Unique, bool Partial)> ListIndexes(ApplyContext context, string table, string schema)
    {
        var indexes = new List<(string, bool, bool)>();
        using var command = Command(context.Connection, context.Transaction,
            "SELECT name, \"unique\", partial FROM pragma_index_list($t, $s) WHERE origin = 'c'");
        command.Parameters.AddWithValue("$t", table);
        command.Parameters.AddWithValue("$s", schema);
        using var reader = command.ExecuteReader();
        while (reader.Read()) indexes.Add((reader.GetString(0), reader.GetInt64(1) != 0, reader.GetInt64(2) != 0));
        return indexes;
    }

    private static string IndexSignature(ApplyContext context, string index, bool unique, string schema)
    {
        var text = new StringBuilder(unique ? "u" : "n");
        using var command = Command(context.Connection, context.Transaction,
            "SELECT seqno, coalesce(name, ''), \"desc\", coalesce(coll, ''), key FROM pragma_index_xinfo($i, $s) ORDER BY seqno");
        command.Parameters.AddWithValue("$i", index);
        command.Parameters.AddWithValue("$s", schema);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            text.Append('|').Append(reader.GetInt64(0)).Append(':').Append(reader.GetString(1).ToUpperInvariant()).Append(':').Append(reader.GetInt64(2))
                .Append(':').Append(reader.GetString(3).ToUpperInvariant()).Append(':').Append(reader.GetInt64(4));
        }

        return text.ToString();
    }

    private static List<string> TableColumns(ApplyContext context, string table, string schema)
    {
        var columns = new List<string>();
        using var command = Command(context.Connection, context.Transaction, "SELECT name FROM pragma_table_info($t, $s) ORDER BY cid");
        command.Parameters.AddWithValue("$t", table);
        command.Parameters.AddWithValue("$s", schema);
        using var reader = command.ExecuteReader();
        while (reader.Read()) columns.Add(reader.GetString(0));
        return columns;
    }

    private static string SafeFileName(string fileId)
    {
        var builder = new StringBuilder(fileId.Length);
        foreach (var c in fileId) builder.Append(char.IsAsciiLetterOrDigit(c) ? c : '_');
        return builder.ToString();
    }

    private static G9CSyncException Corrupt(string message) => new(G9SyncErrorCodes.SnapshotFileCorrupt, message);

    internal sealed record StagedFile(string FileId, string Path, G9DtSnapshotFileManifest Manifest, string? SubscriptionKey, long FirstSequence, long SegmentsDone);

    private sealed class PendingSegments
    {
        public int Segments { get; set; }
        public long Rows { get; set; }
        public long Bytes { get; set; }
        public long Unsynced { get; set; }
    }

    [LoggerMessage(EventId = 20121, Level = LogLevel.Debug, Message = "Snapshot file: {Table} received {Rows} row(s) ({Mode})")]
    private static partial void LogSnapshotTableApplied(ILogger logger, string table, long rows, string mode);

    [LoggerMessage(EventId = 20122, Level = LogLevel.Warning, Message = "A staged snapshot file could not be deleted and is retried at the next start: {Path} ({Message})")]
    private static partial void LogSnapshotFileNotDeleted(ILogger logger, string path, string message);
}
