using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace G9SyncData.Client.Sqlite;

/// <summary>What <see cref="G9CSqliteSyncStore.ExportPendingChangesAsync" /> managed to save.</summary>
/// <param name="Path">The file written, or null when there was nothing to export.</param>
/// <param name="Rows">Pending rows written.</param>
/// <param name="Unreadable">Pending rows that could not be read back from the damaged file (their keys are still listed).</param>
public sealed record G9CPendingExport(string? Path, int Rows, int Unreadable);

/// <summary>
///     What an application needs when the device database is damaged (ADR-0068): a way to find out, and a way to save the work that has
///     not been uploaded before the file is thrown away. The store never deletes or rebuilds anything by itself - a damaged file may
///     still hold the only copy of somebody's day of field work, and what to do with it is the application's decision.
/// </summary>
public sealed partial class G9CSqliteSyncStore
{
    /// <summary>
    ///     Runs SQLite's <c>quick_check</c>. Returns the problems it reports; an empty list means the file is sound. A file too damaged
    ///     to be checked at all comes back as one problem rather than an exception, so a caller can always tell the two states apart.
    /// </summary>
    public async Task<IReadOnlyList<string>> CheckIntegrityAsync(CancellationToken cancellationToken = default)
    {
        var problems = new List<string>();
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var command = Command(connection, null, "PRAGMA quick_check(50)");
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var line = reader.GetString(0);
                if (!string.Equals(line, "ok", StringComparison.OrdinalIgnoreCase)) problems.Add(line);
            }
        }
        catch (G9CSqliteException exception)
        {
            problems.Add(exception.Message);
        }

        if (problems.Count > 0) LogIntegrityProblems(_logger, problems.Count, problems[0]);
        return problems;
    }

    /// <summary>
    ///     Writes every pending local change - the rows the server has not received - to <paramref name="path" /> as JSON lines, one row
    ///     per line: <c>{"table": …, "key": …, "deleted": …, "values": {…}}</c>. Best effort by design: it is meant for a database that
    ///     is about to be replaced, possibly a damaged one, so a row that cannot be read is listed with its key and no values instead of
    ///     stopping the export. Nothing in the database is changed.
    /// </summary>
    public async Task<G9CPendingExport> ExportPendingChangesAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        EnsureInitialized();

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var pending = new List<(long Tid, string Key, bool Deleted)>();
        using (var command = Command(connection, null, "SELECT tid, pk, dirty FROM g9_rows WHERE dirty <> 0 ORDER BY local_seq"))
        {
            using var reader = command.ExecuteReader();
            while (reader.Read()) pending.Add((reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2) == 2));
        }

        if (pending.Count == 0) return new G9CPendingExport(null, 0, 0);

        var byTid = _byLocal.Values.ToDictionary(t => t.Tid);
        var rows = 0;
        var unreadable = 0;
        if (System.IO.Path.GetDirectoryName(path) is { Length: > 0 } directory) Directory.CreateDirectory(directory);

        // Written whole and moved into place, so a crash half-way never leaves a file that looks like a complete export.
        var temporary = path + ".tmp";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            using var transaction = connection.BeginTransaction(deferred: true);
            foreach (var (tid, key, deleted) in pending)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!byTid.TryGetValue(tid, out var table)) continue;

                string? values = null;
                if (!deleted)
                {
                    try
                    {
                        var read = ReadLocalValues(connection, transaction, table, key);
                        if (read.Count > 0) values = SerializeValues(read);
                    }
                    catch (G9CSqliteException)
                    {
                        // The page this row lives on is one of the damaged ones. Its key is still worth keeping.
                    }

                    if (values is null) unreadable++;
                }

                using var buffer = new MemoryStream();
                using (var writer = new Utf8JsonWriter(buffer))
                {
                    writer.WriteStartObject();
                    writer.WriteString("table", table.Model.LocalName);
                    writer.WriteString("key", key);
                    writer.WriteBoolean("deleted", deleted);
                    if (values is not null)
                    {
                        writer.WritePropertyName("values");
                        writer.WriteRawValue(values);
                    }

                    writer.WriteEndObject();
                }

                await stream.WriteAsync(buffer.GetBuffer().AsMemory(0, (int)buffer.Length), cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync(Encoding.UTF8.GetBytes("\n"), cancellationToken).ConfigureAwait(false);
                rows++;
            }

            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporary, path, overwrite: true);
        LogPendingExported(_logger, rows, unreadable, path);
        return new G9CPendingExport(path, rows, unreadable);
    }

    [LoggerMessage(EventId = 20107, Level = LogLevel.Error, Message = "The device database failed its integrity check with {Count} problem(s); first: {Problem}")]
    private static partial void LogIntegrityProblems(ILogger logger, int count, string problem);

    [LoggerMessage(EventId = 20108, Level = LogLevel.Warning, Message = "Exported {Rows} pending row(s) ({Unreadable} without readable values) to {Path}")]
    private static partial void LogPendingExported(ILogger logger, int rows, int unreadable, string path);
}
