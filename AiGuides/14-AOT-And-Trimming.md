# 14 — NativeAOT and trimming

ADR-0003 makes AOT compatibility a requirement. This chapter is how it stays true.

## How it is enforced

1. `build/G9SyncData.Library.props` (imported by every `src/` library): `IsAotCompatible`, `EnableTrimAnalyzer`,
   `EnableSingleFileAnalyzer`, `EnableAotAnalyzer`; `Directory.Build.props` makes warnings errors.
2. `samples/G9SyncData.Samples.AotClient` sets `PublishAot`, which runs the AOT analyzers over the **whole client stack as
   consumed by an app** at build time — including Microsoft.Data.Sqlite, SignalR client and G9SignalRSuperNetCore.
3. Before a release, publish it and run it against the sample server:

```powershell
$env:PATH = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer;$env:PATH"   # the native link step needs vswhere.exe
dotnet publish samples/G9SyncData.Samples.AotClient -c Release -r win-x64
samples\G9SyncData.Samples.AotClient\bin\Release\net10.0\win-x64\publish\G9SyncData.Samples.AotClient.exe http://localhost:5210/g9sync
```

Last verified 2026-09-16 (client telemetry included): zero trim/AOT warnings, 10.3 MB executable plus `e_sqlite3.dll`. On
2026-09-15 the native executable
synced the sample server over SignalR: initial pull of 50,000 pots applied in 1.2 s, the following delta of 80 rows in
17 ms (development machine, loopback — not a device measurement).

## Patterns to use

| Need | Use | Not |
|---|---|---|
| JSON | `G9SyncJsonContext.Default.X` / `G9SyncClientJsonContext`; register new types on the context | `JsonSerializer.Serialize(obj)` without type info |
| SignalR JSON | insert the context at the head of `TypeInfoResolverChain` (both sides do) | default reflection resolver |
| Logging | `[LoggerMessage]` partial methods | `logger.LogInformation($"...")` |
| Regex | `[GeneratedRegex]` | `new Regex(...)` |
| Entity model | the source generator | reflection over properties |
| Row values | `G9CRowBatchWriter`/`Reader` typed methods | `object` boxing through reflection |
| DI generic registration | `[DynamicallyAccessedMembers(PublicConstructors)]` on the type parameter | unannotated generics |

## Native SQLite under AOT

`Microsoft.Data.Sqlite` discovers the native provider by reflection unless initialised. Apps call
`SQLitePCL.Batteries_V2.Init()` before the first connection (the AOT sample does). The library itself references only
`Microsoft.Data.Sqlite.Core` (ADR-0009).

## SignalR under AOT

- The client transport uses `InvokeCoreAsync<T>` / `StreamAsyncCore<T>` with a source-generated resolver: AOT-safe.
- Client-to-server streams must carry reference types (value-type `IAsyncEnumerable<T>` items are unsupported by SignalR
  under AOT).
- The server hub constructor is `[RequiresDynamicCode]` — typed `Hub<T>` proxies need dynamic code. The attribute is kept
  visible so an AOT-published server reports it.

## MAUI

The client packages need no AOT exclusions and no iOS interpreter (plan P-15). When adopting in a MAUI app, keep the
app's own trimming settings; do not add `TrimmerRootAssembly` for G9SyncData assemblies — if something needs one, that is
a library bug to fix here.
