# 03 — Build, test, release

## Prerequisites

| Need | For | Notes |
|---|---|---|
| .NET SDK 10.0.100 or later | everything | `global.json` uses `rollForward: latestFeature` |
| PostgreSQL 13+ (disposable) | integration tests, sample server | `xid8` / `pg_snapshot` functions need 13+; see [12-Testing.md](12-Testing.md) |
| Visual Studio C++ build tools | NativeAOT publish on Windows | not needed for normal builds. The ILCompiler link step calls `vswhere.exe` **from PATH**: add `C:\Program Files (x86)\Microsoft Visual Studio\Installer` to PATH or the link fails with `MSB3073` / exit code 123 |

## Commands

```powershell
dotnet build G9SyncData.slnx                                    # must be clean: warnings are errors
dotnet test tests/G9SyncData.Tests                              # no database
dotnet test tests/G9SyncData.IntegrationTests                   # disposable PostgreSQL
dotnet test tests/G9SyncData.Tests -p:G9SqliteBundleVersion=3.0.5   # same store binary on the SQLitePCLRaw 3.x stack (ADR-0032)
$env:G9SYNC_ANDROID = "auto"; dotnet test tests/G9SyncData.IntegrationTests --filter "FullyQualifiedName~Android"   # device/emulator (12-Testing)
dotnet publish samples/G9SyncData.Samples.AotClient -c Release -r win-x64   # NativeAOT proof
dotnet pack G9SyncData.slnx -c Release                          # → artifacts/pack
dotnet run -c Release --project benchmarks/G9SyncData.Benchmarks # disposable PostgreSQL; writes benchmarks/results/latest.md
```

When a build fails, filter the noise:

```powershell
dotnet build G9SyncData.slnx -nologo -clp:NoSummary 2>&1 | Select-String "error|warn" | Sort-Object -Unique
```

## Versioning and packing

- One version for the family: `<Version>` in `Directory.Build.props` (currently `0.1.0`).
- `IsPackable` is false by default and true only through `build/G9SyncData.Library.props` and the generator csproj, so
  tests and samples never pack.
- The source generator packs as an analyzer-only development dependency (`analyzers/dotnet/cs`).
- Before a release: both suites green, AOT publish clean, `AnalyzerReleases.Unshipped.md` rules moved to `Shipped.md`,
  [17-Known-Limitations.md](17-Known-Limitations.md) reviewed.

## Restore errors already met (and the fix that holds)

| Error | Cause | Fix |
|---|---|---|
| `NU1903` SQLitePCLRaw.lib.e_sqlite3 2.1.11 | CVE-2025-6965 in the native SQLite the full `Microsoft.Data.Sqlite` package bundles | the library references only `SQLitePCLRaw.core` (ADR-0032); apps/tests pick `SQLitePCLRaw.bundle_e_sqlite3` ≥ 2.1.13 |
| `NU1605` Logging.Abstractions downgrade | a project referenced 10.0.0 directly while SignalR.Client 10.0.12 needs ≥ 10.0.12 | do not reference it directly where a dependency already brings a higher one |
| `NU1510` package will not be pruned | `Microsoft.Extensions.Hosting` referenced in a project that has the ASP.NET Core framework reference | remove the redundant PackageReference |
| `RS1032` diagnostic message | a generator message contained `e.g.` (read as several sentences without a final period) | single-sentence messages without abbreviations |
