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
    private readonly byte[][] _previousKeys;
    private readonly TimeSpan _lifetime;

    public G9CSyncTokenService(byte[]? key, TimeSpan lifetime, IReadOnlyList<byte[]>? previousKeys = null, bool requireKey = false)
    {
        // No key is a deliberate choice: one instance, tokens that need not outlive the process. A key that was configured but is too
        // short to use is not - silently signing with a random one instead leaves every node rejecting the others' tokens, discovered
        // only in production behind a load balancer (ADR-0050).
        if (key is not null && key.Length < 32)
        {
            throw new ArgumentException(
                $"{nameof(G9CSyncServerOptions.TokenSigningKey)} must be at least 32 bytes; {key.Length} were configured.", nameof(key));
        }

        // A host that says it needs one and has none fails HERE, at startup, not at the first deploy that cuts a field worker's
        // half-downloaded site off (ADR-0064): with a random key every restart invalidates every pull in flight.
        if (key is null && requireKey)
        {
            throw new InvalidOperationException(
                $"{nameof(G9CSyncServerOptions.RequireTokenSigningKey)} is set and no {nameof(G9CSyncServerOptions.TokenSigningKey)} is configured. " +
                "Configure at least 32 random bytes from the secret store: without it every restart invalidates the pulls in flight.");
        }

        foreach (var previous in previousKeys ?? [])
        {
            if (previous is null || previous.Length < 32)
            {
                throw new ArgumentException(
                    $"Every key in {nameof(G9CSyncServerOptions.PreviousTokenSigningKeys)} must be at least 32 bytes; {previous?.Length ?? 0} were configured.",
                    nameof(previousKeys));
            }
        }

        IsEphemeralKey = key is null;
        _key = key ?? RandomNumberGenerator.GetBytes(32);
        _previousKeys = previousKeys is null ? [] : [.. previousKeys];
        _lifetime = lifetime;
    }

    /// <summary>True when no key was configured and this process signs with a random one: tokens do not survive a restart or another instance.</summary>
    public bool IsEphemeralKey { get; }

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

        if (!IsSignedByAKnownKey(json, mac)) throw Invalid("Pull token signature is invalid.");

        var payload = JsonSerializer.Deserialize(json, G9SyncServerJsonContext.Default.G9DtPullTokenPayload) ?? throw Invalid("Empty pull token.");
        if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() > payload.ExpiresUnixMs)
        {
            throw new G9CSyncException(G9SyncErrorCodes.TokenExpired, "The pull token expired; plan the pull again.");
        }

        return payload;
    }

    /// <summary>
    ///     The current key or any previous one (ADR-0064). Tokens are only ever SIGNED with the current key; the previous ones exist so
    ///     that rotating the key does not invalidate the pulls that were planned a minute before. Every key is tried, without an early
    ///     exit, so the time taken does not say which one matched - or whether any did.
    /// </summary>
    private bool IsSignedByAKnownKey(byte[] json, byte[] mac)
    {
        var known = CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(_key, json), mac);
        foreach (var previous in _previousKeys) known |= CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(previous, json), mac);
        return known;
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

    /// <summary>The frame layout negotiated when the pull was planned (0 in tokens from before ADR-0040 = layout 1).</summary>
    public int FrameVersion { get; set; }
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

    /// <summary>
    ///     The file of a <see cref="G9EPullPartKind.SnapshotFile" /> part: everything needed to build it again, so the pull - or its resume -
    ///     can be served by any instance (ADR-0072).
    /// </summary>
    public G9DtTokenFile? File { get; set; }
}

internal sealed class G9DtTokenFile
{
    public string FileId { get; set; } = string.Empty;
    public string Identity { get; set; } = string.Empty;
    public string Snapshot { get; set; } = string.Empty;
    public DateTime SnapshotUtc { get; set; }
    public G9DtSnapshotFileStorage Storage { get; set; } = new();
    public List<G9DtTokenFileTable> Tables { get; set; } = [];
}

internal sealed class G9DtTokenFileTable
{
    public string Table { get; set; } = string.Empty;
    public List<string> Columns { get; set; } = [];
    public G9DtSnapshotFileTable Shape { get; set; } = new();
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(G9DtPullTokenPayload))]
internal sealed partial class G9SyncServerJsonContext : JsonSerializerContext;
