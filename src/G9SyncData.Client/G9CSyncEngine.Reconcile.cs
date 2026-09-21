using G9SyncData.Client.Storage;
using G9SyncData.Protocol;
using G9SyncData.Protocol.RowBatch;
using Microsoft.Extensions.Logging;

namespace G9SyncData.Client;

/// <summary>
///     Reconciliation (D-07): compare 256 bucket digests with the server, list keys only for buckets that differ, fetch
///     rows that are missing or different, add membership for rows that already match, move out rows the server no longer
///     includes. Replaces reinitialize and every "re-pull" workaround; moves only the difference.
/// </summary>
public sealed partial class G9CSyncEngine
{
    private async Task<G9CApplyReport> ReconcileSubscriptionAsync(G9CSyncSubscriptionRecord subscription, G9DtSubscriptionDefinition definition,
        IReadOnlyList<G9DtNegotiatedTable> negotiated, CancellationToken ct)
    {
        var tables = definition.Tables
            .Select(name => negotiated.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)))
            .OfType<G9DtNegotiatedTable>()
            .Where(t => t.Direction != G9ESyncDirection.UploadOnly)   // the device's own rows: nothing on the server to compare them with
            .ToList();

        var request = new G9DtReconcileRequest { ClientId = _clientId, Subscription = subscription.ToReference(), FrameVersion = _options.MaxFrameVersion };
        foreach (var table in tables) request.Tables.Add(await _store.ComputeDigestAsync(subscription.Key, table, ct).ConfigureAwait(false));

        var serverRows = new Dictionary<int, Dictionary<string, (long Version, long Hash)>>();
        G9DtReconcileSummary? summary = null;
        await foreach (var chunk in _transport.ReconcileAsync(request, ct).ConfigureAwait(false))
        {
            if (chunk.IsFinal)
            {
                summary = chunk.ReconcileSummary;
                break;
            }

            if (chunk.Payload is null) continue;
            using var reader = G9CRowBatchReader.Open(chunk.Payload, _options.MaxDecodedFrameBytes);
            while (reader.NextSection())
            {
                if (!serverRows.TryGetValue(reader.TableId, out var rows)) serverRows[reader.TableId] = rows = new Dictionary<string, (long, long)>(StringComparer.Ordinal);
                while (reader.ReadRow()) rows[reader.Key] = (reader.Version, reader.RowHash);
            }
        }

        if (summary is null) throw new G9CSyncException(G9SyncErrorCodes.NetworkFailure, "The reconcile stream ended before its summary.");

        var report = new G9CApplyReport();
        var handedToSnapshot = false;
        foreach (var mismatch in summary.Mismatched)
        {
            var table = tables.FirstOrDefault(t => t.TableId == mismatch.TableId);
            if (table is null) continue;
            var server = serverRows.GetValueOrDefault(table.TableId) ?? new Dictionary<string, (long Version, long Hash)>(StringComparer.Ordinal);
            var buckets = mismatch.Buckets.ToHashSet();
            var local = await _store.ReadLocalEntriesAsync(subscription.Key, table, buckets, server.Keys, ct).ConfigureAwait(false);
            var localByKey = new Dictionary<string, G9CLocalRowEntry>(StringComparer.Ordinal);
            foreach (var entry in local) localByKey[entry.Key] = entry;

            var fetch = new List<string>();
            var addMembership = new List<string>();
            var moveOut = new List<string>();
            foreach (var (key, (_, hash)) in server)
            {
                if (!localByKey.TryGetValue(key, out var entry)) fetch.Add(key);
                else if (entry.Dirty)
                {
                    // A pending local change is never overwritten here; a row visible again still gets its membership back.
                    if (!entry.Member) addMembership.Add(key);
                }
                else if (entry.Hash != hash) fetch.Add(key);
                else if (!entry.Member) addMembership.Add(key);
            }

            foreach (var entry in local)
            {
                // Rows with pending changes are released too: the store's revocation policy decides (by default it quarantines them,
                // so the app stops showing them and the user's change survives - ADR-0026).
                if (entry.Member && buckets.Contains(entry.Bucket) && !server.ContainsKey(entry.Key)) moveOut.Add(entry.Key);
            }

            // A difference this large is a download, not a repair (ADR-0066): an epoch change, a restore, a device that was away past
            // the tombstone horizon. Fetching it key by key holds the whole site in memory and lands it in transactions that cannot
            // resume; the pull that follows this step in the same run does the same job staged, resumable and with the large-load
            // apply path. So: what only a reconcile can do - memberships and move-outs - is done here, and the rows are left to a
            // snapshot by clearing the cursor. Rows that are already right cost that snapshot nothing but the bytes (guarded upsert).
            if (_options.ReconcileSnapshotThreshold > 0 && fetch.Count >= _options.ReconcileSnapshotThreshold)
            {
                var released = await _store.ApplyReconcileAsync(subscription.Key, table, [], addMembership, moveOut, definition.MoveOutPolicy, ct)
                    .ConfigureAwait(false);
                report.Merge(released);
                handedToSnapshot = true;
                LogReconcileHandedToSnapshot(_logger, subscription.Key, table.Name, fetch.Count, _options.ReconcileSnapshotThreshold);
                continue;
            }

            // Fetched and applied in groups, not all at once (ADR-0066): every frame of a large difference used to be held in one list
            // and applied in one transaction, on a device whose map already has it near its memory limit. Each group is a committed
            // transaction of real server rows, the subscription's cursor only moves when every group of every table is in, and an
            // interrupted reconcile simply reconciles again and finds less to do.
            var groupKeys = Math.Max(Math.Max(1, _options.FetchKeysPerRequest), _options.ReconcileApplyRows);
            var pendingMembership = addMembership;
            var pendingMoveOut = moveOut;
            var fetched = 0;
            foreach (var group in fetch.Chunk(groupKeys))
            {
                var frames = new List<byte[]>();
                var returned = new HashSet<string>(StringComparer.Ordinal);
                foreach (var keys in group.Chunk(Math.Max(1, _options.FetchKeysPerRequest)))
                {
                    var fetchRequest = new G9DtFetchRowsRequest
                    {
                        ClientId = _clientId,
                        Subscription = subscription.ToReference(),
                        TableId = table.TableId,
                        Keys = keys.ToList(),
                        Columns = table.Columns.Select(c => c.Name).ToList(),
                        FrameVersion = _options.MaxFrameVersion
                    };
                    await foreach (var chunk in _transport.FetchRowsAsync(fetchRequest, ct).ConfigureAwait(false))
                    {
                        if (chunk.IsFinal || chunk.Payload is null) continue;
                        frames.Add(chunk.Payload);
                        using var reader = G9CRowBatchReader.Open(chunk.Payload, _options.MaxDecodedFrameBytes);
                        while (reader.NextSection())
                        {
                            while (reader.ReadRow()) returned.Add(reader.Key);
                        }
                    }
                }

                // A key listed but not returned left the subscription between listing and fetch.
                var groupMoveOut = group.Where(k => !returned.Contains(k) && localByKey.TryGetValue(k, out var e) && e.Member).ToList();
                groupMoveOut.AddRange(pendingMoveOut);

                // The memberships and move-outs that came from the comparison ride with the first group.
                var applied = await _store.ApplyReconcileAsync(subscription.Key, table, frames, pendingMembership, groupMoveOut, definition.MoveOutPolicy, ct)
                    .ConfigureAwait(false);
                report.Merge(applied);
                pendingMembership = [];
                pendingMoveOut = [];
                fetched += group.Length;
            }

            if (fetched == 0 && (pendingMembership.Count > 0 || pendingMoveOut.Count > 0))
            {
                // Nothing to fetch, but rows to adopt or release.
                report.Merge(await _store.ApplyReconcileAsync(subscription.Key, table, [], pendingMembership, pendingMoveOut, definition.MoveOutPolicy, ct)
                    .ConfigureAwait(false));
            }

            LogReconciledTable(_logger, subscription.Key, table.Name, mismatch.Buckets.Count, fetch.Count, addMembership.Count, moveOut.Count);
        }

        await _store.UpdateSubscriptionAsync(subscription.Key, handedToSnapshot
            ? new G9CSubscriptionUpdate
            {
                // No cursor: the plan step of this same run sees an initial pull and stages a snapshot. The hashes are left as they
                // are - the snapshot's apply writes the new ones together with its cursor, atomically with the rows.
                ClearCursor = true,
                State = G9ESubscriptionState.Active,
                ClearError = true,
                Reconciled = true
            }
            : new G9CSubscriptionUpdate
            {
                Cursor = summary.Cursor,
                DefinitionHash = summary.DefinitionHash,
                AuthHash = summary.AuthHash,
                State = G9ESubscriptionState.Active,
                ClearError = true,
                Reconciled = true
            }, ct).ConfigureAwait(false);
        return report;
    }

    [LoggerMessage(EventId = 20024, Level = LogLevel.Information,
        Message = "Reconciling {Subscription} / {Table} would fetch {Rows} rows (threshold {Threshold}): released what left, and leaving the rows to a staged snapshot pull")]
    private static partial void LogReconcileHandedToSnapshot(ILogger logger, string subscription, string table, int rows, int threshold);

    [LoggerMessage(EventId = 20020, Level = LogLevel.Information, Message = "Reconciled {Subscription} / {Table}: {Buckets} buckets differed, fetched {Fetched}, adopted {Adopted}, moved out {MovedOut}")]
    private static partial void LogReconciledTable(ILogger logger, string subscription, string table, int buckets, int fetched, int adopted, int movedOut);
}
