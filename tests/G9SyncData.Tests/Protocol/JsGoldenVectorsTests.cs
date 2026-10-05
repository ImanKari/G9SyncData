using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using G9SyncData.Client.Model;
using G9SyncData.Client.Sqlite;
using G9SyncData.Client.Sqlite.Internal;
using G9SyncData.Client.Sqlite.Native;
using G9SyncData.Protocol;
using G9SyncData.Protocol.RowBatch;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.Options;
using G9SignalRSuperNetCore.Client.MessagePack;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace G9SyncData.Tests.Protocol;

/// <summary>
///     The .NET half of the TypeScript parity contract (js/packages/syncdata/PARITY.md). Everything the TypeScript twin must reproduce to
///     the byte - varints, MD5 buckets and row hashes, canonical texts, subscription keys, G9RowBatch frames in both layouts, the local
///     schema SQL and model hashes, storage text forms, and the hub messages of both SignalR protocols - is generated here into
///     js/packages/syncdata/test/golden/dotnet-vectors.json, which the TypeScript tests read. The other direction - what the TypeScript
///     client sends - is js-vectors.json, written by the TypeScript tests and parsed here with the server's own hub protocols.
/// </summary>
/// <remarks>Set G9_UPDATE_GOLDEN=1 to rewrite dotnet-vectors.json after an intended change (and commit it with the change).</remarks>
public sealed class JsGoldenVectorsTests
{
    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true, NewLine = "\n", Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    [Fact]
    public void Dotnet_vectors_are_current()
    {
        var json = Build().ToJsonString(Indented) + "\n";
        var path = GoldenPath("dotnet-vectors.json");
        if (Environment.GetEnvironmentVariable("G9_UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, json, new UTF8Encoding(false));
            return;
        }

        Assert.True(File.Exists(path), $"{path} is missing: run this test once with G9_UPDATE_GOLDEN=1.");
        Assert.True(json == File.ReadAllText(path).ReplaceLineEndings("\n"),
            "dotnet-vectors.json is stale: a wire or storage form changed. If that is intended, rerun with G9_UPDATE_GOLDEN=1, then make the " +
            "TypeScript twin pass (npm test in js/) in the same change.");
    }

    [Fact]
    public void Requests_the_TypeScript_client_writes_deserialize_on_the_server()
    {
        var path = GoldenPath("js-vectors.json");
        Assert.True(File.Exists(path), $"{path} is missing: the TypeScript tests write it (npm test in js/packages/syncdata).");
        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var binder = new SyncBinder();
        var protocols = new (string Name, IHubProtocol Protocol, Func<JsonNode, byte[]> Bytes)[]
        {
            ("json", JsonProtocol(), node => Encoding.UTF8.GetBytes(node.GetValue<string>())),
            ("messagepack", MessagePackProtocol(), node => Convert.FromHexString(node.GetValue<string>()))
        };

        foreach (var (name, protocol, bytes) in protocols)
        {
            var messages = root["hub"]![name]!.AsObject();
            Assert.Equal(6, messages.Count);

            var hello = Parse<InvocationMessage>(protocol, binder, bytes(messages["hello"]!));
            Assert.Equal(G9SyncProtocol.Methods.Hello, hello.Target);
            AssertSame(JsGoldenSamples.HelloRequest(), hello.Arguments[0]);

            var push = Parse<InvocationMessage>(protocol, binder, bytes(messages["push"]!));
            Assert.Equal(G9SyncProtocol.Methods.Push, push.Target);
            Assert.Single(push.Arguments);
            AssertSame(JsGoldenSamples.PushRequest(), push.Arguments[0]);
            Assert.NotNull(push.StreamIds);
            var streamId = Assert.Single(push.StreamIds!);

            var item = Parse<StreamItemMessage>(protocol, binder, bytes(messages["pushItem"]!));
            Assert.Equal(streamId, item.InvocationId);
            AssertSame(JsGoldenSamples.PushChunk(), item.Item);

            var pull = Parse<StreamInvocationMessage>(protocol, binder, bytes(messages["pull"]!));
            Assert.Equal(G9SyncProtocol.Methods.Pull, pull.Target);
            Assert.Equal("tok.en", pull.Arguments[0]);
            AssertSame(JsGoldenSamples.Continuation(), pull.Arguments[1]);

            var reconcile = Parse<StreamInvocationMessage>(protocol, binder, bytes(messages["reconcile"]!));
            Assert.Equal(G9SyncProtocol.Methods.Reconcile, reconcile.Target);
            var request = Assert.IsType<G9DtReconcileRequest>(reconcile.Arguments[0]);
            Assert.Equal(long.MinValue, request.Tables[0].Digests[0]);
            Assert.Equal(9_007_199_254_740_993, request.Tables[0].Digests[2]);
            Assert.Equal(long.MaxValue, request.Tables[0].Digests[255]);
            AssertSame(JsGoldenSamples.ReconcileRequest(), request);

            var fetch = Parse<StreamInvocationMessage>(protocol, binder, bytes(messages["fetchRows"]!));
            Assert.Equal(G9SyncProtocol.Methods.FetchRows, fetch.Target);
            AssertSame(JsGoldenSamples.FetchRowsRequest(), fetch.Arguments[0]);
        }
    }

    // ================================================================ the vectors

    private static JsonObject Build() => new()
    {
        ["generator"] = "tests/G9SyncData.Tests/Protocol/JsGoldenVectorsTests.cs",
        ["varints"] = Varints(),
        ["zigzag"] = ZigZags(),
        ["md5"] = Md5s(),
        ["digests"] = Digests(),
        ["canonicalText"] = CanonicalTexts(),
        ["keys"] = Keys(),
        ["parameters"] = Parameters(),
        ["frames"] = Frames(),
        ["model"] = Model(),
        ["storage"] = Storage(),
        ["hub"] = Hub()
    };

    private static JsonArray Varints()
    {
        ulong[] values = [0, 1, 127, 128, 300, 16_383, 16_384, int.MaxValue, 1UL << 32, (1UL << 53) - 1, 1UL << 53, long.MaxValue, ulong.MaxValue];
        var array = new JsonArray();
        var buffer = new byte[G9Varint.MaxBytes];
        foreach (var value in values)
        {
            var length = G9Varint.Write(buffer, value);
            array.Add(new JsonObject { ["value"] = value.ToString(CultureInfo.InvariantCulture), ["hex"] = Hex(buffer.AsSpan(0, length)) });
        }

        return array;
    }

    private static JsonArray ZigZags()
    {
        long[] values = [0, -1, 1, -2, 2, int.MinValue, int.MaxValue, long.MinValue, long.MaxValue, 1L << 53, -(1L << 53), (1L << 52) - 1, -(1L << 52)];
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(new JsonObject
            {
                ["value"] = value.ToString(CultureInfo.InvariantCulture), ["zigzag"] = G9Varint.ZigZag(value).ToString(CultureInfo.InvariantCulture)
            });
        }

        return array;
    }

