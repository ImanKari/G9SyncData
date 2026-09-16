using Microsoft.Extensions.Logging;

namespace G9SyncData.IntegrationTests.Infrastructure;

/// <summary>Routes library logs into the test output, so a failing test shows what the engines did.</summary>
internal sealed class XunitLoggerProvider(ITestOutputHelper output) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new XunitLogger(output, categoryName[(categoryName.LastIndexOf('.') + 1)..]);

    public void Dispose()
    {
    }

    private sealed class XunitLogger(ITestOutputHelper output, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            try
            {
                output.WriteLine($"[{logLevel}] {category}: {formatter(state, exception)}{(exception is null ? string.Empty : Environment.NewLine + exception)}");
            }
            catch (InvalidOperationException)
            {
                // The test already finished; late background logs are dropped.
            }
        }
    }
}
