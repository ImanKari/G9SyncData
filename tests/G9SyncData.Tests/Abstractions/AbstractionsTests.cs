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