    private static JsonArray Md5s()
    {
        string[] texts = ["", "a", "abc", "message digest", new('x', 55), new('y', 56), new('z', 64), new('w', 119), new('v', 120), string.Concat(Enumerable.Repeat("Ünïcødé ✓ 😀 ", 90))];
        var array = new JsonArray();
        var hash = new byte[16];
        foreach (var text in texts)
        {
            G9CMd5.HashData(Encoding.UTF8.GetBytes(text), hash);
            array.Add(new JsonObject { ["text"] = text, ["hex"] = Hex(hash) });
        }

        return array;
    }

    private static JsonArray Digests()
    {
        string[] keys = ["0f8fad5b-d9cb-469f-a165-70867728950e", "42", "-7", "hello", "Ünïcødé ✓ 😀", ""];
        long[] versions = [0, 1, 123_456_789, long.MaxValue];
        string?[][] computed = [[], [null], ["1.5", "x"], ["", null]];
        var array = new JsonArray();
        foreach (var key in keys)
        {
            foreach (var version in versions)
            {
                foreach (var values in computed)
                {
                    var computedArray = new JsonArray();
                    foreach (var value in values) computedArray.Add(value is null ? null : JsonValue.Create(value));
                    array.Add(new JsonObject
                    {
                        ["key"] = key,
                        ["version"] = version.ToString(CultureInfo.InvariantCulture),
                        ["computed"] = computedArray,
                        ["bucket"] = G9CSyncDigest.GetBucket(key),
                        ["rowHash"] = G9CSyncDigest.GetRowHash(key, version, values).ToString(CultureInfo.InvariantCulture)
                    });
                }
            }
        }

        // The UTF-8 forms the client store uses (no computed columns).
        foreach (var key in keys)
        {
            array.Add(new JsonObject
            {
                ["key"] = key, ["version"] = "987654321", ["computed"] = new JsonArray(), ["bucket"] = G9CSyncDigest.GetBucket(Encoding.UTF8.GetBytes(key)),
                ["rowHash"] = G9CSyncDigest.GetRowHash(Encoding.UTF8.GetBytes(key), 987_654_321).ToString(CultureInfo.InvariantCulture)
            });
        }

        return array;
    }

    private static JsonArray CanonicalTexts()
    {
        var array = new JsonArray();
        void Add(G9ESyncColumnType type, string kind, JsonNode? value, object? clr)
        {
            array.Add(new JsonObject { ["type"] = (int)type, ["kind"] = kind, ["value"] = value, ["text"] = G9CSyncDigest.CanonicalText(type, clr) });
        }

        Add(G9ESyncColumnType.Boolean, "integer", "3", 3L);
        Add(G9ESyncColumnType.Boolean, "integer", "0", 0L);
        Add(G9ESyncColumnType.Int64, "integer", "9007199254740993", 9_007_199_254_740_993L);
        Add(G9ESyncColumnType.Int64, "integer", "-9223372036854775808", long.MinValue);
        Add(G9ESyncColumnType.Int32, "integer", "1000000000000000", 1_000_000_000_000_000L);
        double[] doubles =
        [
            0.1, 1e15, 1e14, 123_456_789_012_345_680.0, 12_345_678_901_234_567.0, 1e-5, 1e-4, -0.0, 0.0, 1.5, -2.25, double.MaxValue, double.Epsilon, 100.0,
            1e21, 0.30000000000000004, 5e-324, 1.7976931348623157e308, double.NaN, double.PositiveInfinity, double.NegativeInfinity, 123456.789e3, 1e16, 1e17
        ];
        foreach (var value in doubles) Add(G9ESyncColumnType.Double, "real", DoubleBits(value), value);
        Add(G9ESyncColumnType.Guid, "text", "0F8FAD5B-D9CB-469F-A165-70867728950E", "0F8FAD5B-D9CB-469F-A165-70867728950E");
        Add(G9ESyncColumnType.Guid, "text", "not-a-guid", "not-a-guid");
        Add(G9ESyncColumnType.String, "text", "Ünïcødé", "Ünïcødé");
        Add(G9ESyncColumnType.Bytes, "blob", "0102", new byte[] { 1, 2 });
        Add(G9ESyncColumnType.String, "null", null, null);
        return array;
    }

