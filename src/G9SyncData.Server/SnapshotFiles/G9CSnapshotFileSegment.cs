using G9SyncData.Protocol;

namespace G9SyncData.Server.SnapshotFiles;

/// <summary>One segment of a file on its way to a device.</summary>
/// <param name="Segment">What the chunk says about the segment (without its manifest; the engine attaches that to the first chunk it sends).</param>
/// <param name="Manifest">The whole file.</param>
/// <param name="Compressed">The segment, Brotli-compressed on its own.</param>
/// <param name="Rows">This segment's share of the file's rows, for progress.</param>
public sealed record G9CSnapshotFileSegment(G9DtFileSegment Segment, G9DtSnapshotFileManifest Manifest, byte[] Compressed, int Rows);
