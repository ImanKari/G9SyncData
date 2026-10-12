using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using G9SyncData.Client.Sqlite.SnapshotFiles;
using G9SyncData.Protocol;
using Microsoft.Extensions.Logging;

namespace G9SyncData.Server.SnapshotFiles;

public sealed partial class G9CSnapshotFileService
{
    /// <summary>
    ///     Builds one file: the snapshot rows of every table through the client store's own writer into a private SQLite file, then cut into
    ///     segments of <see cref="G9CSnapshotFileOptions.SegmentBytes" /> that are hashed and Brotli-compressed in parallel, then published -
    ///     the container under a content-addressed name, the record last. A failure deletes everything it wrote.
    /// </summary>
    private async Task<ReadyFile> BuildAsync(G9CSnapshotFileBuild build)
    {
        var lifetime = _lifetime.Token;
        await _builds.WaitAsync(lifetime).ConfigureAwait(false);
        var unique = Guid.NewGuid().ToString("N")[..8];
        var raw = Path.Combine(_options.Directory, build.FileId + "." + unique + ".building.sqlite");
        var containerTemp = Path.Combine(_options.Directory, build.FileId + "." + unique + ".building.g9seg");
        string? container = null;
        var started = Stopwatch.GetTimestamp();
        try
        {
            LogBuildStarted(_logger, build.FileId, build.SubscriptionKey);
            IReadOnlyList<G9DtSnapshotFileTableInfo> tables;
            long rows;
            using (var writer = new G9CSnapshotFileWriter(raw, build.Storage, _options.MaxDecodedFrameBytes))
            {
                foreach (var table in build.Tables) writer.AddTable(table.ServerName, table.Shape, table.FrameColumns);
                foreach (var table in build.Tables)
                {
                    await foreach (var page in table.ReadAsync(lifetime).ConfigureAwait(false)) writer.WriteFrame(page.Frame);
                }

                tables = writer.Complete(_options.VerifyBuiltFiles);
                rows = writer.Rows;
            }

            var written = Stopwatch.GetElapsedTime(started);
            var length = new FileInfo(raw).Length;
            var segmentBytes = _options.SegmentBytes;
            var count = (int)((length + segmentBytes - 1) / segmentBytes);
            var compressed = new byte[count][];
            var hashes = new string[count];
            using (var handle = File.OpenHandle(raw, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess))
            {
                await Parallel.ForAsync(0, count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, _options.CompressionParallelism), CancellationToken = lifetime },
                    (index, _) =>
                    {
                        var offset = (long)index * segmentBytes;
                        var size = (int)Math.Min(segmentBytes, length - offset);
                        var buffer = ArrayPool<byte>.Shared.Rent(size);
                        var target = ArrayPool<byte>.Shared.Rent(BrotliEncoder.GetMaxCompressedLength(size));
                        try
                        {
                            var body = buffer.AsSpan(0, size);
                            for (var read = 0; read < size;)
                            {
                                var n = RandomAccess.Read(handle, body[read..], offset + read);
                                if (n == 0) throw new EndOfStreamException("The built file ended early.");
                                read += n;
                            }

                            hashes[index] = Convert.ToHexStringLower(SHA256.HashData(body));
                            if (!BrotliEncoder.TryCompress(body, target, out var bytes, _options.CompressionQuality, _options.CompressionWindow))
                            {
                                throw new InvalidOperationException("Brotli could not compress a segment into its worst-case size.");
                            }

                            compressed[index] = target.AsSpan(0, bytes).ToArray();
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(buffer);
                            ArrayPool<byte>.Shared.Return(target);
                        }

                        return ValueTask.CompletedTask;
                    }).ConfigureAwait(false);
            }

