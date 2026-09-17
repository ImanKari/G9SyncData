using System.Buffers.Binary;
using G9SyncData.Protocol.RowBatch;

namespace G9SyncData.Tests.Protocol;

/// <summary>
///     A frame's header states how large its body becomes, and that is what gets allocated before a byte is decompressed. A reader
///     handling frames it did not produce refuses one that exceeds its budget instead of allocating for it (ADR-0051).
/// </summary>
public sealed class FrameDecodeBudgetTests
{
    [Fact]
    public void A_small_frame_claiming_a_huge_body_is_refused_before_anything_is_allocated()
    {
        // 96 bytes on the wire, 576 MiB claimed: the amplification a wire-size limit alone cannot see.
        var frame = FrameClaiming(G9RowBatchFormat.MaxDecodedBytes);
        Assert.Equal(96, frame.Length);

        var exception = Assert.Throws<FormatException>(() => G9CRowBatchReader.Open(frame, 64 * 1024 * 1024));
        Assert.Contains("at most", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_body_larger_than_the_format_allows_is_refused_whatever_the_budget()
    {
        Assert.Throws<FormatException>(() => G9CRowBatchReader.Open(FrameClaiming(G9RowBatchFormat.MaxDecodedBytes + 1), int.MaxValue));
    }

    [Fact]
    public void A_frame_within_the_budget_still_opens()
    {
        var writer = new G9CRowBatchWriter();
        writer.BeginSection(G9ERowBatchOp.Tombstone, 1, G9ESyncColumnType.Int64, []);
        writer.AddKey(42L, 9);
        writer.EndSection();
        var frame = writer.ToFrame();

        using var reader = G9CRowBatchReader.Open(frame, 64 * 1024 * 1024);
        Assert.True(reader.NextSection());
        Assert.Equal(1, reader.RowCount);
        Assert.True(reader.BodyBytes > 0);
        Assert.True(reader.BodyBytes <= 64 * 1024 * 1024);
    }

    [Fact]
    public void A_budget_of_zero_or_less_is_a_programming_error_not_a_frame_error()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => G9CRowBatchReader.Open(FrameClaiming(1024), 0));
    }

    /// <summary>A well-formed Brotli frame header that declares <paramref name="bodyLength" /> bytes of body.</summary>
    private static byte[] FrameClaiming(int bodyLength)
    {
        var frame = new byte[G9RowBatchFormat.HeaderSize + 86];
        frame[0] = (byte)'G';
        frame[1] = (byte)'9';
        frame[2] = (byte)'R';
        frame[3] = (byte)'B';
        frame[4] = G9RowBatchFormat.Version;
        frame[5] = (byte)G9ECompression.Brotli;
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(6, 4), bodyLength);
        return frame;
    }
}
