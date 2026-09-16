using System.Buffers.Text;
using System.Text;
using G9SyncData.Server.Model;
using G9SyncData.Server.Storage;

namespace G9SyncData.Server;

/// <summary>One committed change of a synced table.</summary>
/// <param name="Key">Canonical key.</param>
/// <param name="Version">Row version after the change.</param>
/// <param name="Deleted">The row was deleted.</param>
/// <param name="ChangedColumns">Physical columns the last update changed; null for inserts, deletes and unknown masks.</param>
/// <param name="OriginClient">The device whose push made the change, or null for server-side writes.</param>
/// <param name="ChangedAtUtc">Start time of the transaction that made the change.</param>
public sealed record G9CServerChange(string Key, long Version, bool Deleted, IReadOnlyList<string>? ChangedColumns, Guid? OriginClient, DateTime ChangedAtUtc);

public sealed class G9CServerChangePage
{
    public required IReadOnlyList<G9CServerChange> Changes { get; init; }

    /// <summary>Pass to the next read. Store it only after the page was processed.</summary>
    public required string NextCursor { get; init; }

    /// <summary>More changes up to the same snapshot are waiting: read again with <see cref="NextCursor" />.</summary>
    public required bool HasMore { get; init; }
}

/// <summary>
///     A snapshot-exact change stream of synced tables for server code (plan D-15) — e.g. a cache that must follow edits made by
///     devices and by the web app alike. Cursors are stateless strings: store one per consumer, per table.
/// </summary>
/// <remarks>
///     Changes are coalesced per row: a row changed several times between two reads appears once, with its latest state.
///     A cursor older than the tombstone horizon fails with <c>G9S-RSY-HORIZON</c>; the consumer rebuilds from the table and
///     starts again from <see cref="GetCurrentCursorAsync" />.
/// </remarks>
public interface IG9SyncServerChangeFeed
{
    /// <summary>A cursor that means "everything committed until now has been seen".</summary>
    Task<string> GetCurrentCursorAsync(CancellationToken cancellationToken = default);

    /// <summary>The next page of changes of <paramref name="table" /> (logical name, <c>schema.table</c>) after <paramref name="cursor" />.</summary>
    Task<G9CServerChangePage> ReadAsync(string table, string cursor, int limit = 1000, CancellationToken cancellationToken = default);
}

internal sealed class G9CSyncServerChangeFeed(G9CSyncServerEngine engine) : IG9SyncServerChangeFeed
{
    private const string Prefix = "f1";

    public async Task<string> GetCurrentCursorAsync(CancellationToken cancellationToken = default)
    {
        await engine.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var snapshot = await engine.Store.GetCurrentSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return Format(engine.Store.Epoch, snapshot, null, null);
    }

    public async Task<G9CServerChangePage> ReadAsync(string table, string cursor, int limit = 1000, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await engine.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        var model = engine.Model.FindTable(table) ?? throw new G9CSyncException(G9SyncErrorCodes.UnknownTable, $"'{table}' is not a synced table.");
        if (!TryParse(cursor, out var epoch, out var previous, out var next, out var after))
        {
            throw new G9CSyncException(G9SyncErrorCodes.TokenInvalid, "The change feed cursor is malformed.");
        }

        var store = engine.Store;
        if (epoch != store.Epoch)
        {
            throw new G9CSyncException(G9SyncErrorCodes.ResyncEpoch, "The capture installation changed since the cursor was issued; rebuild from the table.");
        }

        if (await store.IsBeyondTombstoneHorizonAsync(previous, [model], cancellationToken).ConfigureAwait(false))
        {
            throw new G9CSyncException(G9SyncErrorCodes.ResyncHorizon, "The cursor is older than the tombstone horizon; rebuild from the table.");
        }

        next ??= await store.GetCurrentSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var rows = await store.ReadChangesAsync(new G9CStoreChangeRequest
        {
            Table = model, PreviousSnapshot = previous, NewSnapshot = next, AfterKey = after, Limit = limit + 1
        }, cancellationToken).ConfigureAwait(false);

        var hasMore = rows.Count > limit;
        var page = hasMore ? rows.Take(limit).ToList() : rows;
        return new G9CServerChangePage
        {
            Changes = page.Select(row => new G9CServerChange(row.Key, row.Version, row.Deleted, ChangedColumns(model, row), row.OriginClient, row.ChangedAtUtc))
                .ToList(),
            HasMore = hasMore,
            NextCursor = hasMore ? Format(epoch, previous, next, page[^1].Key) : Format(epoch, next, null, null)
        };
    }

    private static IReadOnlyList<string>? ChangedColumns(G9CSyncServerTable table, G9CStoreChange change)
    {
        if (change.Deleted || change.ChangedMask == -1) return null;
        var columns = new List<string>();
        for (var i = 0; i < table.PhysicalColumns.Count && i < 63; i++)
        {
            if ((change.ChangedMask & (1L << i)) != 0) columns.Add(table.PhysicalColumns[i].Name);
        }

        return columns;
    }

    // f1.{epoch:N}.{b64url(previous snapshot)}.{b64url(page snapshot)?}.{b64url(after key)?}
    private static string Format(Guid epoch, string previous, string? next, string? after) =>
        string.Join('.', Prefix, epoch.ToString("N"), Encode(previous), Encode(next), Encode(after));

    private static bool TryParse(string? cursor, out Guid epoch, out string previous, out string? next, out string? after)
    {
        epoch = Guid.Empty;
        previous = string.Empty;
        next = null;
        after = null;
        var parts = cursor?.Split('.');
        if (parts is not { Length: 5 } || parts[0] != Prefix || !Guid.TryParseExact(parts[1], "N", out epoch)) return false;
        try
        {
            previous = Decode(parts[2]) ?? string.Empty;
            next = Decode(parts[3]);
            after = Decode(parts[4]);
        }
        catch (FormatException)
        {
            return false;
        }

        return previous.Length > 0;
    }

    private static string Encode(string? value) => value is null ? string.Empty : Base64Url.EncodeToString(Encoding.UTF8.GetBytes(value));

    private static string? Decode(string value) => value.Length == 0 ? null : Encoding.UTF8.GetString(Base64Url.DecodeFromChars(value));
}
