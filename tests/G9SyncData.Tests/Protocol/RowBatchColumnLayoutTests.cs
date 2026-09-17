using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using G9SyncData.Protocol.RowBatch;

namespace G9SyncData.Tests.Protocol;

/// <summary>G9RowBatch version 2 (column-grouped body, ADR-0040) against version 1: same content, exact conversion, hostile input.</summary>
public sealed class RowBatchColumnLayoutTests
{
    private static readonly G9ESyncColumnType[] ValueTypes =
    [
        G9ESyncColumnType.Boolean, G9ESyncColumnType.Int32, G9ESyncColumnType.Int64, G9ESyncColumnType.Double,
        G9ESyncColumnType.Decimal, G9ESyncColumnType.String, G9ESyncColumnType.Guid, G9ESyncColumnType.DateTime,
        G9ESyncColumnType.DateTimeOffset, G9ESyncColumnType.TimeSpan, G9ESyncColumnType.Bytes
    ];

    private static readonly G9ESyncColumnType[] KeyTypes = [G9ESyncColumnType.Guid, G9ESyncColumnType.Int64, G9ESyncColumnType.String];

    [Fact]
    public void Random_frames_of_every_op_key_and_column_type_read_the_same_in_both_layouts_and_convert_back_byte_for_byte()
    {
        var random = new Random(20260917);
        for (var iteration = 0; iteration < 300; iteration++)
        {
            using var writer = RandomBatch(random);
            var v1 = writer.ToFrame(G9ECompression.None, version: 1);
            var v2 = writer.ToFrame(G9ECompression.None, version: 2);
            var v2Brotli = writer.ToFrame(G9ECompression.Brotli, version: 2);

            Assert.Equal(2, v2[4]);
            var expected = Describe(v1);
            Assert.Equal(expected, Describe(v2));
            Assert.Equal(expected, Describe(v2Brotli));

            var rows = v1.AsSpan(10);
            var columns = G9RowBatchColumnLayout.FromRows(rows, out var columnsLength);
            try
            {
                Assert.Equal(columnsLength, BinaryPrimitives.ReadInt32LittleEndian(v2.AsSpan(6)));
                Assert.True(columns.AsSpan(0, columnsLength).SequenceEqual(v2.AsSpan(10)));
                var back = G9RowBatchColumnLayout.ToRows(columns.AsSpan(0, columnsLength), out var backLength);
                try
                {
                    Assert.True(back.AsSpan(0, backLength).SequenceEqual(rows), $"iteration {iteration}: the row layout did not come back unchanged");
                }
                finally
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(back);
                }
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(columns);
            }
        }
    }

    [Fact]
    public void Empty_frames_sections_without_rows_and_rows_without_columns_survive_both_layouts()
    {
        using var writer = new G9CRowBatchWriter();
        Assert.Equal(Describe(writer.ToFrame(version: 1)), Describe(writer.ToFrame(version: 2)));

        writer.BeginSection(G9ERowBatchOp.Upsert, 1, G9ESyncColumnType.Guid, []);
        writer.EndSection();
        writer.BeginSection(G9ERowBatchOp.Upsert, 2, G9ESyncColumnType.Int64, []);
        writer.BeginRow(5L, 1);
        writer.EndRow();
        writer.EndSection();
        writer.BeginSection(G9ERowBatchOp.Push, 3, G9ESyncColumnType.String, [G9ESyncColumnType.String]);
        writer.BeginPushRow("gone", G9EPushRowOp.Delete, 1, 9, 0, 0);
        writer.EndRow();
        writer.EndSection();

        Assert.Equal(Describe(writer.ToFrame(G9ECompression.None, version: 1)), Describe(writer.ToFrame(G9ECompression.None, version: 2)));
    }

    [Fact]
    public void A_damaged_column_grouped_body_is_refused_with_a_format_error_and_never_misread()
    {
        var random = new Random(17);
        using var writer = RandomBatch(random, rowsPerSection: 20);
        var frame = writer.ToFrame(G9ECompression.None, version: 2);

        var refused = 0;
        for (var attempt = 0; attempt < 20000; attempt++)
        {
            var damaged = (byte[])frame.Clone();
            switch (attempt % 3)
            {
                case 0: damaged[10 + random.Next(damaged.Length - 10)] ^= (byte)(1 << random.Next(8)); break;
                case 1: damaged[10 + random.Next(damaged.Length - 10)] = (byte)random.Next(256); break;
                default:
                    var cut = 10 + random.Next(damaged.Length - 10);
                    damaged = damaged[..cut];
                    BinaryPrimitives.WriteInt32LittleEndian(damaged.AsSpan(6), cut - 10);
                    break;
            }

            // A changed byte inside a value can go unnoticed by design (no checksum; the transport guarantees integrity). What must never
            // happen is any failure other than FormatException: no out-of-range read, no argument exception from a decoded value.
            try
            {
                _ = Describe(damaged);
            }
            catch (FormatException)
            {
                refused++;
            }
        }

        Assert.True(refused > 7000, $"only {refused} of 20000 damaged bodies were refused");
    }

    [Fact]
    public void Grouping_by_column_makes_a_page_of_agripad_pots_markedly_smaller_once_compressed()
    {
        using var writer = new G9CRowBatchWriter();
        G9ESyncColumnType[] types =
        [
            G9ESyncColumnType.Int32, G9ESyncColumnType.String, G9ESyncColumnType.Int32, G9ESyncColumnType.Guid, G9ESyncColumnType.Guid,
            G9ESyncColumnType.DateTime, G9ESyncColumnType.DateTime, G9ESyncColumnType.Guid, G9ESyncColumnType.Guid
        ];
        var variety = Guid.NewGuid();
        var user = Guid.NewGuid();
        var ridge = Guid.NewGuid();
        writer.BeginSection(G9ERowBatchOp.Upsert, 2, G9ESyncColumnType.Guid, types);
        foreach (var (key, g) in Enumerable.Range(1, 2000).Select(g => (Guid.NewGuid(), g)).OrderBy(r => r.Item1))
        {
            if (g % 500 == 1) ridge = Guid.NewGuid();
            writer.BeginRow(key, 1_000_000 + (g % 50));
            writer.WriteInt32(g);
            writer.WriteString(FormattableString.Invariant($"POINT({51.3 + (g % 1000) / 100000.0} {35.7 + g / 10000000.0})"));
            writer.WriteInt32(g % 3);
            writer.WriteGuid(ridge);
            writer.WriteGuid(variety);
            writer.WriteDateTime(new DateTime(2024, 1, 1).AddMinutes(g));
            writer.WriteDateTime(new DateTime(2025, 6, 1).AddSeconds(g * 7));
            writer.WriteGuid(user);
            if (g % 4 == 0) writer.WriteNull();
            else writer.WriteGuid(user);
            writer.EndRow();
        }

        writer.EndSection();
        var v1 = writer.ToFrame(version: 1).Length;
        var v2 = writer.ToFrame(version: 2).Length;
        Assert.True(v2 < v1 * 0.9, $"row-wise {v1:N0} bytes, column-grouped {v2:N0} bytes");
    }

    // ---------------------------------------------------------------- helpers

    private static G9CRowBatchWriter RandomBatch(Random random, int? rowsPerSection = null)
    {
        var writer = new G9CRowBatchWriter(256);
        var sections = random.Next(1, 5);
        for (var s = 0; s < sections; s++)
        {
            var op = (G9ERowBatchOp)random.Next(1, 7);
            var keyType = KeyTypes[random.Next(KeyTypes.Length)];
            var columnCount = op is G9ERowBatchOp.Tombstone or G9ERowBatchOp.MoveOut or G9ERowBatchOp.KeyVersions ? 0 : random.Next(0, 20);
            var types = Enumerable.Range(0, columnCount).Select(_ => ValueTypes[random.Next(ValueTypes.Length)]).ToArray();
            writer.BeginSection(op, random.Next(0, 300), keyType, types);
            var rows = rowsPerSection ?? random.Next(0, 40);
            for (var r = 0; r < rows; r++)
            {
                var key = RandomKey(random, keyType);
                var version = random.NextInt64(0, long.MaxValue >> random.Next(0, 60));
                switch (op)
                {
                    case G9ERowBatchOp.Upsert or G9ERowBatchOp.Backfill:
                        writer.BeginRow(key, version);
                        WriteValues(writer, random, types);
                        writer.EndRow();
                        break;
                    case G9ERowBatchOp.Tombstone or G9ERowBatchOp.MoveOut:
                        writer.AddKey(key, version);
                        break;
                    case G9ERowBatchOp.KeyVersions:
                        writer.AddKeyVersion(key, version, random.NextInt64(long.MinValue, long.MaxValue));
                        break;
                    case G9ERowBatchOp.Push:
                        var pushOp = random.Next(3) == 0 ? G9EPushRowOp.Delete : G9EPushRowOp.Upsert;
                        writer.BeginPushRow(key, pushOp, random.NextInt64(0, 1L << 40), version, random.NextInt64(long.MinValue, long.MaxValue),
                            random.NextInt64(-(1L << 50), 1L << 50));
                        if (pushOp == G9EPushRowOp.Upsert) WriteValues(writer, random, types);
                        writer.EndRow();
                        break;
                }
            }

            writer.EndSection();
        }

        return writer;
    }

    private static string RandomKey(Random random, G9ESyncColumnType keyType) => keyType switch
    {
        G9ESyncColumnType.Guid => Guid.NewGuid().ToString("D"),
        G9ESyncColumnType.Int64 => random.NextInt64(long.MinValue, long.MaxValue).ToString(CultureInfo.InvariantCulture),
        _ => RandomText(random)
    };

    private static void WriteValues(G9CRowBatchWriter writer, Random random, G9ESyncColumnType[] types)
    {
        foreach (var type in types)
        {
            if (random.Next(4) == 0)
            {
                writer.WriteNull();
                continue;
            }

            switch (type)
            {
                case G9ESyncColumnType.Boolean: writer.WriteBoolean(random.Next(2) == 1); break;
                case G9ESyncColumnType.Int32: writer.WriteInt32(random.Next(int.MinValue, int.MaxValue)); break;
                case G9ESyncColumnType.Int64: writer.WriteInt64(random.NextInt64(long.MinValue, long.MaxValue)); break;
                case G9ESyncColumnType.Double: writer.WriteDouble(random.NextDouble() * 1e9 - 5e8); break;
                case G9ESyncColumnType.Decimal: writer.WriteDecimal(new decimal(random.Next(), random.Next(), random.Next(), random.Next(2) == 1, (byte)random.Next(0, 29))); break;
                case G9ESyncColumnType.String: writer.WriteString(RandomText(random)); break;
                case G9ESyncColumnType.Guid: writer.WriteGuid(Guid.NewGuid()); break;
                case G9ESyncColumnType.DateTime: writer.WriteDateTime(new DateTime(random.NextInt64(DateTime.MinValue.Ticks, DateTime.MaxValue.Ticks), (DateTimeKind)random.Next(3))); break;
                case G9ESyncColumnType.DateTimeOffset:
                    writer.WriteDateTimeOffset(new DateTimeOffset(new DateTime(2000, 1, 1).AddSeconds(random.Next()), TimeSpan.FromMinutes(random.Next(-840, 841))));
                    break;
                case G9ESyncColumnType.TimeSpan: writer.WriteTimeSpan(TimeSpan.FromTicks(random.NextInt64(long.MinValue, long.MaxValue))); break;
                case G9ESyncColumnType.Bytes:
                    var bytes = new byte[random.Next(0, 300)];
                    random.NextBytes(bytes);
                    writer.WriteBytes(bytes);
                    break;
            }
        }
    }

    private static string RandomText(Random random)
    {
        var alphabet = "abcXYZ 0123456789-_/:سلام🌱";
        var builder = new StringBuilder();
        var length = random.Next(0, random.Next(2) == 0 ? 8 : 200);
        for (var i = 0; i < length; i++)
        {
            var c = alphabet[random.Next(alphabet.Length)];
            if (char.IsSurrogate(c)) continue;
            builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>Everything a reader exposes about a frame, as text.</summary>
    private static string Describe(byte[] frame)
    {
        using var reader = G9CRowBatchReader.Open(frame);
        var text = new StringBuilder().Append(reader.SectionCount).Append('\n');
        while (reader.NextSection())
        {
            text.Append(CultureInfo.InvariantCulture, $"section {reader.Op} {reader.TableId} {reader.KeyType} [{string.Join(',', reader.ColumnTypes)}] {reader.RowCount}\n");
            while (reader.ReadRow())
            {
                text.Append(CultureInfo.InvariantCulture, $"  {reader.Key} v{reader.Version} h{reader.RowHash}");
                if (reader.Op == G9ERowBatchOp.Push)
                    text.Append(CultureInfo.InvariantCulture, $" {reader.PushOp} s{reader.Sequence} b{reader.BaseVersion} m{reader.DirtyMask} t{reader.HlcMs}");
                if (reader.HasValues)
                {
                    for (var c = 0; c < reader.ColumnCount; c++)
                    {
                        var value = reader.GetValue(c);
                        text.Append(" | ").Append(value switch
                        {
                            null => "NULL",
                            byte[] b => Convert.ToHexString(b),
                            DateTime d => d.Ticks + ":" + d.Kind,
                            DateTimeOffset o => o.UtcTicks + ":" + o.Offset,
                            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                            _ => value.ToString()
                        });
                    }
                }

                text.Append('\n');
            }
        }

        return text.ToString();
    }
}
