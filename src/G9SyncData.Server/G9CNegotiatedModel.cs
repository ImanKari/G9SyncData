using System.Security.Cryptography;
using System.Text.Json;
using G9SyncData.Protocol;

namespace G9SyncData.Server;

/// <summary>
///     One client model as this server negotiated it (1.5.21, TR-0069): its id, the tables both sides agreed on, the tables the server
///     does not have, and the id of that answer. Shared by every Hello that names the model; never modified after it is built.
/// </summary>
internal sealed class G9CNegotiatedModel
{
    public G9CNegotiatedModel(string modelHash, List<G9DtNegotiatedTable> tables, List<string> unknownTables)
    {
        ModelHash = modelHash;
        Tables = tables;
        UnknownTables = unknownTables;
        var answer = JsonSerializer.SerializeToUtf8Bytes(tables, G9SyncJsonContext.Default.ListG9DtNegotiatedTable)
            .Concat(JsonSerializer.SerializeToUtf8Bytes(unknownTables, G9SyncJsonContext.Default.ListString)).ToArray();
        TablesHash = Hash(answer);
    }

    public string ModelHash { get; }
    public IReadOnlyList<G9DtNegotiatedTable> Tables { get; }
    public IReadOnlyList<string> UnknownTables { get; }
    public string TablesHash { get; }

    /// <summary>An id of a serialised value: the first 32 hex digits of its SHA-256.</summary>
    public static string Hash(byte[] serialized) => Convert.ToHexStringLower(SHA256.HashData(serialized))[..32];
}