    private static JsonArray Keys()
    {
        (G9ESyncColumnType Type, string Text)[] cases =
        [
            (G9ESyncColumnType.Guid, "{0F8FAD5B-D9CB-469F-A165-70867728950E}"), (G9ESyncColumnType.Guid, "0f8fad5bd9cb469fa16570867728950e"),
            (G9ESyncColumnType.Guid, "(0f8fad5b-d9cb-469f-a165-70867728950e)"), (G9ESyncColumnType.Guid, "  0F8FAD5B-D9CB-469F-A165-70867728950E  "),
            (G9ESyncColumnType.Guid, "0f8fad5b-d9cb-469f-a165-70867728950"), (G9ESyncColumnType.Guid, "0f8fad5b-d9cb-469f-a165-70867728950g"),
            (G9ESyncColumnType.Int64, "+42"), (G9ESyncColumnType.Int64, "-0"), (G9ESyncColumnType.Int64, "0042"), (G9ESyncColumnType.Int64, " 42"),
            (G9ESyncColumnType.Int64, "9223372036854775807"), (G9ESyncColumnType.Int64, "9223372036854775808"), (G9ESyncColumnType.Int64, "-9223372036854775808"),
            (G9ESyncColumnType.Int32, "-17"), (G9ESyncColumnType.Int32, "1.5"), (G9ESyncColumnType.String, " Mixed Case "), (G9ESyncColumnType.String, "")
        ];
        var array = new JsonArray();
        foreach (var (type, text) in cases)
        {
            array.Add(new JsonObject
            {
                ["type"] = (int)type, ["text"] = text, ["canonical"] = G9CSyncKey.TryNormalize(type, text, out var canonical) ? canonical : null
            });
        }

        return array;
    }

    /// <summary>Parameter cases as data, applied by the same interpreter on both sides.</summary>
    private static JsonArray Parameters()
    {
        var cases = new JsonArray
        {
            ParameterCase("Site", Op("guid", "siteId", "0F8FAD5B-D9CB-469F-A165-70867728950E"), Op("int64", "year", "2026")),
            ParameterCase("Mixed",
                Op("guidList", "ids", new JsonArray("6f1c2a7e-3b4d-4e5f-8a9b-0c1d2e3f4a5b", "0F8FAD5B-D9CB-469F-A165-70867728950E", "6F1C2A7E-3B4D-4E5F-8A9B-0C1D2E3F4A5B")),
                Op("string", "name", "a b&c=d/é😀~*!'()%"), Op("bool", "flag", true), Op("int64List", "n", new JsonArray("3", "-1", "3", "10", "-9223372036854775808")),
                Op("stringList", "tags", new JsonArray("b", "a", "b", "Z", "ä")), Op("int32", "Ünï", "-5")),
            ParameterCase("Empty")
        };

        foreach (var item in cases)
        {
            var parameters = new G9CSyncParameters();
            foreach (var op in item!["ops"]!.AsArray())
            {
                var name = op!["name"]!.GetValue<string>();
                var value = op["value"]!;
                switch (op["op"]!.GetValue<string>())
                {
                    case "guid": parameters.Set(name, Guid.Parse(value.GetValue<string>())); break;
                    case "int64": parameters.Set(name, long.Parse(value.GetValue<string>(), CultureInfo.InvariantCulture)); break;
                    case "int32": parameters.Set(name, int.Parse(value.GetValue<string>(), CultureInfo.InvariantCulture)); break;
                    case "bool": parameters.Set(name, value.GetValue<bool>()); break;
                    case "string": parameters.Set(name, value.GetValue<string>()); break;
                    case "guidList": parameters.SetList(name, value.AsArray().Select(v => Guid.Parse(v!.GetValue<string>()))); break;
                    case "int64List": parameters.SetList(name, value.AsArray().Select(v => long.Parse(v!.GetValue<string>(), CultureInfo.InvariantCulture))); break;
                    case "stringList": parameters.SetList(name, value.AsArray().Select(v => v!.GetValue<string>())); break;
                    default: throw new InvalidOperationException("Unknown op.");
                }
            }

            item["canonical"] = parameters.ToCanonicalString();
            item["key"] = G9CSyncParameters.BuildSubscriptionKey(item["subscription"]!.GetValue<string>(), parameters);
        }

        return cases;

        static JsonObject Op(string op, string name, JsonNode value) => new() { ["op"] = op, ["name"] = name, ["value"] = value };

        static JsonObject ParameterCase(string subscription, params JsonObject[] ops)
        {
            var array = new JsonArray();
            foreach (var op in ops) array.Add(op);
            return new JsonObject { ["subscription"] = subscription, ["ops"] = array };
        }
    }

    // ---------------------------------------------------------------- frames

