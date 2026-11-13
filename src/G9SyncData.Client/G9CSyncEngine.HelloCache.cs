using G9SyncData.Protocol;

namespace G9SyncData.Client;

/// <summary>
///     What Hello does not send again (1.5.21, AgriPad TR-0069): the negotiated tables and the definitions the server answered last, kept
///     in memory with the server's ids for them. The next Hello names the ids; a server whose answer is the same says
///     <see cref="G9DtHelloResult.TablesUnchanged" /> / <see cref="G9DtHelloResult.DefinitionsUnchanged" /> and leaves them out, and this
///     engine puts the kept ones back before anything reads the answer - so the rest of a run sees a complete Hello either way. The first
///     Hello of a process always gets them in full (nothing is kept across restarts), which also re-logs unknown tables once per start.
/// </summary>
public sealed partial class G9CSyncEngine
{
    private sealed record HelloAnswer(string? TablesHash, List<G9DtNegotiatedTable> Tables, List<string> UnknownTables, string? DefinitionsHash,
        List<G9DtSubscriptionDefinition> Definitions);

    private HelloAnswer? _lastAnswer;
    private List<G9DtClientTableModel>? _wireTables;

    /// <summary>The model as Hello sends it, built once: the transport recognises an unchanged model by this very list.</summary>
    private List<G9DtClientTableModel> WireTables => _wireTables ??= _model.Tables.Select(t => t.ToWire()).ToList();

    /// <summary>Puts back what an "unchanged" answer left out. An answer that says unchanged for something never kept is a server fault.</summary>
    private static void RestoreUnchanged(G9DtHelloResult hello, HelloAnswer? known)
    {
        if (hello.TablesUnchanged)
        {
            if (known is null) throw new G9CSyncException(G9SyncErrorCodes.ProtocolMismatch, "The server answered 'tables unchanged' to a client that holds none.");
            hello.Tables = known.Tables;
            hello.UnknownTables = known.UnknownTables;
        }

        if (hello.DefinitionsUnchanged)
        {
            if (known is null) throw new G9CSyncException(G9SyncErrorCodes.ProtocolMismatch, "The server answered 'definitions unchanged' to a client that holds none.");
            hello.Definitions = known.Definitions;
        }
    }

    private void RememberAnswer(G9DtHelloResult hello) =>
        Volatile.Write(ref _lastAnswer, string.IsNullOrEmpty(hello.TablesHash) && string.IsNullOrEmpty(hello.DefinitionsHash)
            ? null
            : new HelloAnswer(hello.TablesHash, hello.Tables, hello.UnknownTables, hello.DefinitionsHash, hello.Definitions));
}
