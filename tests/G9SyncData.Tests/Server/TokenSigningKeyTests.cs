using G9SyncData.Server;
using G9SyncData.Server.Engine;

namespace G9SyncData.Tests.Server;

/// <summary>
///     A pull token is only resumable on another node because every node signs with the same key. A key that was configured but
///     cannot be used has to say so at startup rather than be quietly replaced (ADR-0050).
/// </summary>
public sealed class TokenSigningKeyTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    [InlineData(31)]
    public void A_configured_key_that_is_too_short_fails_instead_of_being_replaced(int length)
    {
        var exception = Assert.Throws<ArgumentException>(() => new G9CSyncTokenService(new byte[length], TimeSpan.FromHours(2)));
        Assert.Contains(nameof(G9CSyncServerOptions.TokenSigningKey), exception.Message, StringComparison.Ordinal);
        Assert.Contains(length.ToString(System.Globalization.CultureInfo.InvariantCulture), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void No_key_at_all_stays_a_single_instance_choice_not_an_error()
    {
        var service = new G9CSyncTokenService(null, TimeSpan.FromHours(2));
        var payload = new G9DtPullTokenPayload { ClientId = Guid.NewGuid() };

        Assert.Equal(payload.ClientId, service.Read(service.Create(payload)).ClientId);
    }

    [Fact]
    public void Nodes_sharing_a_key_accept_each_others_tokens_and_nodes_that_do_not_reject_them()
    {
        var key = new byte[32];
        Random.Shared.NextBytes(key);
        var token = new G9CSyncTokenService(key, TimeSpan.FromHours(2)).Create(new G9DtPullTokenPayload { ClientId = Guid.NewGuid() });

        Assert.NotNull(new G9CSyncTokenService([.. key], TimeSpan.FromHours(2)).Read(token));
        Assert.Throws<G9CSyncException>(() => new G9CSyncTokenService(null, TimeSpan.FromHours(2)).Read(token));
    }

    [Fact]
    public void After_a_rotation_tokens_signed_with_the_previous_key_are_still_accepted_and_new_ones_use_the_new_key()
    {
        // ADR-0064. Without the previous key, rotating the signing key discards every pull that was planned a minute earlier.
        var old = NewKey();
        var current = NewKey();
        var planned = new G9CSyncTokenService(old, TimeSpan.FromHours(2)).Create(new G9DtPullTokenPayload { ClientId = Guid.NewGuid() });

        var rotated = new G9CSyncTokenService(current, TimeSpan.FromHours(2), [old]);
        Assert.NotNull(rotated.Read(planned));

        // Signing only ever uses the current key: a node that has dropped the old key still accepts what the rotated node issues.
        var issued = rotated.Create(new G9DtPullTokenPayload { ClientId = Guid.NewGuid() });
        Assert.NotNull(new G9CSyncTokenService(current, TimeSpan.FromHours(2)).Read(issued));
        Assert.Throws<G9CSyncException>(() => new G9CSyncTokenService(old, TimeSpan.FromHours(2)).Read(issued));

        // And a key that was never configured anywhere is still refused.
        var foreign = new G9CSyncTokenService(NewKey(), TimeSpan.FromHours(2)).Create(new G9DtPullTokenPayload { ClientId = Guid.NewGuid() });
        Assert.Equal(G9SyncErrorCodes.TokenInvalid, Assert.Throws<G9CSyncException>(() => rotated.Read(foreign)).Code);
    }

    [Fact]
    public void A_previous_key_that_is_too_short_fails_at_startup()
    {
        var exception = Assert.Throws<ArgumentException>(() => new G9CSyncTokenService(NewKey(), TimeSpan.FromHours(2), [new byte[8]]));
        Assert.Contains(nameof(G9CSyncServerOptions.PreviousTokenSigningKeys), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_host_that_requires_a_signing_key_does_not_start_without_one()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => new G9CSyncTokenService(null, TimeSpan.FromHours(2), requireKey: true));
        Assert.Contains(nameof(G9CSyncServerOptions.TokenSigningKey), exception.Message, StringComparison.Ordinal);

        Assert.False(new G9CSyncTokenService(NewKey(), TimeSpan.FromHours(2), requireKey: true).IsEphemeralKey);
        Assert.True(new G9CSyncTokenService(null, TimeSpan.FromHours(2)).IsEphemeralKey);
    }

    private static byte[] NewKey()
    {
        var key = new byte[32];
        Random.Shared.NextBytes(key);
        return key;
    }
}