    private static JsonArray Frames()
    {
        var all = new JsonArray(Section(G9ERowBatchOp.Upsert, 3, G9ESyncColumnType.Guid, AllTypes, [.. UpsertRows()]),
            Section(G9ERowBatchOp.Tombstone, 300, G9ESyncColumnType.String, [], [KeyRow("k1", 5), KeyRow("ünï", 0)]),
            Section(G9ERowBatchOp.MoveOut, 1, G9ESyncColumnType.Int64, [], [KeyRow("-5", 7), KeyRow("9223372036854775807", 1)]),
            Section(G9ERowBatchOp.KeyVersions, 2, G9ESyncColumnType.Int32, [],
            [
                new JsonObject { ["key"] = "42", ["version"] = "9", ["rowHash"] = long.MinValue.ToString(CultureInfo.InvariantCulture) },
                new JsonObject { ["key"] = "-1", ["version"] = "0", ["rowHash"] = "123" }
            ]),
            Section(G9ERowBatchOp.Push, 4, G9ESyncColumnType.Guid, [G9ESyncColumnType.String, G9ESyncColumnType.Int32, G9ESyncColumnType.Bytes],
            [
                new JsonObject
                {
                    ["key"] = "6f1c2a7e-3b4d-4e5f-8a9b-0c1d2e3f4a5b", ["pushOp"] = 1, ["sequence"] = "10", ["baseVersion"] = "3",
                    ["dirtyMask"] = ((1L << 61) | 5).ToString(CultureInfo.InvariantCulture), ["hlcMs"] = "1759667696789", ["values"] = new JsonArray("pushed", 7, Bytes(1, 2, 3))
                },
                new JsonObject
                {
                    ["key"] = "0f8fad5b-d9cb-469f-a165-70867728950e", ["pushOp"] = 2, ["sequence"] = "11", ["baseVersion"] = "0", ["dirtyMask"] = "0",
                    ["hlcMs"] = "-5"
                },
                new JsonObject
                {
                    ["key"] = "a1b2c3d4-e5f6-4a7b-8c9d-0e1f2a3b4c5d", ["pushOp"] = 1, ["sequence"] = "12", ["baseVersion"] = "9007199254740993",
                    ["dirtyMask"] = "-1", ["hlcMs"] = "0", ["values"] = new JsonArray(null, null, null)
                }
            ]),
            Section(G9ERowBatchOp.Backfill, 3, G9ESyncColumnType.Guid, [G9ESyncColumnType.Double, G9ESyncColumnType.Boolean],
            [
                new JsonObject { ["key"] = "0f8fad5b-d9cb-469f-a165-70867728950e", ["version"] = "12", ["values"] = new JsonArray(DoubleBits(-1.25), false) }
            ]),
            Section(G9ERowBatchOp.Upsert, 5, G9ESyncColumnType.String, [G9ESyncColumnType.Int32, G9ESyncColumnType.String], []));

        var result = new JsonArray
        {
            Frame("all-ops", all),
            Frame("empty", new JsonArray()),
            Frame("tiny", new JsonArray(Section(G9ERowBatchOp.Tombstone, 0, G9ESyncColumnType.Guid, [], [KeyRow("0f8fad5b-d9cb-469f-a165-70867728950e", 1)])))
        };
        return result;
    }

    private static readonly G9ESyncColumnType[] AllTypes =
    [
        G9ESyncColumnType.Boolean, G9ESyncColumnType.Int32, G9ESyncColumnType.Int64, G9ESyncColumnType.Double, G9ESyncColumnType.Decimal,
        G9ESyncColumnType.String, G9ESyncColumnType.Guid, G9ESyncColumnType.DateTime, G9ESyncColumnType.DateTimeOffset, G9ESyncColumnType.TimeSpan,
        G9ESyncColumnType.Bytes
    ];

