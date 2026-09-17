namespace G9SyncData.Protocol;

/// <summary>Protocol constants shared by every transport.</summary>
public static class G9SyncProtocol
{
    /// <summary>Wire protocol version. The server refuses a Hello from a different major version.</summary>
    public const int Version = 1;

    /// <summary>
    ///     The newest G9RowBatch body layout this build reads and writes (1 row-wise, 2 column-grouped; ADR-0040). A request states the
    ///     newest layout its sender reads; <see cref="NegotiateFrameVersion" /> picks what to answer with.
    /// </summary>
    public const int MaxFrameVersion = 2;

    /// <summary>The frame layout to answer a request with: the newest both sides know, and 1 for a sender that did not say.</summary>
    public static int NegotiateFrameVersion(int requested) => Math.Clamp(requested, 1, MaxFrameVersion);

    /// <summary>Default SignalR hub route.</summary>
    public const string DefaultHubRoute = "/g9sync";

    /// <summary>The SignalR group of connections that negotiated a table; pokes about the table go to it.</summary>
    public static string TableGroup(string logicalTable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalTable);
        return "g9sync:" + logicalTable.ToLowerInvariant();
    }

    /// <summary>Hub method names (client → server).</summary>
    public static class Methods
    {
        public const string Hello = nameof(Hello);
        public const string Push = nameof(Push);
        public const string PlanPull = nameof(PlanPull);
        public const string Pull = nameof(Pull);
        public const string Reconcile = nameof(Reconcile);
        public const string FetchRows = nameof(FetchRows);
    }

    /// <summary>Listener method names (server → client).</summary>
    public static class Listeners
    {
        public const string OnChangesAvailable = nameof(OnChangesAvailable);
    }
}
