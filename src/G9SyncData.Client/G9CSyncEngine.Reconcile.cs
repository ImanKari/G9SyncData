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
            using var reader = G9CRowBatchReader.Open(chunk.Payload);
            while (reader.NextSection())
            {
                if (!serverRows.TryGetValue(reader.TableId, out var rows)) serverRows[reader.TableId] = rows = new Dictionary<string, (long, long)>(StringComparer.Ordinal);
                while (reader.ReadRow()) rows[reader.Key] = (reader.Version, reader.RowHash);
            }
        }

        if (summary is null) throw new G9CSyncException(G9SyncErrorCodes.NetworkFailure, "The reconcile stream ended before its summary.");

        var report = new G9CApplyReport();
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

            var frames = new List<byte[]>();
            var returned = new HashSet<string>(StringComparer.Ordinal);
            foreach (var keys in fetch.Chunk(Math.Max(1, _options.FetchKeysPerRequest)))
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
                    using var reader = G9CRowBatchReader.Open(chunk.Payload);
                    while (reader.NextSection())
                    {
                        while (reader.ReadRow()) returned.Add(reader.Key);
                    }
                }
            }

            // A key listed but not returned left the subscription between listing and fetch.
            moveOut.AddRange(fetch.Where(k => !returned.Contains(k) && localByKey.TryGetValue(k, out var e) && e.Member));

            var applied = await _store.ApplyReconcileAsync(subscription.Key, table, frames, addMembership, moveOut, definition.MoveOutPolicy, ct)
                .ConfigureAwait(false);
            report.Merge(applied);
            LogReconciledTable(_logger, subscription.Key, table.Name, mismatch.Buckets.Count, fetch.Count, addMembership.Count, moveOut.Count);
        }

        await _store.UpdateSubscriptionAsync(subscription.Key, new G9CSubscriptionUpdate
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

    [LoggerMessage(EventId = 20020, Level = LogLevel.Information, Message = "Reconciled {Subscription} / {Table}: {Buckets} buckets differed, fetched {Fetched}, adopted {Adopted}, moved out {MovedOut}")]
    private static partial void LogReconciledTable(ILogger logger, string subscription, string table, int buckets, int fetched, int adopted, int movedOut);
}
