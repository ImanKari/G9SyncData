using System.Globalization;
using System.Text;
using System.Text.Json;
using G9SyncData.Client.Sqlite.Internal;
using G9SyncData.Client.Storage;
using G9SyncData.Client.Sqlite.Native;

namespace G9SyncData.Client.Sqlite;

/// <summary>
///     Revocation dispositions (ADR-0026). Every path that releases a row — a move-out in a pull or a reconciliation, an unsubscribe, an
///     expired ephemeral subscription — ends in <see cref="DisposeReleasedRow" />, which asks the store's
///     <see cref="G9CSqliteSyncStoreOptions.Revocation" /> policy what to do with a row NO subscription holds any more.
/// </summary>
public sealed partial class G9CSqliteSyncStore
{
    private const string RevokedRejectionCode = "G9S-REJ-REVOKED";

    /// <summary>
    ///     Applies the revocation policy to a released row. Rows still held by another subscription are untouched. Returns true when the row
    ///     left the app's table (deleted or quarantined).
    /// </summary>
    private bool DisposeReleasedRow(SqliteConnection connection, SqliteTransaction transaction, LocalTable table, string key, G9ERevocationReason reason,
        string? subscriptionKey, long sequence, G9CApplyReport? report)
    {
        long subs = 0, dirty = 0, dirtyMask = 0, rejected = 0;
        using (var state = Command(connection, transaction, "SELECT subs, dirty, dirty_mask, rejected FROM g9_rows WHERE tid = $t AND pk = $pk AND deleted = 0"))
        {
            state.Parameters.AddWithValue("$t", table.Tid);
            state.Parameters.AddWithValue("$pk", key);
            using var reader = state.ExecuteReader();
            if (reader.Read())
            {
                subs = reader.GetInt64(0);
                dirty = reader.GetInt64(1);
                dirtyMask = reader.GetInt64(2);
                rejected = reader.GetInt64(3);
            }
        }

        if (subs != 0) return false;

        // A row touched by a pending command is settled by that command's result, never disposed underneath it.
        if (dirty == 0 && Convert.ToInt64(Scalar(connection, transaction,
                "SELECT count(*) FROM g9_command_rows r JOIN g9_commands c ON c.command_id = r.command_id WHERE c.state = 0 AND r.tid = $t AND r.pk = $pk",
                ("$t", table.Tid), ("$pk", key)), CultureInfo.InvariantCulture) > 0)
        {
            return false;
        }

        var values = ReadLocalValues(connection, transaction, table, key);
        if (values.Count == 0)
        {
            Exec(connection, transaction, "UPDATE g9_rows SET deleted = 1, dirty = 0, dirty_mask = 0, rejected = 0, local_seq = $s WHERE tid = $t AND pk = $pk",
                ("$s", sequence), ("$t", table.Tid), ("$pk", key));
            return false;
        }

        var editor = new RowEditor(values);
        var row = new G9CRevokedRow
        {
            LocalTable = table.Model.LocalName,
            Key = key,
            Reason = reason,
            SubscriptionKey = subscriptionKey,
            HasPendingChanges = dirty != 0,
            IsRejected = rejected != 0,
            Values = values
        };
        var (rule, disposition) = _options.Revocation.Decide(row, editor);
        var now = NowMs();

        switch (disposition)
        {
            case G9ERevocationDisposition.MarkRevoked or G9ERevocationDisposition.Retain:
                if (editor.Changed.Count > 0)
                {
                    var columns = editor.Changed.ToArray();
                    Exec(connection, transaction,
                        $"UPDATE {table.Quoted} SET {string.Join(", ", columns.Select((c, i) => SqliteSql.Quote(c) + " = $v" + i.ToString(CultureInfo.InvariantCulture)))} " +
                        $"WHERE {table.KeyQuoted} = $k",
                        columns.Select((c, i) => ("$v" + i.ToString(CultureInfo.InvariantCulture), values[c])).Append(("$k", KeyValue(table, key))).ToArray());
                }

                Exec(connection, transaction,
                    "INSERT OR REPLACE INTO g9_revoked (tid, pk, disposition, reason, rule, at_utc) VALUES ($t, $pk, $d, $r, $rule, $now)",
                    ("$t", table.Tid), ("$pk", key), ("$d", (int)disposition), ("$r", (int)reason), ("$rule", rule?.Name), ("$now", now));
                if (dirty != 0)
                {
                    // Its pending change can no longer be accepted: park it as rejected instead of uploading it on every push.
                    Exec(connection, transaction,
                        "UPDATE g9_rows SET rejected = 1 WHERE tid = $t AND pk = $pk; " +
                        "INSERT OR REPLACE INTO g9_rejections (tid, pk, code, message, at_utc) VALUES ($t, $pk, $code, $message, $now)",
                        ("$t", table.Tid), ("$pk", key), ("$code", RevokedRejectionCode),
                        ("$message", "The row is no longer visible to this device; its pending change was not uploaded."), ("$now", now));
                }

                if (report is not null) report.RevokedRetained++;
                return false;

            case G9ERevocationDisposition.Quarantine:
                Exec(connection, transaction,
                    "INSERT OR REPLACE INTO g9_quarantine (tid, pk, reason, rule, row_json, had_pending, dirty_mask, at_utc) VALUES ($t, $pk, $r, $rule, $json, $p, $m, $now)",
                    ("$t", table.Tid), ("$pk", key), ("$r", (int)reason), ("$rule", rule?.Name), ("$json", SerializeValues(values)), ("$p", dirty != 0 ? 1 : 0),
                    ("$m", dirtyMask), ("$now", now));
                RemoveLocalRow(connection, transaction, table, key, sequence);
                if (report is not null) report.RevokedQuarantined++;
                return true;

            default:
                RemoveLocalRow(connection, transaction, table, key, sequence);
                if (report is not null) report.RevokedDeleted++;
                return true;
        }
    }

