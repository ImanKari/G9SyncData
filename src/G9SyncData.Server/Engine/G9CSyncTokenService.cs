using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using G9SyncData.Protocol;

namespace G9SyncData.Server.Engine;

/// <summary>
///     Signs and verifies pull tokens (HMAC-SHA256). A token names every part of a planned pull, so ANY server instance
///     holding the same key can serve or resume it: no session store (Dotmim's in-memory session, P-13).
/// </summary>
internal sealed class G9CSyncTokenService
{
    private readonly byte[] _key;
    private readonly TimeSpan _lifetime;

    public G9CSyncTokenService(byte[]? key, TimeSpan lifetime)
    {
        _key = key is { Length: >= 32 } ? key : RandomNumberGenerator.GetBytes(32);
        _lifetime = lifetime;
    }

    public string Create(G9DtPullTokenPayload payload)
    {
        payload.ExpiresUnixMs = DateTimeOffset.UtcNow.Add(_lifetime).ToUnixTimeMilliseconds();
        var json = JsonSerializer.SerializeToUtf8Bytes(payload, G9SyncServerJsonContext.Default.G9DtPullTokenPayload);
        var mac = HMACSHA256.HashData(_key, json);
        return Base64Url.EncodeToString(json) + "." + Base64Url.EncodeToString(mac);
    }

    public G9DtPullTokenPayload Read(string? token)
    {
        if (string.IsNullOrEmpty(token)) throw Invalid("Missing pull token.");
        var separator = token.IndexOf('.', StringComparison.Ordinal);
        if (separator <= 0) throw Invalid("Malformed pull token.");

        byte[] json;
        byte[] mac;
        try
        {
            json = Base64Url.DecodeFromChars(token.AsSpan(0, separator));
            mac = Base64Url.DecodeFromChars(token.AsSpan(separator + 1));
        }
        catch (FormatException)
        {
            throw Invalid("Malformed pull token.");
        }

        if (!CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(_key, json), mac)) throw Invalid("Pull token signature is invalid.");

        var payload = JsonSerializer.Deserialize(json, G9SyncServerJsonContext.Default.G9DtPullTokenPayload) ?? throw Invalid("Empty pull token.");
        if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() > payload.ExpiresUnixMs)
        {
            throw new G9CSyncException(G9SyncErrorCodes.TokenExpired, "The pull token expired; plan the pull again.");
        }

        return payload;
    }

    private static G9CSyncException Invalid(string message) => new(G9SyncErrorCodes.TokenInvalid, message);
}

internal sealed class G9DtPullTokenPayload
{
    public Guid ClientId { get; set; }
    public string? UserId { get; set; }
    public Guid Epoch { get; set; }
    public long ExpiresUnixMs { get; set; }
    public string NewSnapshot { get; set; } = string.Empty;
    public List<G9DtTokenSubscription> Subscriptions { get; set; } = [];
    public List<G9DtTokenPart> Parts { get; set; } = [];
}

internal sealed class G9DtTokenSubscription
{
    public string Name { get; set; } = string.Empty;
    public Dictionary<string, string> Parameters { get; set; } = new(StringComparer.Ordinal);
    public string AuthHash { get; set; } = string.Empty;
}

internal sealed class G9DtTokenPart
{
    public int Subscription { get; set; }
    public string Table { get; set; } = string.Empty;
    public G9EPullPartKind Kind { get; set; }
    public string? PreviousSnapshot { get; set; }
    public List<string> Columns { get; set; } = [];
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(G9DtPullTokenPayload))]
internal sealed partial class G9SyncServerJsonContext : JsonSerializerContext;
