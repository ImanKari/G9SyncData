using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace G9SyncData.IntegrationTests.Infrastructure;

/// <summary>
///     The SQLite store no longer initialises a native provider implicitly (Microsoft.Data.Sqlite did, by reflection): the app does, once,
///     and so does this test assembly.
/// </summary>
internal static class SqliteProviderInitializer
{
    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255:The 'ModuleInitializer' attribute should not be used in libraries", Justification = "Test assembly.")]
    internal static void Initialize() => SQLitePCL.Batteries_V2.Init();
}
