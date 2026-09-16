namespace G9SyncData.Protocol;

/// <summary>Protocol constants shared by every transport.</summary>
public static class G9SyncProtocol
{
    /// <summary>Wire protocol version. The server refuses a Hello from a different major version.</summary>
    public const int Version = 1;

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