            string fileSha;
            await using (var stream = new FileStream(raw, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan))
            {
                fileSha = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, lifetime).ConfigureAwait(false));
            }

            // The container: segments back to back. Its name carries the hash, so two builds of one file (two instances sharing the
            // directory) never overwrite each other, and a download that resumes on the other one notices and starts over.
            var segments = new List<G9CSnapshotFileSegmentRecord>(count);
            long position = 0;
            await using (var output = new FileStream(containerTemp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20))
            {
                for (var index = 0; index < count; index++)
                {
                    var raw0 = (long)index * segmentBytes;
                    var rawLength = (int)Math.Min(segmentBytes, length - raw0);
                    segments.Add(new G9CSnapshotFileSegmentRecord
                    {
                        Offset = position, Length = compressed[index].Length, RawLength = rawLength, Sha256 = hashes[index],
                        Rows = (int)(rows * (index + 1) / count - rows * index / count)
                    });
                    await output.WriteAsync(compressed[index], lifetime).ConfigureAwait(false);
                    position += compressed[index].Length;
                }

                await output.FlushAsync(lifetime).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }

            container = Path.Combine(_options.Directory, build.FileId + "-" + fileSha[..16] + ".g9seg");
            File.Move(containerTemp, container, overwrite: true);
            var record = new G9CSnapshotFileRecord
            {
                FileId = build.FileId,
                Identity = build.Identity,
                Snapshot = build.Snapshot,
                Epoch = build.Epoch,
                SnapshotUtc = build.SnapshotUtc,
                BuiltUtc = DateTime.UtcNow,
                Container = Path.GetFileName(container),
                Rows = rows,
                CompressedBytes = position,
                RowsByTable = tables.ToDictionary(t => t.TableId, t => t.Rows),
                Manifest = new G9DtSnapshotFileManifest
                {
                    FileId = build.FileId,
                    FormatVersion = G9SyncProtocol.SnapshotFileFormatVersion,
                    Bytes = length,
                    SegmentBytes = segmentBytes,
                    Segments = count,
                    Sha256 = fileSha,
                    Compression = "br",
                    BuiltUtc = DateTime.UtcNow,
                    Tables = tables.ToList()
                },
                Segments = segments
            };

            var recordTemp = RecordPath(build.FileId) + "." + unique + ".tmp";
            await File.WriteAllTextAsync(recordTemp, JsonSerializer.Serialize(record, G9CSnapshotFileJsonContext.Default.G9CSnapshotFileRecord), lifetime)
                .ConfigureAwait(false);
            File.Move(recordTemp, RecordPath(build.FileId), overwrite: true);
            TryDelete(raw);

            var elapsed = Stopwatch.GetElapsedTime(started);
            LogBuilt(_logger, build.FileId, build.SubscriptionKey, rows, length, position, (long)written.TotalMilliseconds, (long)elapsed.TotalMilliseconds);
            return new ReadyFile(record, container);
        }
        catch (Exception exception)
        {
            TryDelete(raw);
            TryDelete(containerTemp);
            LogBuildFailed(_logger, exception, build.FileId, build.SubscriptionKey);
            throw;
        }
        finally
        {
            _builds.Release();
        }
    }

    [LoggerMessage(EventId = 21203, Level = LogLevel.Information, Message = "Snapshot file {FileId} building for {Subscription}")]
    private static partial void LogBuildStarted(ILogger logger, string fileId, string subscription);

    [LoggerMessage(EventId = 21204, Level = LogLevel.Information,
        Message = "Snapshot file {FileId} built for {Subscription}: {Rows} rows, {RawBytes} bytes as SQLite, {CompressedBytes} compressed; rows written in {WriteMs} ms, ready in {ElapsedMs} ms")]
    private static partial void LogBuilt(ILogger logger, string fileId, string subscription, long rows, long rawBytes, long compressedBytes, long writeMs, long elapsedMs);

    [LoggerMessage(EventId = 21205, Level = LogLevel.Error, Message = "Snapshot file {FileId} for {Subscription} could not be built; devices that planned it get rows")]
    private static partial void LogBuildFailed(ILogger logger, Exception exception, string fileId, string subscription);
}
