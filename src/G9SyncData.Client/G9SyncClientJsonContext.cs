using System.Text.Json.Serialization;
using G9SyncData.Client.Storage;

namespace G9SyncData.Client;

/// <summary>Source-generated JSON metadata for client-side persisted types (NativeAOT-safe).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(G9CSyncRunReport))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(Dictionary<string, double>))]
public sealed partial class G9SyncClientJsonContext : JsonSerializerContext;
