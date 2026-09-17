using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

using G9SyncData.IntegrationTests.Infrastructure;

namespace G9SyncData.IntegrationTests.Android;

/// <summary>
///     The Android target of the device tests (AiGuides/12-Testing.md → "Android"): adb, the chosen serial, building and installing the
///     device test app once per test run, starting a scenario and reading its results.
/// </summary>
/// <remarks>
///     Opt-in, never implicit: <c>G9SYNC_ANDROID=auto</c> picks the only attached device, <c>G9SYNC_ANDROID=&lt;serial&gt;</c> pins one.
///     Unset, every Android test is skipped - an attached emulator is often somebody's working device. The device test app is its own
///     package; it never touches another app on the device.
/// </remarks>
internal sealed class AndroidDevice
{
    public const string Package = "com.g9tm.g9syncdata.devicetests";
    private const string Activity = Package + "/.MainActivity";
    private const string RunsDirectory = "/sdcard/Android/data/" + Package + "/files/runs";

    private static readonly Lazy<(AndroidDevice? Device, string? SkipReason)> Resolved = new(Resolve);
    private static readonly SemaphoreSlim InstallGate = new(1, 1);
    private static bool _installed;

    private AndroidDevice(string adb, string serial, string abi, string model)
    {
        Adb = adb;
        Serial = serial;
        Abi = abi;
        Model = model;
    }

    public string Adb { get; }
    public string Serial { get; }
    public string Abi { get; }
    public string Model { get; }
    public string RuntimeIdentifier => Abi.StartsWith("x86_64", StringComparison.Ordinal) ? "android-x64" : "android-arm64";

    public static string? SkipReason => Resolved.Value.SkipReason;

    public static AndroidDevice Current => Resolved.Value.Device ?? throw new InvalidOperationException(Resolved.Value.SkipReason);

    /// <summary>Builds the device test app (Release, the device's ABI) and installs it, once per test process.</summary>
    public async Task EnsureInstalledAsync(ITestOutputHelper output)
    {
        await InstallGate.WaitAsync();
        try
        {
            if (_installed) return;
            var project = Path.Combine(RepositoryRoot, "tests", "G9SyncData.DeviceTests.Android", "G9SyncData.DeviceTests.Android.csproj");
            var configuration = Environment.GetEnvironmentVariable("G9SYNC_ANDROID_CONFIGURATION") is { Length: > 0 } c ? c : "Release";
            if (!string.Equals(Environment.GetEnvironmentVariable("G9SYNC_ANDROID_BUILD"), "skip", StringComparison.OrdinalIgnoreCase))
            {
                var stopwatch = Stopwatch.StartNew();
                var (code, text) = await RunAsync("dotnet", ["build", project, "-c", configuration, "-p:RuntimeIdentifier=" + RuntimeIdentifier, "-nologo", "-v:q"],
                    TimeSpan.FromMinutes(20));
                if (code != 0) throw new InvalidOperationException("Building the device test app failed:\n" + Tail(text, 4000));
                output.WriteLine($"device test app built in {stopwatch.Elapsed.TotalSeconds:N0} s");
            }

            var apk = Directory.GetFiles(Path.Combine(Path.GetDirectoryName(project)!, "bin", configuration, "net10.0-android", RuntimeIdentifier), "*-Signed.apk")
                .Single();
            var (installCode, installText) = await AdbAsync(["install", "-r", apk], TimeSpan.FromMinutes(5));
            if (installCode != 0 || !installText.Contains("Success", StringComparison.Ordinal))
            {
                // A previous build signed with another key: replace OUR package only.
                await AdbAsync(["uninstall", Package], TimeSpan.FromMinutes(1));
                (installCode, installText) = await AdbAsync(["install", apk], TimeSpan.FromMinutes(5));
                if (installCode != 0 || !installText.Contains("Success", StringComparison.Ordinal)) throw new InvalidOperationException("Install failed: " + installText);
            }

            _installed = true;
        }
        finally
        {
            InstallGate.Release();
        }
    }