    private static void RemoveLocalRow(SqliteConnection connection, SqliteTransaction transaction, LocalTable table, string key, long sequence)
    {
        Exec(connection, transaction, $"DELETE FROM {table.Quoted} WHERE {table.KeyQuoted} = $k", ("$k", KeyValue(table, key)));
        Exec(connection, transaction,
            "UPDATE g9_rows SET deleted = 1, dirty = 0, dirty_mask = 0, rejected = 0, local_seq = $s WHERE tid = $t AND pk = $pk; " +
            "DELETE FROM g9_rejections WHERE tid = $t AND pk = $pk; DELETE FROM g9_revoked WHERE tid = $t AND pk = $pk",
            ("$s", sequence), ("$t", table.Tid), ("$pk", key));
    }

    private static Dictionary<string, object?> ReadLocalValues(SqliteConnection connection, SqliteTransaction transaction, LocalTable table, string key)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        using var command = Command(connection, transaction, $"SELECT * FROM {table.Quoted} WHERE {table.KeyQuoted} = $k");
        command.Parameters.AddWithValue("$k", KeyValue(table, key));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return values;
        for (var i = 0; i < reader.FieldCount; i++) values[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
        return values;
    }

    // ================================================================ holding area and retained rows

    public async Task<IReadOnlyList<G9CQuarantinedRow>> GetQuarantinedAsync(CancellationToken cancellationToken)
    {
        EnsureInitialized();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = Command(connection, null,
            "SELECT t.local_name, q.pk, q.reason, q.rule, q.had_pending, q.row_json, q.at_utc FROM g9_quarantine q JOIN g9_tables t ON t.tid = q.tid ORDER BY q.at_utc");
        var rows = new List<G9CQuarantinedRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new G9CQuarantinedRow
            {
                LocalTable = reader.GetString(0),
                Key = reader.GetString(1),
                Reason = (G9ERevocationReason)reader.GetInt32(2),
                Rule = reader.IsDBNull(3) ? null : reader.GetString(3),
                HadPendingChanges = reader.GetInt64(4) != 0,
                Values = DeserializeValues(reader.GetString(5)),
                AtUtc = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6)).UtcDateTime
            });
        }

        return rows;
    }

    public async Task<int> RestoreQuarantinedAsync(string localTable, string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localTable);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        EnsureInitialized();
        var table = GetLocal(localTable);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction(deferred: false);
            string? json = null;
            long mask = 0;
            using (var command = Command(connection, transaction, "SELECT row_json, dirty_mask, had_pending FROM g9_quarantine WHERE tid = $t AND pk = $pk"))
            {
                command.Parameters.AddWithValue("$t", table.Tid);
                command.Parameters.AddWithValue("$pk", key);
                using var reader = command.ExecuteReader();
                if (reader.Read())
                {
                    json = reader.GetString(0);
                    mask = reader.GetInt64(2) != 0 ? reader.GetInt64(1) : 0;
                }
            }

            if (json is null) return 0;
            var values = DeserializeValues(json);
            var exists = ReadLocalValues(connection, transaction, table, key).Count > 0;

            // Capture is ON (applying = 0): the restored values become pending local changes and upload on the next push. When the row was
            // re-downloaded meanwhile, only the columns the user had changed are written back, so newer server values of the others survive.
            if (exists)
            {
                var columns = table.Model.Columns.Select((c, i) => (c.Name, Index: i))
                    .Where(c => (mask == 0 || (mask & (1L << c.Index)) != 0) && values.ContainsKey(c.Name)).Select(c => c.Name).ToArray();
                if (columns.Length > 0)
                {
                    Exec(connection, transaction,
                        $"UPDATE {table.Quoted} SET {string.Join(", ", columns.Select((c, i) => SqliteSql.Quote(c) + " = $v" + i.ToString(CultureInfo.InvariantCulture)))} " +
                        $"WHERE {table.KeyQuoted} = $k",
                        columns.Select((c, i) => ("$v" + i.ToString(CultureInfo.InvariantCulture), values[c])).Append(("$k", KeyValue(table, key))).ToArray());
                }
            }
            else
            {
                var columns = values.Keys.ToArray();
                Exec(connection, transaction,
                    $"INSERT INTO {table.Quoted} ({string.Join(", ", columns.Select(SqliteSql.Quote))}) VALUES ({string.Join(", ", columns.Select((_, i) => "$v" + i.ToString(CultureInfo.InvariantCulture)))})",
                    columns.Select((c, i) => ("$v" + i.ToString(CultureInfo.InvariantCulture), values[c])).ToArray());
            }

            Exec(connection, transaction, "DELETE FROM g9_quarantine WHERE tid = $t AND pk = $pk", ("$t", table.Tid), ("$pk", key));
            transaction.Commit();
            return 1;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<int> DiscardQuarantinedAsync(string? localTable, string? key, CancellationToken cancellationToken)
    {
        EnsureInitialized();
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            var tid = localTable is null ? (long?)null : GetLocal(localTable).Tid;
            return Exec(connection, null, "DELETE FROM g9_quarantine WHERE ($t IS NULL OR tid = $t) AND ($pk IS NULL OR pk = $pk)", ("$t", tid), ("$pk", key));
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<G9CRetainedRow>> GetRetainedAsync(CancellationToken cancellationToken)
    {
        EnsureInitialized();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = Command(connection, null,
            "SELECT t.local_name, r.pk, r.disposition, r.reason, r.rule, r.at_utc FROM g9_revoked r JOIN g9_tables t ON t.tid = r.tid ORDER BY r.at_utc");
        var rows = new List<G9CRetainedRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new G9CRetainedRow
            {
                LocalTable = reader.GetString(0),
                Key = reader.GetString(1),
                Disposition = (G9ERevocationDisposition)reader.GetInt32(2),
                Reason = (G9ERevocationReason)reader.GetInt32(3),
                Rule = reader.IsDBNull(4) ? null : reader.GetString(4),
                AtUtc = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5)).UtcDateTime
            });
        }

        return rows;
    }

    public async Task<int> ReevaluateRetainedAsync(CancellationToken cancellationToken)
    {
        EnsureInitialized();
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction(deferred: false);
            var retained = new List<(long Tid, string Key, int Reason, string? Rule, int Disposition)>();
            using (var command = Command(connection, transaction, "SELECT tid, pk, reason, rule, disposition FROM g9_revoked"))
            {
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    retained.Add((reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetInt32(4)));
                }
            }

            if (retained.Count == 0) return 0;
            Exec(connection, transaction, "UPDATE g9_ctx SET applying = 1 WHERE id = 1");
            var sequence = Convert.ToInt64(Scalar(connection, transaction, "SELECT seq FROM g9_clock WHERE id = 1"), CultureInfo.InvariantCulture);
            var deleted = 0;
            foreach (var (tid, key, reason, ruleName, disposition) in retained)
            {
                var table = _byLocal.Values.FirstOrDefault(t => t.Tid == tid);
                if (table is null) continue;
                var subs = Convert.ToInt64(Scalar(connection, transaction, "SELECT coalesce(max(subs), 0) FROM g9_rows WHERE tid = $t AND pk = $pk AND deleted = 0",
                    ("$t", tid), ("$pk", key)), CultureInfo.InvariantCulture);
                if (subs != 0)
                {
                    // Visible again: no longer revoked, and a pending change parked by the revocation may upload again.
                    Exec(connection, transaction,
                        "DELETE FROM g9_revoked WHERE tid = $t AND pk = $pk; DELETE FROM g9_rejections WHERE tid = $t AND pk = $pk AND code = $code; " +
                        "UPDATE g9_rows SET rejected = 0 WHERE tid = $t AND pk = $pk AND NOT EXISTS (SELECT 1 FROM g9_rejections WHERE tid = $t AND pk = $pk)",
                        ("$t", tid), ("$pk", key), ("$code", RevokedRejectionCode));
                    continue;
                }

                if (disposition != (int)G9ERevocationDisposition.Retain) continue;

                var values = ReadLocalValues(connection, transaction, table, key);
                var rule = ruleName is null ? null : _options.Revocation.Find(ruleName);
                var row = new G9CRevokedRow
                {
                    LocalTable = table.Model.LocalName, Key = key, Reason = (G9ERevocationReason)reason, HasPendingChanges = false, IsRejected = false, Values = values
                };
                if (rule?.RetainWhile is { } retainWhile && values.Count > 0 && retainWhile(row)) continue;

                RemoveLocalRow(connection, transaction, table, key, ++sequence);
                deleted++;
            }

            Exec(connection, transaction, "UPDATE g9_clock SET seq = $s WHERE id = 1; UPDATE g9_ctx SET applying = 0 WHERE id = 1", ("$s", sequence));
            transaction.Commit();
            return deleted;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // ================================================================ values as JSON

    /// <summary>SQLite values as JSON: text as strings, integers and reals as numbers, blobs as <c>{"$b64": "…"}</c>.</summary>
    private static string SerializeValues(IReadOnlyDictionary<string, object?> values)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var (column, value) in values)
            {
                switch (value)
                {
                    case null:
                        writer.WriteNull(column);
                        break;
                    case long l:
                        writer.WriteNumber(column, l);
                        break;
                    case double d:
                        writer.WriteNumber(column, d);
                        break;
                    case byte[] bytes:
                        writer.WriteStartObject(column);
                        writer.WriteBase64String("$b64", bytes);
                        writer.WriteEndObject();
                        break;
                    default:
                        writer.WriteString(column, Convert.ToString(value, CultureInfo.InvariantCulture));
                        break;
                }
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static Dictionary<string, object?> DeserializeValues(string json)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        using var document = JsonDocument.Parse(json);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            values[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Number => property.Value.TryGetInt64(out var l) ? l : property.Value.GetDouble(),
                JsonValueKind.Object when property.Value.TryGetProperty("$b64", out var b64) => b64.GetBytesFromBase64(),
                _ => property.Value.GetRawText()
            };
        }

        return values;
    }

    private sealed class RowEditor(Dictionary<string, object?> values) : IG9RevokedRowEditor
    {
        public HashSet<string> Changed { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void Set(string column, object? value)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(column);
            if (!values.ContainsKey(column)) throw new ArgumentException($"'{column}' is not a column of the row.", nameof(column));
            values[column] = value;
            Changed.Add(column);
        }
    }
}
