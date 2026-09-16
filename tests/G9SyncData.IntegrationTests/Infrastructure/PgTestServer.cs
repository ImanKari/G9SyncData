using Npgsql;

namespace G9SyncData.IntegrationTests.Infrastructure;

/// <summary>Where the disposable PostgreSQL test server is, and whether it answers.</summary>
internal static class PgTestServer
{
    private static readonly Lazy<string?> Unavailable = new(Probe);

    public static string AdminConnectionString { get; } =
        Environment.GetEnvironmentVariable("G9SYNC_TEST_PG") is { Length: > 0 } configured
            ? configured
            : "Host=localhost;Port=15433;Username=postgres;Database=postgres";

    /// <summary>Null when the server answers; otherwise why it does not.</summary>
    public static string? UnavailableReason => Unavailable.Value;

    private static string? Probe()
    {
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(AdminConnectionString) { Timeout = 3, Pooling = false };
            using var connection = new NpgsqlConnection(builder.ConnectionString);
            connection.Open();
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}

/// <summary>A fact that is skipped (not failed) when no PostgreSQL test server answers.</summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (PgTestServer.UnavailableReason is { } reason) Skip = "PostgreSQL test server unavailable (set G9SYNC_TEST_PG): " + reason;
    }
}
