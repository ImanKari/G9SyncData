using PolyType;

namespace G9SyncData.Protocol;

/// <summary>
///     Source-generated type shapes of every hub argument, result and stream item, for the MessagePack hub protocol. The binary
///     protocol carries <see cref="G9DtSyncChunk.Payload" /> as raw bytes instead of base64 text (+33%) and is NativeAOT-safe because
///     the shapes are generated at build time. Pass <c>G9SyncWireShapes.GeneratedTypeShapeProvider</c> to <c>AddMessagePackProtocol</c>.
/// </summary>
[GenerateShapeFor<G9DtHelloRequest>]
[GenerateShapeFor<G9DtHelloResult>]
[GenerateShapeFor<G9DtPushRequest>]
[GenerateShapeFor<G9DtPushResult>]
[GenerateShapeFor<G9DtPullRequest>]
[GenerateShapeFor<G9DtPullPlan>]
[GenerateShapeFor<G9DtPullContinuation>]
[GenerateShapeFor<G9DtSyncChunk>]
[GenerateShapeFor<G9DtReconcileRequest>]
[GenerateShapeFor<G9DtFetchRowsRequest>]
[GenerateShapeFor<G9DtChangesAvailable>]
[GenerateShapeFor<string>]
public sealed partial class G9SyncWireShapes;