    private static List<JsonNode?> UpsertRows()
    {
        var rows = new List<JsonNode?>();
        var big = new byte[300];
        for (var i = 0; i < big.Length; i++) big[i] = (byte)(i * 7);
        rows.Add(new JsonObject
        {
            ["key"] = "0f8fad5b-d9cb-469f-a165-70867728950e", ["version"] = "1",
            ["values"] = new JsonArray(true, -123456, "-9007199254740993", DoubleBits(3.141592653589793), Decimal(1.50m), "Ünïcødé ✓ 😀 text",
                "6f1c2a7e-3b4d-4e5f-8a9b-0c1d2e3f4a5b", DateTimeValue(new DateTime(2026, 10, 5, 12, 34, 56, DateTimeKind.Utc).AddTicks(1_234_567)),
                OffsetValue(new DateTimeOffset(2026, 10, 5, 16, 4, 56, TimeSpan.FromMinutes(210)).AddTicks(7)), TimeSpanValue(TimeSpan.FromTicks(-36_000_000_001)),
                Bytes(0, 1, 254, 255))
        });
        rows.Add(new JsonObject
        {
            ["key"] = "6f1c2a7e-3b4d-4e5f-8a9b-0c1d2e3f4a5b", ["version"] = "9223372036854775807",
            ["values"] = new JsonArray(null, null, null, null, null, null, null, null, null, null, null)
        });
        rows.Add(new JsonObject
        {
            ["key"] = "a1b2c3d4-e5f6-4a7b-8c9d-0e1f2a3b4c5d", ["version"] = "300",
            ["values"] = new JsonArray(false, int.MinValue, long.MaxValue.ToString(CultureInfo.InvariantCulture), DoubleBits(double.NaN), Decimal(decimal.MaxValue), "",
                "00000000-0000-0000-0000-000000000000", DateTimeValue(DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Local)),
                OffsetValue(new DateTimeOffset(1, 1, 2, 0, 0, 0, TimeSpan.FromHours(-14))), TimeSpanValue(TimeSpan.MaxValue), Bytes(big))
        });
        rows.Add(new JsonObject
        {
            ["key"] = "b1b2c3d4-e5f6-4a7b-8c9d-0e1f2a3b4c5d", ["version"] = "16384",
            ["values"] = new JsonArray(null, 0, "0", DoubleBits(-0.0), Decimal(-0.0000000000000000000000000001m), null, null,
                DateTimeValue(DateTime.MinValue), OffsetValue(new DateTimeOffset(9999, 12, 31, 23, 0, 0, TimeSpan.FromHours(14))), TimeSpanValue(TimeSpan.MinValue), Bytes())
        });
        return rows;
    }

    private static JsonObject KeyRow(string key, long version) => new() { ["key"] = key, ["version"] = version.ToString(CultureInfo.InvariantCulture) };

    private static JsonObject Section(G9ERowBatchOp op, int tableId, G9ESyncColumnType keyType, G9ESyncColumnType[] types, JsonNode?[] rows)
    {
        var typeArray = new JsonArray();
        foreach (var type in types) typeArray.Add((int)type);
        return new JsonObject { ["op"] = (int)op, ["tableId"] = tableId, ["keyType"] = (int)keyType, ["types"] = typeArray, ["rows"] = new JsonArray(rows) };
    }

    private static JsonObject Frame(string name, JsonArray sections)
    {
        using var writer = new G9CRowBatchWriter(256);
        foreach (var section in sections) WriteSection(writer, section!.AsObject());
        return new JsonObject
        {
            ["name"] = name,
            ["sections"] = sections.DeepClone(),
            ["v1"] = Hex(writer.ToFrame(G9ECompression.None, version: 1)),
            ["v2"] = Hex(writer.ToFrame(G9ECompression.None, version: 2)),
            ["v1br"] = Hex(writer.ToFrame(G9ECompression.Brotli, version: 1)),
            ["v2br"] = Hex(writer.ToFrame(G9ECompression.Brotli, version: 2))
        };
    }

    /// <summary>The frame writer driven by the section description (the TypeScript test drives its writer with the same data).</summary>
    private static void WriteSection(G9CRowBatchWriter writer, JsonObject section)
    {
        var op = (G9ERowBatchOp)section["op"]!.GetValue<int>();
        var types = section["types"]!.AsArray().Select(t => (G9ESyncColumnType)t!.GetValue<int>()).ToArray();
        writer.BeginSection(op, section["tableId"]!.GetValue<int>(), (G9ESyncColumnType)section["keyType"]!.GetValue<int>(), types);
        foreach (var node in section["rows"]!.AsArray())
        {
            var row = node!.AsObject();
            var key = row["key"]!.GetValue<string>();
            switch (op)
            {
                case G9ERowBatchOp.Upsert or G9ERowBatchOp.Backfill:
                    writer.BeginRow(key, Long(row["version"]));
                    WriteValues(writer, types, row["values"]!.AsArray());
                    writer.EndRow();
                    break;
                case G9ERowBatchOp.Tombstone or G9ERowBatchOp.MoveOut:
                    writer.AddKey(key, Long(row["version"]));
                    break;
                case G9ERowBatchOp.KeyVersions:
                    writer.AddKeyVersion(key, Long(row["version"]), Long(row["rowHash"]));
                    break;
                case G9ERowBatchOp.Push:
                    var pushOp = (G9EPushRowOp)row["pushOp"]!.GetValue<int>();
                    writer.BeginPushRow(key, pushOp, Long(row["sequence"]), Long(row["baseVersion"]), Long(row["dirtyMask"]), Long(row["hlcMs"]));
                    if (pushOp == G9EPushRowOp.Upsert) WriteValues(writer, types, row["values"]!.AsArray());
                    writer.EndRow();
                    break;
            }
        }

        writer.EndSection();
    }

    private static void WriteValues(G9CRowBatchWriter writer, G9ESyncColumnType[] types, JsonArray values)
    {
        for (var i = 0; i < types.Length; i++)
        {
            var value = values[i];
            if (value is null)
            {
                writer.WriteNull();
                continue;
            }

            switch (types[i])
            {
                case G9ESyncColumnType.Boolean: writer.WriteBoolean(value.GetValue<bool>()); break;
                case G9ESyncColumnType.Int32: writer.WriteInt32(value.GetValue<int>()); break;
                case G9ESyncColumnType.Int64: writer.WriteInt64(Long(value)); break;
                case G9ESyncColumnType.Double: writer.WriteDouble(BitConverter.Int64BitsToDouble(Convert.ToInt64(value["bits"]!.GetValue<string>(), 16))); break;
                case G9ESyncColumnType.Decimal:
                    writer.WriteDecimal(new decimal(value["lo"]!.GetValue<int>(), value["mid"]!.GetValue<int>(), value["hi"]!.GetValue<int>(),
                        value["negative"]!.GetValue<bool>(), (byte)value["scale"]!.GetValue<int>()));
                    break;
                case G9ESyncColumnType.String: writer.WriteString(value.GetValue<string>()); break;
                case G9ESyncColumnType.Guid: writer.WriteGuid(Guid.Parse(value.GetValue<string>())); break;
                case G9ESyncColumnType.DateTime:
                    writer.WriteDateTime(new DateTime(Long(value["ticks"]), (DateTimeKind)value["kind"]!.GetValue<int>()));
                    break;
                case G9ESyncColumnType.DateTimeOffset:
                    var offset = TimeSpan.FromMinutes(value["offsetMinutes"]!.GetValue<int>());
                    writer.WriteDateTimeOffset(new DateTimeOffset(Long(value["utcTicks"]) + offset.Ticks, offset));
                    break;
                case G9ESyncColumnType.TimeSpan: writer.WriteTimeSpan(TimeSpan.FromTicks(Long(value["ticks"]))); break;
                case G9ESyncColumnType.Bytes: writer.WriteBytes(Convert.FromHexString(value["hex"]!.GetValue<string>())); break;
                default: throw new InvalidOperationException("Unknown type.");
            }
        }
    }

    // ---------------------------------------------------------------- the local schema

    private static JsonObject Model()
    {
        var model = new G9CSyncClientModelBuilder()
            .Table("Operation.Pot", "Pot", t => t.Key("Id")
                .Column("RidgeId", G9ESyncColumnType.Guid)
                .Column("Code", G9ESyncColumnType.String, nullable: false)
                .Column("PlantedAt", G9ESyncColumnType.DateTime)
                .Column("Weight", G9ESyncColumnType.Decimal)
                .Column("Count", G9ESyncColumnType.Int32, nullable: false)
                .Column("Alive", G9ESyncColumnType.Boolean, nullable: false)
                .Column("Photo", G9ESyncColumnType.Bytes)
                .Column("Moment", G9ESyncColumnType.DateTimeOffset, nullable: false)
                .Column("Span", G9ESyncColumnType.TimeSpan)
                .Column("Ratio", G9ESyncColumnType.Double, nullable: false)
                .Column("Big", G9ESyncColumnType.Int64)
                .Column("Due", G9ESyncColumnType.DateTime, nullable: false)
                .Column("Price", G9ESyncColumnType.Decimal, nullable: false)
                .Column("Owner", G9ESyncColumnType.Guid, nullable: false)
                .Column("Blob", G9ESyncColumnType.Bytes, nullable: false)
                .Index(null, false, "RidgeId")
                .Index("UX_Pot_Code", true, "Code", "Count"))
            .Table("Base.Site", "Site", t => t.Key("SiteId", G9ESyncColumnType.Int64).Column("Name", G9ESyncColumnType.String))
            .Table("Base.Tag", "Tag]x", t => t.Key("Name", G9ESyncColumnType.String).Column("Color", G9ESyncColumnType.String, nullable: false))
            .Table("Wide.T", "Wide", t =>
            {
                t.Key("Id", G9ESyncColumnType.Int32);
                for (var i = 0; i < G9CSyncClientModelBuilder.MaxColumns; i++) t.Column("C" + i.ToString(CultureInfo.InvariantCulture), G9ESyncColumnType.Int32);
            })
            .Table("Empty.T", "Bare", t => t.Key("Id"))
            .Build();

        var textOptions = new G9CSqliteSyncStoreOptions();
        var exactOptions = new G9CSqliteSyncStoreOptions { DateTimeStorage = G9ESqliteDateTimeStorage.Ticks, DecimalStorage = G9ESqliteDecimalStorage.Text };
        var tables = new JsonArray();
        var tid = 7L;
        foreach (var table in model.Tables)
        {
            tables.Add(new JsonObject
            {
                ["serverName"] = table.ServerName,
                ["localName"] = table.LocalName,
                ["modelHash"] = SqliteSql.ModelHash(table),
                ["createTable"] = SqliteSql.CreateTable(table, textOptions),
                ["createTableExact"] = SqliteSql.CreateTable(table, exactOptions),
                ["createTriggers"] = SqliteSql.CreateTriggers(table, tid).ReplaceLineEndings("\n"),
                ["createCommandTriggers"] = SqliteSql.CreateCommandTriggers(table, tid).ReplaceLineEndings("\n"),
                ["dropTriggers"] = SqliteSql.DropTriggers(table),
                ["tid"] = tid
            });
            tid++;
        }

        return new JsonObject { ["metadata"] = SqliteSql.Metadata.ReplaceLineEndings("\n"), ["tables"] = tables };
    }

    // ---------------------------------------------------------------- storage forms

    private static JsonObject Storage()
    {
        var options = new G9CSqliteSyncStoreOptions();
        var dateTimes = new JsonArray();
        DateTime[] moments =
        [
            DateTime.MinValue, DateTime.MaxValue, new DateTime(2026, 10, 5, 12, 34, 56, DateTimeKind.Utc).AddTicks(1_234_567),
            new DateTime(1999, 12, 31, 23, 59, 59, DateTimeKind.Unspecified), new DateTime(2024, 2, 29, 0, 0, 0, DateTimeKind.Local).AddTicks(1),
            new DateTime(1, 3, 1, 1, 2, 3, DateTimeKind.Utc), new DateTime(2100, 3, 1, 0, 0, 0, DateTimeKind.Utc)
        ];
        foreach (var moment in moments)
        {
            dateTimes.Add(new JsonObject
            {
                ["ticks"] = moment.Ticks.ToString(CultureInfo.InvariantCulture), ["kind"] = (int)moment.Kind,
                ["text"] = moment.ToString(options.DateTimeFormat, CultureInfo.InvariantCulture)
            });
        }

        var offsets = new JsonArray();
        DateTimeOffset[] values =
        [
            new(2026, 10, 5, 16, 4, 56, TimeSpan.FromMinutes(210)), new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.FromHours(-14)).AddTicks(9_999_999),
            new(1, 1, 1, 14, 0, 0, TimeSpan.FromHours(14)), new(9999, 12, 31, 23, 59, 59, TimeSpan.Zero), new(2026, 6, 1, 8, 0, 0, TimeSpan.FromMinutes(-150))
        ];
        foreach (var value in values)
        {
            offsets.Add(new JsonObject
            {
                ["utcTicks"] = value.UtcTicks.ToString(CultureInfo.InvariantCulture), ["offsetMinutes"] = (int)value.Offset.TotalMinutes,
                ["text"] = value.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture)
            });
        }

        var decimals = new JsonArray();
        decimal[] numbers =
        [
            0m, 1.50m, -1.50m, 0.1m, 123456789.123456789m, decimal.MaxValue, decimal.MinValue, 0.0000000000000000000000000001m, 79228162514264337593543950.335m,
            1e-10m, 2.675m, 100m, 0.30000000000000004m, 12345678901234567890.5m
        ];
        foreach (var number in numbers) decimals.Add(Decimal(number));

        // What the store pushes for a REAL-stored decimal: decimal.Parse of SQLite's own text for the REAL (G9SqliteNative.GetDecimal).
        var realDecimals = new JsonArray();
        double[] reals = [0.1, 0.30000000000000004, 1.0, 123.456, 1e20, 1.5e-7, 12345678901234567890.0, 0.1 + 0.7, 2.675, -3.14159265358979, 1e-5, 9007199254740993.0, 1e15, 0.000123];
        var file = Path.Combine(Path.GetTempPath(), "g9-golden-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using var connection = new SqliteConnection("Data Source=" + file + ";Pooling=False");
            connection.Open();
            foreach (var real in reals)
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT CAST($v AS REAL), CAST(CAST($v AS REAL) AS TEXT)";
                command.Parameters.AddWithValue("$v", real);
                using var reader = command.ExecuteReader();
                Assert.True(reader.Read());
                realDecimals.Add(new JsonObject
                {
                    ["bits"] = DoubleBits(real)["bits"]!.DeepClone(), ["sqliteText"] = reader.GetString(1),
                    ["decimal"] = reader.GetDecimal(0).ToString(CultureInfo.InvariantCulture)
                });
            }
        }
        finally
        {
            File.Delete(file);
        }

        var parsedDates = new JsonArray();
        string[] texts = ["2026-10-05T12:34:56.1234567", "2026-10-05 12:34:56", "2026-10-05T12:34:56Z", "2026-10-05", "2026-10-05 12:34:56.123", "0001-01-01T00:00:00.0000000"];
        foreach (var text in texts)
        {
            var parsed = DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            parsedDates.Add(new JsonObject { ["text"] = text, ["ticks"] = parsed.Ticks.ToString(CultureInfo.InvariantCulture), ["kind"] = (int)parsed.Kind });
        }

        var parsedOffsets = new JsonArray();
        string[] offsetTexts = ["2026-10-05T12:34:56.1234567+03:30", "2026-10-05T12:34:56.0000000-14:00", "2026-10-05T12:34:56Z", "2026-10-05 12:34:56 +00:00"];
        foreach (var text in offsetTexts)
        {
            var parsed = DateTimeOffset.Parse(text, CultureInfo.InvariantCulture);
            parsedOffsets.Add(new JsonObject
            {
                ["text"] = text, ["utcTicks"] = parsed.UtcTicks.ToString(CultureInfo.InvariantCulture), ["offsetMinutes"] = (int)parsed.Offset.TotalMinutes
            });
        }

        return new JsonObject
        {
            ["dateTimeFormat"] = options.DateTimeFormat,
            ["dateTimeMinLiteral"] = SqliteSql.DefaultLiteral(G9ESyncColumnType.DateTime, options),
            ["dateTimes"] = dateTimes,
            ["dateTimeOffsets"] = offsets,
            ["decimals"] = decimals,
            ["realDecimals"] = realDecimals,
            ["parsedDateTimes"] = parsedDates,
            ["parsedDateTimeOffsets"] = parsedOffsets
        };
    }

    // ---------------------------------------------------------------- hub messages

    private static JsonObject Hub()
    {
        HubMessage[] messages =
        [
            CompletionMessage.WithResult("1", JsGoldenSamples.HelloResult()), CompletionMessage.WithResult("2", JsGoldenSamples.PushResult()),
            CompletionMessage.WithResult("3", JsGoldenSamples.PullPlan()), new StreamItemMessage("4", JsGoldenSamples.FullChunk()),
            new StreamItemMessage("5", JsGoldenSamples.EmptyChunk()), new InvocationMessage(G9SyncProtocol.Listeners.OnChangesAvailable, [JsGoldenSamples.Changes()]),
            CompletionMessage.WithError("6", "An unexpected error occurred invoking 'Push' on the server. HubException: [G9S|G9S-SRV-BUSY|1] The server is busy."),
            CompletionMessage.Empty("7")
        ];
        string[] names = ["helloResult", "pushResult", "pullPlan", "fullChunk", "emptyChunk", "changesAvailable", "error", "streamEnd"];
        var json = new JsonObject();
        var msgpack = new JsonObject();
        var jsonProtocol = JsonProtocol();
        var msgpackProtocol = MessagePackProtocol();
        for (var i = 0; i < messages.Length; i++)
        {
            json[names[i]] = Encoding.UTF8.GetString(jsonProtocol.GetMessageBytes(messages[i]).Span);
            msgpack[names[i]] = Hex(msgpackProtocol.GetMessageBytes(messages[i]).Span);
        }

        return new JsonObject { ["json"] = json, ["messagepack"] = msgpack };
    }

    private static JsonHubProtocol JsonProtocol()
    {
        var options = new JsonHubProtocolOptions();
        options.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, G9SyncJsonContext.Default);
        return new JsonHubProtocol(Options.Create(options));
    }

    /// <summary>Nerdbank's protocol class is internal: take it from the registration both ends use.</summary>
    private static IHubProtocol MessagePackProtocol()
    {
        var builder = new HubConnectionBuilder();
        builder.AddG9MessagePackProtocol(G9SyncWireShapes.GeneratedTypeShapeProvider);
        using var services = builder.Services.BuildServiceProvider();
        return services.GetServices<IHubProtocol>().Single(p => p.Name == "messagepack");
    }

    private static T Parse<T>(IHubProtocol protocol, IInvocationBinder binder, byte[] bytes)
        where T : HubMessage
    {
        var sequence = new System.Buffers.ReadOnlySequence<byte>(bytes);
        Assert.True(protocol.TryParseMessage(ref sequence, binder, out var message), "The message did not parse.");
        Assert.True(sequence.IsEmpty, "The message left bytes unread.");
        return Assert.IsType<T>(message);
    }

    /// <summary>Two DTOs are the same when their wire JSON (the source-generated context) is identical.</summary>
    private static void AssertSame<T>(T expected, object? actual)
    {
        Assert.IsType<T>(actual);
        Assert.Equal(JsonSerializer.Serialize(expected, typeof(T), G9SyncJsonContext.Default), JsonSerializer.Serialize(actual, typeof(T), G9SyncJsonContext.Default));
    }

    /// <summary>The parameter types of the hub's methods, as the server's binder resolves them.</summary>
    private sealed class SyncBinder : IInvocationBinder
    {
        public IReadOnlyList<Type> GetParameterTypes(string methodName) => methodName switch
        {
            G9SyncProtocol.Methods.Hello => [typeof(G9DtHelloRequest)],
            // Stream parameters are not in a hub method's parameter types (HubMethodDescriptor): they arrive as StreamIds.
            G9SyncProtocol.Methods.Push => [typeof(G9DtPushRequest)],
            G9SyncProtocol.Methods.PlanPull => [typeof(G9DtPullRequest)],
            G9SyncProtocol.Methods.Pull => [typeof(string), typeof(G9DtPullContinuation)],
            G9SyncProtocol.Methods.Reconcile => [typeof(G9DtReconcileRequest)],
            G9SyncProtocol.Methods.FetchRows => [typeof(G9DtFetchRowsRequest)],
            _ => throw new InvalidOperationException("Unknown method " + methodName)
        };

        public Type GetReturnType(string invocationId) => typeof(object);

        public Type GetStreamItemType(string streamId) => typeof(G9DtSyncChunk);
    }

    // ---------------------------------------------------------------- helpers

    private static string GoldenPath(string file)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "G9SyncData.slnx"))) directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "js", "packages", "syncdata", "test", "golden", file);
    }

    private static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(bytes);

    private static long Long(JsonNode? node) => long.Parse(node!.GetValue<string>(), CultureInfo.InvariantCulture);

    private static JsonObject DoubleBits(double value) =>
        new() { ["bits"] = BitConverter.DoubleToInt64Bits(value).ToString("x16", CultureInfo.InvariantCulture), ["text"] = value.ToString(CultureInfo.InvariantCulture) };

    private static JsonObject Decimal(decimal value)
    {
        var bits = decimal.GetBits(value);
        return new JsonObject
        {
            ["lo"] = bits[0], ["mid"] = bits[1], ["hi"] = bits[2], ["scale"] = (bits[3] >> 16) & 0xFF, ["negative"] = bits[3] < 0,
            ["text"] = value.ToString(CultureInfo.InvariantCulture), ["double"] = DoubleBits((double)value)["bits"]!.DeepClone()
        };
    }

    private static JsonObject DateTimeValue(DateTime value) => new() { ["ticks"] = value.Ticks.ToString(CultureInfo.InvariantCulture), ["kind"] = (int)value.Kind };

    private static JsonObject OffsetValue(DateTimeOffset value) =>
        new() { ["utcTicks"] = value.UtcTicks.ToString(CultureInfo.InvariantCulture), ["offsetMinutes"] = (int)value.Offset.TotalMinutes };

    private static JsonObject TimeSpanValue(TimeSpan value) => new() { ["ticks"] = value.Ticks.ToString(CultureInfo.InvariantCulture) };

    private static JsonObject Bytes(params byte[] bytes) => new() { ["hex"] = Convert.ToHexStringLower(bytes) };
}
