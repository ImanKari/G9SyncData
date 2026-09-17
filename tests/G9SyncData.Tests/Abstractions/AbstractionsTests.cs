using System.Text.Json;
using G9SyncData.Protocol;

namespace G9SyncData.Tests.Abstractions;

public sealed class DigestTests
{
    // Golden values computed by PostgreSQL 18 with the exact SQL the server provider uses:
    //   get_byte(decode(md5(lower(id::text)), 'hex'), 0)
    //   ('x' || left(md5(lower(id::text) || ':' || ver::text), 16))::bit(64)::bigint
    [Theory]
    [InlineData("aaaaaaaa-0000-0000-0000-000000000001", 5L, 3, -2463579834790192429L)]
    [InlineData("bbbbbbbb-0000-0000-0000-000000000002", 4L, 181, 3058123743801994473L)]
    public void Csharp_digest_matches_postgres(string key, long version, int bucket, long rowHash)
    {
        Assert.Equal(bucket, G9CSyncDigest.GetBucket(key));
        Assert.Equal(rowHash, G9CSyncDigest.GetRowHash(key, version));
        Assert.Equal(bucket, G9CSyncDigest.GetBucket(System.Text.Encoding.UTF8.GetBytes(key)));
        Assert.Equal(rowHash, G9CSyncDigest.GetRowHash(System.Text.Encoding.UTF8.GetBytes(key), version));
    }

    [Theory]
    [InlineData("8f3eb2a1-0c4d-4e5f-9a6b-7c8d9e0f1a2b", 0L)]
    [InlineData("9007199254740993", long.MaxValue)]
    [InlineData("Key With Spaces ۱ and a long tail ....................................................................................................................................................................................................................................................................", 42L)]
    public void The_allocation_free_digest_equals_the_string_digest(string key, long version)
    {
        var utf8 = System.Text.Encoding.UTF8.GetBytes(key);
        Assert.Equal(G9CSyncDigest.GetBucket(key), G9CSyncDigest.GetBucket(utf8));
        Assert.Equal(G9CSyncDigest.GetRowHash(key, version), G9CSyncDigest.GetRowHash(utf8, version));
    }

    [Theory]
    [InlineData("", "d41d8cd98f00b204e9800998ecf8427e")]
    [InlineData("a", "0cc175b9c0f1b6a831c399e269772661")]
    [InlineData("abc", "900150983cd24fb0d6963f7d28e17f72")]
    [InlineData("message digest", "f96b697d7cb7938d525a2f31aaf161d0")]
    [InlineData("abcdefghijklmnopqrstuvwxyz", "c3fcd3d76192e4007dfb496cca67e13b")]
    [InlineData("12345678901234567890123456789012345678901234567890123456789012345678901234567890", "57edf4a22be3c955ac49da2e2107b67a")]
    public void The_managed_md5_matches_rfc_1321(string text, string expected)
    {
        Span<byte> hash = stackalloc byte[16];
        G9CMd5.HashData(System.Text.Encoding.ASCII.GetBytes(text), hash);
        Assert.Equal(expected, Convert.ToHexStringLower(hash));
    }

    [Fact]
    public void The_managed_md5_matches_the_platform_md5_for_every_length_that_crosses_a_block()
    {
        var random = new Random(20260917);
        Span<byte> managed = stackalloc byte[16];
        for (var length = 0; length <= 300; length++)
        {
            var data = new byte[length];
            random.NextBytes(data);
            G9CMd5.HashData(data, managed);
#pragma warning disable CA5351 // the reference implementation for a test
            Assert.Equal(System.Security.Cryptography.MD5.HashData(data), managed.ToArray());
#pragma warning restore CA5351
        }
    }

    [Fact]
    public void Computed_values_change_the_row_hash()
    {
        var key = G9CSyncKey.FromGuid(Guid.NewGuid());
        Assert.NotEqual(G9CSyncDigest.GetRowHash(key, 1, ["1"]), G9CSyncDigest.GetRowHash(key, 1, ["0"]));
        Assert.Equal(G9CSyncDigest.GetRowHash(key, 1, [null]), G9CSyncDigest.GetRowHash(key, 1, [""]));
    }

