using System.Text.Json.Serialization;
using G9SyncData.Protocol;

namespace G9SyncData.Server.SnapshotFiles;

/// <summary>
///     What the cache keeps about one built file, as <c>{fileId}.json</c> beside its segment container. Written last, after the container is
///     complete: a record on disk always describes a whole file.
/// </summary>
internal sealed class G9CSnapshotFileRecord
{
    public string FileId { get; set; } = string.Empty;
    public string Identity { get; set; } = string.Empty;
    public string Snapshot { get; set; } = string.Empty;
    public Guid Epoch { get; set; }
    public DateTime SnapshotUtc { get; set; }
    public DateTime BuiltUtc { get; set; }

    /// <summary>The container's file name (content-addressed: <c>{fileId}-{sha256 prefix}.g9seg</c>).</summary>
    public string Container { get; set; } = string.Empty;

    public long Rows { get; set; }
    public long CompressedBytes { get; set; }
    public Dictionary<int, long> RowsByTable { get; set; } = [];
    public G9DtSnapshotFileManifest Manifest { get; set; } = new();
    public List<G9CSnapshotFileSegmentRecord> Segments { get; set; } = [];
}

internal sealed class G9CSnapshotFileSegmentRecord
{
    public long Offset { get; set; }
    public int Length { get; set; }
    public int RawLength { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public int Rows { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(G9CSnapshotFileRecord))]
internal sealed partial class G9CSnapshotFileJsonContext : JsonSerializerContext;
