using System.Globalization;
using G9SyncData.Client.Sqlite.Native;
using Microsoft.Extensions.Logging;

namespace G9SyncData.Client.Sqlite;

/// <summary>
///     Detects a database that was restored from a backup or copied from another device (ADR-0067).
/// </summary>
/// <remarks>
///     <para>The device's identity - its client id and its push batch counter - lives INSIDE the database file. Restore that file from a
///     backup, or let a device-to-device transfer copy it, and two histories share one identity. The server recognises a push by
///     (client id, batch id): when the restored copy reaches a batch id the other history already used, the server answers "already
///     applied" with the OTHER history's result, and the device clears the pending flag of rows it never actually uploaded. An edit is
///     lost, silently, on a device that looks perfectly healthy.</para>
///     <para>The fix is a second copy of a random value - the incarnation - kept in a file the platform does NOT back up (Android
///     <c>noBackupFilesDir</c>, an iOS file marked excluded from backup). The database says which incarnation it belongs to; the file says
///     which incarnation this installation is. When they disagree the database came from somewhere else: the device takes a new client id
///     and starts its batch counter again, so the server sees a new device, and every pending row stays pending and uploads normally.
///     Nothing else changes - rows, memberships and cursors are as valid as they were.</para>
///     <para>When the marker cannot be written the check does nothing, which is exactly the behaviour before it existed.</para>
/// </remarks>
public sealed partial class G9CSqliteSyncStore
{
    private const string IncarnationKey = "incarnation";

    /// <summary>True when this initialisation found a database that belongs to another installation and gave it a new identity.</summary>
    public bool ReplicaIdentityWasReplaced { get; private set; }

    /// <summary>Runs inside the initialisation transaction, after <c>client_id</c> exists.</summary>
    private void VerifyIncarnation(SqliteConnection connection, SqliteTransaction transaction)
    {
        var path = _options.IncarnationMarkerPath;
        if (string.IsNullOrWhiteSpace(path)) return;

        var inDatabase = Meta(connection, transaction, IncarnationKey);
        var inMarker = ReadMarker(path);

        // Same installation, same database.
        if (inDatabase is not null && string.Equals(inDatabase, inMarker, StringComparison.Ordinal)) return;

        // The marker is written BEFORE the transaction commits. If that fails nothing is recorded and nothing is reset - the feature is
        // simply off for this run. The other order could commit an incarnation no marker backs up, and the next start would read that
        // as a foreign database and replace a perfectly good identity.
        var incarnation = Guid.NewGuid().ToString("N");
        if (!TryWriteMarker(path, incarnation)) return;

        if (inDatabase is null)
        {
            // A database from before this check existed, or a brand-new one: adopt it. There is nothing to compare it with, and an
            // upgrade must not cost every device its identity.
            SetMeta(connection, transaction, IncarnationKey, incarnation);
            return;
        }

        // The database names an incarnation this installation does not have: it was restored or copied.
        var pending = Convert.ToInt64(Scalar(connection, transaction, "SELECT count(*) FROM g9_rows WHERE dirty <> 0"), CultureInfo.InvariantCulture);
        SetMeta(connection, transaction, IncarnationKey, incarnation);
        SetMeta(connection, transaction, "client_id", Guid.NewGuid().ToString("D"));
        SetMeta(connection, transaction, "next_batch_id", "1");

        // An in-flight batch and the commands tied to it belong to the old identity's numbering; they are rebuilt under the new one.
        // A stage holds a token issued to the old client id.
        Exec(connection, transaction,
            "DELETE FROM g9_meta WHERE key IN ('inflight_batch_id', 'inflight_max_seq'); " +
            "UPDATE g9_commands SET batch_id = NULL WHERE state = 0; " +
            "DELETE FROM g9_stage; DELETE FROM g9_stage_runs;");
        ReplicaIdentityWasReplaced = true;
        LogReplicaIdentityReplaced(_logger, pending);
    }

    private string? ReadMarker(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path).Trim();
            return text.Length == 0 ? null : text;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LogMarkerUnavailable(_logger, exception, path);
            return null;
        }
    }

    private bool TryWriteMarker(string path, string incarnation)
    {
        try
        {
            if (Path.GetDirectoryName(path) is { Length: > 0 } directory) Directory.CreateDirectory(directory);

            // Written whole and then moved into place: a marker cut short by a crash would read as "another installation".
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, incarnation);
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            LogMarkerUnavailable(_logger, exception, path);
            return false;
        }
    }

    [LoggerMessage(EventId = 20105, Level = LogLevel.Warning,
        Message = "This database was restored or copied from another installation: it has a new client id and its push batch counter starts again. " +
                  "{Pending} pending row(s) were kept and upload as usual")]
    private static partial void LogReplicaIdentityReplaced(ILogger logger, long pending);

    [LoggerMessage(EventId = 20106, Level = LogLevel.Warning,
        Message = "The incarnation marker {Path} could not be read or written; restore detection is off for this run")]
    private static partial void LogMarkerUnavailable(ILogger logger, Exception exception, string path);
}
