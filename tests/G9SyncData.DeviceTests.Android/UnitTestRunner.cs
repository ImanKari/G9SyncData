using System.Diagnostics;
using System.Reflection;
using Xunit;
using Xunit.Abstractions;

namespace G9SyncData.DeviceTests;

/// <summary>
///     Runs the linked xUnit test classes on the device: <c>[Fact]</c>, <c>[Theory]</c> with <c>[InlineData]</c>, <c>IAsyncLifetime</c>,
///     <c>IDisposable</c> / <c>IAsyncDisposable</c> and an optional <c>ITestOutputHelper</c> constructor - exactly what those classes use.
///     Sequential on purpose: device numbers must not depend on parallel scheduling. A full xUnit device runner would add a UI stack and
///     another moving part for no additional coverage.
/// </summary>
public static class UnitTestRunner
{
    public static async Task<bool> RunAsync(Assembly assembly, DeviceReport report, Action<string> log)
    {
        var passed = 0;
        var failed = 0;
        var skipped = 0;
        var total = Stopwatch.StartNew();
        foreach (var type in assembly.GetTypes().Where(t => t.IsClass && !t.IsAbstract && t.Namespace?.StartsWith("G9SyncData.Tests", StringComparison.Ordinal) == true)
                     .OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance).OrderBy(m => m.Name, StringComparer.Ordinal))
            {
                var fact = method.GetCustomAttribute<FactAttribute>();
                if (fact is null) continue;
                var cases = method.GetCustomAttribute<TheoryAttribute>() is null
                    ? [[]]
                    : method.GetCustomAttributes<InlineDataAttribute>().SelectMany(d => d.GetData(method)).ToList();
                foreach (var arguments in cases)
                {
                    var name = $"{type.Name}.{method.Name}" + (arguments.Length == 0 ? string.Empty : $"({string.Join(", ", arguments)})");
                    if (fact.Skip is { } reason)
                    {
                        skipped++;
                        report.Add("tests", ("name", name), ("outcome", "skipped"), ("message", reason));
                        continue;
                    }

                    var stopwatch = Stopwatch.StartNew();
                    try
                    {
                        await RunOneAsync(type, method, arguments, log).WaitAsync(TimeSpan.FromMinutes(3));
                        passed++;
                        report.Add("tests", ("name", name), ("outcome", "passed"), ("ms", stopwatch.Elapsed.TotalMilliseconds));
                    }
                    catch (Exception exception)
                    {
                        failed++;
                        var cause = exception is TargetInvocationException { InnerException: { } inner } ? inner : exception;
                        report.Add("tests", ("name", name), ("outcome", "failed"), ("ms", stopwatch.Elapsed.TotalMilliseconds), ("message", cause.ToString()));
                        log($"FAIL {name}: {cause.Message}");
                    }
                }
            }
        }

        report.Set("passed", passed);
        report.Set("failed", failed);
        report.Set("skipped", skipped);
        report.Set("totalMs", total.Elapsed.TotalMilliseconds);
        log($"unit tests: {passed} passed, {failed} failed, {skipped} skipped in {total.Elapsed.TotalSeconds:N1} s");
        return failed == 0 && passed > 0;
    }

    private static async Task RunOneAsync(Type type, MethodInfo method, object?[] arguments, Action<string> log)
    {
        var constructor = type.GetConstructors().OrderBy(c => c.GetParameters().Length).First();
        var instance = constructor.GetParameters().Length == 0
            ? constructor.Invoke([])
            : constructor.Invoke([new OutputHelper(log)]);
        try
        {
            if (instance is IAsyncLifetime lifetime) await lifetime.InitializeAsync();
            var result = method.Invoke(instance, arguments.Length == 0 ? null : arguments);
            if (result is Task task) await task;
            else if (result is ValueTask valueTask) await valueTask;
        }
        finally
        {
            if (instance is IAsyncLifetime lifetime) await lifetime.DisposeAsync();
            else if (instance is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync();
            else if (instance is IDisposable disposable) disposable.Dispose();
        }
    }

    private sealed class OutputHelper(Action<string> log) : ITestOutputHelper
    {
        public void WriteLine(string message) => log("  " + message);

        public void WriteLine(string format, params object[] args) => log("  " + string.Format(System.Globalization.CultureInfo.InvariantCulture, format, args));
    }
}
