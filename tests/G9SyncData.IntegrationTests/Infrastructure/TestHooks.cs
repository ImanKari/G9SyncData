using System.Collections.Concurrent;
using G9SyncData.Client.Progress;
using G9SyncData.Protocol;
using G9SyncData.Server.Auth;
using G9SyncData.Server.Model;
using G9SyncData.Server.Pipeline;

namespace G9SyncData.IntegrationTests.Infrastructure;

/// <summary>An auth context the test changes between runs (permissions revoked, subscriptions forbidden).</summary>
internal sealed class MutableAuthProvider : IG9SyncAuthContextProvider
{
    public Guid[]? AllowedSites { get; set; }

    public Func<string, bool>? SubscriptionFilter { get; set; }

    public ValueTask<G9CSyncAuthContext> ResolveAsync(G9CSyncCaller caller, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new G9CSyncAuthContext
        {
            UserId = "tester",
            ServerParameters = new Dictionary<string, object?>(StringComparer.Ordinal) { ["AllowedSites"] = AllowedSites },
            SubscriptionFilter = SubscriptionFilter
        });
}

/// <summary>What the server-side hooks saw.</summary>
internal sealed class HookRecorder
{
    private int _resolverCalls;

    public ConcurrentQueue<string> DeletedKeys { get; } = new();
    public ConcurrentQueue<string> CommittedUpserts { get; } = new();
    public ConcurrentQueue<string> CommittedDeletes { get; } = new();
    public int ResolverCalls => _resolverCalls;

    public void CountResolverCall() => Interlocked.Increment(ref _resolverCalls);
}

internal sealed class NegativeStatusValidator : IG9UploadValidator
{
    public const string Code = "APP-STATUS-NEGATIVE";

    public bool AppliesTo(G9CSyncServerTable table) => table.LogicalName == "farm.Pot";

    public async ValueTask ValidateAsync(G9CUploadStepContext context, CancellationToken cancellationToken)
    {
        var rejections = new List<G9CRowRejection>();
        await using (var command = context.Connection.CreateCommand())
        {
            command.Transaction = context.Transaction;
            command.CommandText = $"SELECT g9_key::text FROM {context.StagingTable} WHERE g9_action = 1 AND g9_op = 1 AND \"Status\" < 0";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) rejections.Add(new G9CRowRejection(reader.GetString(0), Code, "Status must not be negative."));
        }

        await context.RejectAsync(rejections, cancellationToken);
    }
}

internal sealed class UpperCaseCodeTransformer : IG9UploadTransformer
{
    public bool AppliesTo(G9CSyncServerTable table) => table.LogicalName == "farm.Pot";

    public async ValueTask TransformAsync(G9CUploadStepContext context, CancellationToken cancellationToken)
    {
        await using var command = context.Connection.CreateCommand();
        command.Transaction = context.Transaction;
        command.CommandText = $"UPDATE {context.StagingTable} SET \"Code\" = upper(\"Code\") WHERE g9_action = 1 AND g9_op = 1 AND \"Code\" IS NOT NULL";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

internal sealed class RecordingDeleteHandler(HookRecorder recorder) : IG9DeleteHandler
{
    public bool AppliesTo(G9CSyncServerTable table) => table.LogicalName == "farm.Pot";

    public ValueTask HandleDeletesAsync(G9CUploadStepContext context, IReadOnlyList<string> keys, CancellationToken cancellationToken)
    {
        foreach (var key in keys) recorder.DeletedKeys.Enqueue(key);
        return ValueTask.CompletedTask;
    }
}

internal sealed class RecordingPostCommitHandler(HookRecorder recorder) : IG9PostCommitHandler
{
    public bool AppliesTo(G9CSyncServerTable table) => table.LogicalName == "farm.Pot";

    public ValueTask HandleAsync(G9CPostCommitContext context, CancellationToken cancellationToken)
    {
        foreach (var key in context.UpsertedKeys) recorder.CommittedUpserts.Enqueue(key);
        foreach (var key in context.DeletedKeys) recorder.CommittedDeletes.Enqueue(key);
        return ValueTask.CompletedTask;
    }
}

/// <summary>Decides by the client's Note: "accept" → client wins, "reject" → app rejection, anything else → server wins.</summary>
internal sealed class NoteDrivenConflictResolver(HookRecorder recorder) : IG9ConflictResolver
{
    public const string RejectCode = "APP-CONFLICT";

    public bool AppliesTo(G9CSyncServerTable table) => table.LogicalName == "farm.Pot";

    public ValueTask<IReadOnlyList<G9CConflictResolution>> ResolveAsync(G9CConflictContext context, CancellationToken cancellationToken)
    {
        recorder.CountResolverCall();
        IReadOnlyList<G9CConflictResolution> decisions = context.Rows.Select(row => (row.ClientValues.GetValueOrDefault("Note") as string) switch
        {
            "accept" => new G9CConflictResolution(row.Key, G9EConflictDecision.AcceptClient),
            "reject" => new G9CConflictResolution(row.Key, G9EConflictDecision.Reject, RejectCode, "Rejected by the app resolver."),
            _ => new G9CConflictResolution(row.Key, G9EConflictDecision.KeepServer)
        }).ToList();
        return ValueTask.FromResult(decisions);
    }
}

internal sealed class MinimumVersionGate : IG9SyncConnectGate
{
    public const string OutdatedVersion = "0.9";

    public ValueTask<G9DtSyncError?> CheckAsync(G9CSyncCaller caller, G9DtHelloRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(request.AppVersion == OutdatedVersion
            ? new G9DtSyncError { Code = G9SyncErrorCodes.UpgradeRequired, Message = "This app version is no longer supported." }
            : null);
}

/// <summary>Records progress synchronously, in publication order.</summary>
internal sealed class RecordingProgress : IProgress<G9DtSyncProgress>
{
    private readonly ConcurrentQueue<G9DtSyncProgress> _items = new();

    public IReadOnlyList<G9DtSyncProgress> Items => _items.ToArray();

    public void Report(G9DtSyncProgress value) => _items.Enqueue(value);
}

internal static class Wait
{
    public static async Task UntilAsync(Func<Task<bool>> condition, TimeSpan timeout, string because)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"Timed out after {timeout.TotalSeconds:N0} s waiting until {because}.");
            await Task.Delay(50);
        }
    }

    public static Task UntilAsync(Func<bool> condition, TimeSpan timeout, string because) =>
        UntilAsync(() => Task.FromResult(condition()), timeout, because);
}