    /// <summary>Starts a scenario in a fresh process and waits for its report. Throws with the device log when it does not finish.</summary>
    public async Task<DeviceRun> RunScenarioAsync(string scenario, IReadOnlyDictionary<string, string> extras, TimeSpan timeout, ITestOutputHelper output)
    {
        var runId = scenario + "-" + DateTime.UtcNow.ToString("HHmmssfff", CultureInfo.InvariantCulture);
        var arguments = new List<string> { "shell", "am", "start", "-S", "-W", "-n", Activity, "--es", "run", runId, "--es", "scenario", scenario };
        foreach (var (key, value) in extras) arguments.AddRange(["--es", key, value]);
        var (code, text) = await AdbAsync(arguments, TimeSpan.FromMinutes(1));
        if (code != 0) throw new InvalidOperationException("am start failed: " + text);

        var directory = RunsDirectory + "/" + runId;
        var stopwatch = Stopwatch.StartNew();
        string status;
        while (true)
        {
            status = (await AdbAsync(["shell", "cat", directory + "/status"], TimeSpan.FromSeconds(30))).Text.Trim();
            if (status is "passed" or "failed") break;
            if (stopwatch.Elapsed > timeout)
            {
                var logcat = (await AdbAsync(["logcat", "-d", "-t", "400", "-s", "G9DEVICE:V", "AndroidRuntime:E", "DOTNET:V", "monodroid:V"], TimeSpan.FromSeconds(30))).Text;
                throw new TimeoutException($"Scenario {scenario} did not finish in {timeout} (status '{status}').\n{Tail(logcat, 6000)}");
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        var log = (await AdbAsync(["shell", "cat", directory + "/log.txt"], TimeSpan.FromSeconds(30))).Text;
        var report = (await AdbAsync(["shell", "cat", directory + "/report.json"], TimeSpan.FromSeconds(30))).Text;
        output.WriteLine(log);
        await AdbAsync(["shell", "rm", "-rf", directory], TimeSpan.FromSeconds(30));
        return new DeviceRun(runId, status, log, JsonDocument.Parse(report));
    }

    /// <summary>Makes the host's <paramref name="port" /> reachable as 127.0.0.1:<paramref name="port" /> on the device (emulator or USB).</summary>
    public async Task<IAsyncDisposable> ReversePortAsync(int port)
    {
        var (code, text) = await AdbAsync(["reverse", $"tcp:{port}", $"tcp:{port}"], TimeSpan.FromSeconds(30));
        if (code != 0) throw new InvalidOperationException("adb reverse failed: " + text);
        return new PortReverse(this, port);
    }

    public Task<(int Code, string Text)> AdbAsync(IReadOnlyList<string> arguments, TimeSpan timeout) =>
        RunAsync(Adb, ["-s", Serial, .. arguments], timeout);

    public static string RepositoryRoot { get; } = FindRepositoryRoot();

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "G9SyncData.slnx"))) return directory.FullName;
        }

        throw new InvalidOperationException("G9SyncData.slnx not found above " + AppContext.BaseDirectory);
    }

    private static (AndroidDevice?, string?) Resolve()
    {
        var setting = Environment.GetEnvironmentVariable("G9SYNC_ANDROID");
        if (string.IsNullOrWhiteSpace(setting)) return (null, "Android tests are opt-in: set G9SYNC_ANDROID=auto (or a device serial).");
        var adb = FindAdb();
        if (adb is null) return (null, "adb not found (set ANDROID_HOME or ANDROID_SDK_ROOT).");

        var (code, text) = RunAsync(adb, ["devices"], TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
        if (code != 0) return (null, "adb devices failed: " + text);
        var serials = text.Split('\n').Skip(1).Select(l => l.Trim().Split('\t')).Where(p => p.Length == 2 && p[1] == "device").Select(p => p[0]).ToList();
        string serial;
        if (string.Equals(setting, "auto", StringComparison.OrdinalIgnoreCase))
        {
            if (serials.Count != 1) return (null, $"G9SYNC_ANDROID=auto needs exactly one attached device; found {serials.Count}.");
            serial = serials[0];
        }
        else
        {
            if (!serials.Contains(setting)) return (null, $"Device {setting} is not attached.");
            serial = setting;
        }

        var abi = RunAsync(adb, ["-s", serial, "shell", "getprop", "ro.product.cpu.abi"], TimeSpan.FromSeconds(30)).GetAwaiter().GetResult().Text.Trim();
        var model = RunAsync(adb, ["-s", serial, "shell", "getprop", "ro.product.model"], TimeSpan.FromSeconds(30)).GetAwaiter().GetResult().Text.Trim();
        return (new AndroidDevice(adb, serial, abi, model), null);
    }

    private static string? FindAdb()
    {
        var roots = new[]
        {
            Environment.GetEnvironmentVariable("ANDROID_HOME"),
            Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT"),
            @"C:\Program Files (x86)\Android\android-sdk",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk")
        };
        foreach (var root in roots.Where(r => !string.IsNullOrWhiteSpace(r)))
        {
            var candidate = Path.Combine(root!, "platform-tools", OperatingSystem.IsWindows() ? "adb.exe" : "adb");
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    private static async Task<(int Code, string Text)> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        var start = new ProcessStartInfo(fileName) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start " + fileName);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{fileName} {string.Join(' ', arguments)} did not finish in {timeout}.");
        }

        var text = new StringBuilder().Append(await stdout).Append(await stderr).ToString();
        return (process.ExitCode, text);
    }

    private static string Tail(string text, int characters) => text.Length <= characters ? text : text[^characters..];

    private sealed class PortReverse(AndroidDevice device, int port) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await device.AdbAsync(["reverse", "--remove", $"tcp:{port}"], TimeSpan.FromSeconds(30));
    }
}

/// <summary>One finished scenario: its status, device log and report.</summary>
internal sealed record DeviceRun(string RunId, string Status, string Log, JsonDocument Report) : IDisposable
{
    public JsonElement Root => Report.RootElement;

    public long Long(string name) => Root.GetProperty(name).GetInt64();

    public JsonElement Step(string step) => Root.GetProperty("steps").EnumerateArray().Single(s => s.GetProperty("step").GetString() == step);

    public void Dispose() => Report.Dispose();
}

/// <summary>An Android test: skipped unless G9SYNC_ANDROID selects a device (and, for sync tests, PostgreSQL answers).</summary>
internal sealed class AndroidFactAttribute : FactAttribute
{
    public AndroidFactAttribute(bool needsPostgres = false) => Skip = AndroidDevice.SkipReason ?? (needsPostgres ? PgTestServer.UnavailableReason : null);
}

/// <inheritdoc cref="AndroidFactAttribute" />
internal sealed class AndroidTheoryAttribute : TheoryAttribute
{
    public AndroidTheoryAttribute(bool needsPostgres = false) => Skip = AndroidDevice.SkipReason ?? (needsPostgres ? PgTestServer.UnavailableReason : null);
}
