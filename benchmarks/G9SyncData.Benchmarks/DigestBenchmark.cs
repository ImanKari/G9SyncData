using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace G9SyncData.Benchmarks;

/// <summary>
///     The reconciliation hash on the current platform: <c>md5(key)</c> + <c>md5(key:version)</c> per row, through the platform MD5 (JNI on
///     Android) and through the library's managed MD5 (<see cref="G9CSyncDigest" />). Run with <c>--digest [--rows N]</c>, or on a device
///     through the Android test app.
/// </summary>
internal static class DigestBenchmark
{
    internal sealed record Result(string Name, TimeSpan Elapsed, double RowsPerSecond, long AllocatedBytes);

    public static IReadOnlyList<Result> Run(int rows, Action<string>? log = null)
    {
        log ??= Console.WriteLine;
        var keys = Enumerable.Range(0, rows).Select(_ => Encoding.UTF8.GetBytes(Guid.NewGuid().ToString("D"))).ToArray();
        var results = new List<Result>();
        long check = 0;

        results.Add(Measure("platform MD5 (System.Security.Cryptography)", rows, () =>
        {
            long xor = 0;
            Span<byte> hash = stackalloc byte[16];
            Span<byte> buffer = stackalloc byte[64];
            for (var i = 0; i < keys.Length; i++)
            {
#pragma warning disable CA5351 // the reconciliation hash is MD5 by definition (ADR-0012); this is the comparison baseline
                MD5.HashData(keys[i], hash);
                keys[i].CopyTo(buffer);
                buffer[keys[i].Length] = (byte)':';
                i.TryFormat(buffer[(keys[i].Length + 1)..], out var digits, default, CultureInfo.InvariantCulture);
                MD5.HashData(buffer[..(keys[i].Length + 1 + digits)], hash);
#pragma warning restore CA5351
                xor ^= BinaryPrimitives.ReadInt64BigEndian(hash);
            }

            check = xor;
        }, log));

        results.Add(Measure("managed MD5 (G9CSyncDigest)", rows, () =>
        {
            long xor = 0;
            for (var i = 0; i < keys.Length; i++)
            {
                _ = G9CSyncDigest.GetBucket(keys[i]);
                xor ^= G9CSyncDigest.GetRowHash(keys[i], i);
            }

            if (xor != check) throw new InvalidOperationException("The managed and platform digests differ.");
        }, log));

        return results;
    }

    private static Result Measure(string name, int rows, Action action, Action<string> log)
    {
        action();   // warm-up (JIT, first JNI lookups)
        var allocated = GC.GetTotalAllocatedBytes(precise: true);
        var stopwatch = Stopwatch.StartNew();
        action();
        var elapsed = stopwatch.Elapsed;
        var result = new Result(name, elapsed, rows / elapsed.TotalSeconds, GC.GetTotalAllocatedBytes(precise: true) - allocated);
        log($"| {name} | {elapsed.TotalMilliseconds:N0} ms | {result.RowsPerSecond:N0} rows/s | {result.AllocatedBytes / 1048576d:N1} MB |");
        return result;
    }
}
