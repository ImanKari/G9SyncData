using System.Text.Json.Serialization;

namespace G9SyncData.Protocol;

/// <summary>
///     Source-generated JSON metadata for every wire DTO. Transports insert this at the head of the hub protocol's
///     <c>TypeInfoResolverChain</c>, which is what keeps serialization working after trimming / NativeAOT
///     (the reflection resolver's metadata is stripped in a trimmed build).
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(G9DtHelloRequest))]
[JsonSerializable(typeof(G9DtHelloResult))]
[JsonSerializable(typeof(G9DtPushRequest))]
[JsonSerializable(typeof(G9DtPushResult))]
[JsonSerializable(typeof(G9DtPullRequest))]
[JsonSerializable(typeof(G9DtPullPlan))]
[JsonSerializable(typeof(G9DtPullContinuation))]
[JsonSerializable(typeof(G9DtSyncChunk))]
[JsonSerializable(typeof(G9DtReconcileRequest))]
[JsonSerializable(typeof(G9DtReconcileSummary))]
[JsonSerializable(typeof(G9DtFetchRowsRequest))]
[JsonSerializable(typeof(G9DtChangesAvailable))]
[JsonSerializable(typeof(G9DtSyncError))]
[JsonSerializable(typeof(G9DtSnapshotFileRequest))]
[JsonSerializable(typeof(G9DtSnapshotFileManifest))]
[JsonSerializable(typeof(G9DtFileSegment))]
[JsonSerializable(typeof(G9DtPlannedFile))]
[JsonSerializable(typeof(List<G9DtSubscriptionDefinition>))]
[JsonSerializable(typeof(List<G9DtNegotiatedTable>))]
[JsonSerializable(typeof(List<G9DtSubscriptionCursor>))]
[JsonSerializable(typeof(List<G9DtPlannedPart>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(bool))]
public sealed partial class G9SyncJsonContext : JsonSerializerContext;