    [Fact]
    public void Canonical_text_of_booleans_and_guids_matches_the_sql_encoding()
    {
        Assert.Equal("1", G9CSyncDigest.CanonicalText(G9ESyncColumnType.Boolean, true));
        Assert.Equal("0", G9CSyncDigest.CanonicalText(G9ESyncColumnType.Boolean, 0L));
        Assert.Equal("aaaaaaaa-0000-0000-0000-000000000001",
            G9CSyncDigest.CanonicalText(G9ESyncColumnType.Guid, "AAAAAAAA-0000-0000-0000-000000000001"));
        Assert.Null(G9CSyncDigest.CanonicalText(G9ESyncColumnType.String, null));
    }
}

public sealed class KeyAndParameterTests
{
    [Fact]
    public void Guid_keys_normalise_to_lower_case()
    {
        Assert.Equal("aaaaaaaa-0000-0000-0000-000000000001", G9CSyncKey.Normalize(G9ESyncColumnType.Guid, "{AAAAAAAA-0000-0000-0000-000000000001}"));
        Assert.Equal("-12", G9CSyncKey.Normalize(G9ESyncColumnType.Int64, "-0012"));
        Assert.False(G9CSyncKey.TryNormalize(G9ESyncColumnType.Guid, "nope", out _));
    }

    [Fact]
    public void Two_spellings_of_the_same_parameters_build_the_same_key()
    {
        var id = Guid.NewGuid();
        var a = new G9CSyncParameters().Set("FarmId", id).SetList("Ids", [3L, 1L, 3L]);
        var b = G9CSyncParameters.FromValues(new Dictionary<string, string>
        {
            ["Ids"] = "1,3",
            ["FarmId"] = id.ToString()
        });

        Assert.Equal(G9CSyncParameters.BuildSubscriptionKey("site-farm", a), G9CSyncParameters.BuildSubscriptionKey("site-farm", b));
        Assert.Equal("site-farm", G9CSyncParameters.BuildSubscriptionKey("site-farm", null));
        Assert.StartsWith("site-farm?FarmId=", G9CSyncParameters.BuildSubscriptionKey("site-farm", a), StringComparison.Ordinal);
    }

    [Fact]
    public void Guid_lists_are_sorted_deduplicated_and_lower_case()
    {
        var p = new G9CSyncParameters().SetList("Ids",
        [
            Guid.Parse("BBBBBBBB-0000-0000-0000-000000000002"), Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"),
            Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002")
        ]);
        Assert.Equal("aaaaaaaa-0000-0000-0000-000000000001,bbbbbbbb-0000-0000-0000-000000000002", p.Values["Ids"]);
    }
}

public sealed class JsonContextTests
{
    [Fact]
    public void Wire_dtos_round_trip_through_the_source_generated_context()
    {
        var hello = new G9DtHelloResult
        {
            Epoch = Guid.NewGuid(),
            ServerTimeUtc = DateTime.UtcNow,
            Tables = [new G9DtNegotiatedTable { TableId = 3, Name = "demo.Pot", KeyColumn = "Id", KeyType = G9ESyncColumnType.Guid, Columns = [new G9DtColumnModel { Name = "Index", Type = G9ESyncColumnType.Int32 }] }],
            Subscriptions = [new G9DtSubscriptionState { Key = "farm?FarmId=x", Action = G9ESubscriptionAction.Reconcile }]
        };

        var json = JsonSerializer.Serialize(hello, G9SyncJsonContext.Default.G9DtHelloResult);
        var back = JsonSerializer.Deserialize(json, G9SyncJsonContext.Default.G9DtHelloResult)!;
        Assert.Equal(hello.Epoch, back.Epoch);
        Assert.Equal("demo.Pot", back.Tables[0].Name);
        Assert.Equal(G9ESubscriptionAction.Reconcile, back.Subscriptions[0].Action);
        Assert.Contains("\"tableId\":3", json, StringComparison.Ordinal);

        var chunk = new G9DtSyncChunk { Sequence = 2, Payload = [1, 2, 3], Continuation = new G9DtPullContinuation { PartIndex = 1, AfterKey = "k" } };
        var chunkBack = JsonSerializer.Deserialize(JsonSerializer.Serialize(chunk, G9SyncJsonContext.Default.G9DtSyncChunk), G9SyncJsonContext.Default.G9DtSyncChunk)!;
        Assert.Equal(new byte[] { 1, 2, 3 }, chunkBack.Payload);
        Assert.Equal("k", chunkBack.Continuation!.AfterKey);
    }
}
