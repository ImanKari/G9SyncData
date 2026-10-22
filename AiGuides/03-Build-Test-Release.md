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

The TypeScript/Lynx twin (`js/`, Node 22.12+; [21-TypeScript-And-Lynx.md](21-TypeScript-And-Lynx.md)):

```powershell
cd js; npm ci; npm run verify              # versions, typecheck, unit tests, Lynx sandbox, build, Lynx runtime gate
npm run test:interop -w packages/syncdata  # against the real sample server + database parity with the .NET client (disposable PostgreSQL)
npm run test:android-jvm -w packages/lynx; npm run check:android-module -w packages/lynx; npm run test:ios-core -w packages/lynx
npm run pack:all                           # → js/artifacts/npm
```

When a build fails, filter the noise:

```powershell
dotnet build G9SyncData.slnx -nologo -clp:NoSummary 2>&1 | Select-String "error|warn" | Sort-Object -Unique
```

## Versioning and packing

- One version for the family: **`<G9SyncDataVersion>` in `Directory.Build.props`** (currently `1.5.5`). `<Version>` only
  consumes it. The indirection exists so a consuming repository can pin the family from one property — in an
  application project `$(Version)` means "the app's version", not "the version of the packages it references".
- ⛔ **The family releases TOGETHER and must never be mixed.** The protocol frame layout is negotiated across
  Client/Server/Protocol, so a client from one version against a server from another is exactly the mismatch the
  version is there to prevent.
- `IsPackable` is false by default and true only through `build/G9SyncData.Library.props` and two csprojs, so tests,
  samples and benchmarks never pack. Eleven packages come out of a full pack; ten have a `.snupkg`.
- The source generator packs as an analyzer-only development dependency (`analyzers/dotnet/cs`) and sets
  `IncludeSymbols=false`. ⛔ It has no `lib/` assembly, so a symbol package would have nothing to carry and pack fails
  with **NU5017** *after* writing a perfectly good `.nupkg` — a non-zero exit with a correct package on disk, which is
  a confusing shape to debug. Do not "fix" that by turning symbols back on.
- The generator **also ships inside `G9SyncData.Client`** (`analyzers/dotnet/cs`), which is what lets a consumer get it
  with no extra reference. Verified 2026-09-20 against AgriPad in package mode: `G9SyncGeneratedModel.g.cs` is emitted
  from a plain `PackageReference` to `G9SyncData.Client.Sqlite`. ⛔ A consumer must therefore NOT also reference
  `G9SyncData.SourceGenerators` — the generator would load twice and every generated partial would collide.
- Before a release: both suites green, AOT publish clean, `AnalyzerReleases.Unshipped.md` rules moved to `Shipped.md`,
  [17-Known-Limitations.md](17-Known-Limitations.md) reviewed.

## Releasing

`azure-pipelines.yml` builds, tests, packs, mirrors to GitHub and publishes to nuget.org. It runs on **ubuntu-latest**,
because every project targets `net10.0` (`netstandard2.0` for the generator) and the end-to-end suite needs a real
PostgreSQL.

⛔ **PostgreSQL is started with `docker run`, not as an Azure `services:` container**, and that is load-bearing. The WAL
backend needs `wal_level=logical`, which postgres takes as a server ARGUMENT; an Azure `container:` resource splices
its `options` into `docker create` *before* the image name, so it can pass flags but never arguments, and
`POSTGRES_INITDB_ARGS` cannot reach `wal_level` either. On the default `wal_level=replica` the **11**
`[PostgresLogicalFact]` tests (6 in `WalCaptureModeTests`, 5 in `LogicalReplicationChangeSourceTests`) **skip** —
reported as skipped, not failed — so the capture mode these guides recommend had zero coverage and a regression in it
would have passed CI. That is exactly what happened on the first green run (build 461: 149 passed, 16 skipped).

Build 462, with the argument set: **160 passed, 5 skipped** — all 11 WAL tests now run. ⛔ The 5 that remain are
`AndroidDeviceTests` and are SUPPOSED to skip on a hosted agent: they need a real device or emulator. Do not chase
that number to zero, and do not let it hide a WAL regression — that is what the assert step is for.

A dedicated step now asserts `current_setting('wal_level') = 'logical'` and **fails the build** if it is not, so the
tests can never silently go back to skipping. A later step reports any replication slot the suite left behind — as a
warning, not a failure, because a slot survives a dropped database and wedges the NEXT run, which is worth seeing
without burying a good test result.

**The pipeline computes no version.** Bump `G9SyncDataVersion` by hand, with release notes, and let it publish. A
re-run without a bump republishes nothing — the skip-duplicate flag makes that a no-op rather than a failure.

Three SECRET pipeline variables are required:

| Variable | Scope | Used for |
|---|---|---|
| `AzureDevOps.PAT` | Code (Read **& Write**) on g9tm | clones this repo for the GitHub mirror, and pushes the release tag |
| `Github.PAT` | repo | creates/pushes `github.com/ImanKari/G9SyncData` |
| `NuGetApiKey` | Push on nuget.org | publishes the packages |

**Every release is tagged `v<G9SyncDataVersion>`** on the commit the packages were built from, BEFORE the mirror so the tag reaches
GitHub with it. The step only WARNS when it cannot: a PAT with Code (Read) alone cannot push a tag, and a tag that already exists on
another commit means `main` moved without a version bump - in which case the publish below skips every package as a duplicate, and
the warning is the only thing that says why.

Order that matters, and why:

1. **The `github` remote is added before pack.** SourceLink maps only `github.com` remotes, and this repo's `origin` is
   the private Azure DevOps one. `GitRepositoryRemoteName=github` (set when `CI=true`) tells the Git task which remote
   to read. Left on `origin` it would emit no `source-link.json` at all; pointed at Azure it would emit links that 403
   for every consumer, which is worse than none.
2. **The mirror runs before the NuGet push.** The packages' SourceLink points at github.com at a specific commit;
   publishing first would put packages on nuget.org whose source links 404 until the mirror happens to succeed.

A **pull-request** build runs everything up to and including pack and publishes the packages as a build artifact, but
never mirrors and never pushes. That is the point: a PR proves the packages can be produced without making any of it
public.

### Releasing by hand (when the pipeline cannot run)

Used for 1.5.2 – 1.5.5 (2026-10-22): the Azure DevOps free minutes were used up. The same steps as the pipeline, in its order:

1. Bump `G9SyncDataVersion` **and** the npm packages (`js/packages/*/package.json`, their `@g9tm/syncdata` dependency, the
   lockfile, the Lynx module's version constants: `src/index.ts`, `lynxtron/index.ts`, `G9SyncDataLynxModule.kt` / `.m`,
   the podspec, `ios/Tests/linux/main.m`) — `npm run verify` checks they agree. Update `README.md` "What's new".
2. Unit tests + `npm run verify` green; commit on `main`, tag `v<version>`, push both to `origin`.
3. A CLEAN clone of the tag in a SHORT path (`D:\g9r-sd`), `CI=true`, a `github` remote
   (`https://github.com/ImanKari/G9SyncData.git`) for SourceLink, `dotnet build G9SyncData.slnx -c Release`, then
   `dotnet pack <each csproj of the pipeline's libraries list> -c Release --no-build -o <out>` (12 `.nupkg`, 11 `.snupkg`).
   Check one nuspec: `<repository url="https://github.com/…" commit="<the tag's commit>">`.
4. **Mirror BEFORE publishing**: push `main` and the tag to GitHub (a token through `http.extraheader`; the credential
   manager prompt hangs a non-interactive shell).
5. `dotnet nuget push <each .nupkg> --source https://api.nuget.org/v3/index.json --skip-duplicate` (symbols go with it).
6. `npm run pack:all` in `js/`, then `npm publish artifacts/npm/<tgz> --access public` with a temporary `--userconfig`.
7. Consumers pin only once the Nexus group lists the version (or through the app's temporary `nuget-local`, AgriPad guide 19).

### Verifying a version locally before publishing

`dotnet pack` writes to `artifacts/pack`. Point a consumer at that folder as a package source and the exact package the
pipeline would publish can be built against first:

```bash
dotnet pack G9SyncData.slnx -c Release          # -> artifacts/pack
dotnet restore <consumer.csproj> --configfile <a config listing artifacts/pack as a source>
```

⛔ Do NOT pass the folder with `dotnet restore -s <path>` from Git Bash — MSYS rewrites the `https://` sources
alongside it into local paths and restore fails with `NU1301` naming a nonsense directory. A `--configfile` avoids the
rewrite entirely.

## Restore errors already met (and the fix that holds)

| Error | Cause | Fix |
|---|---|---|
| `NU1903` SQLitePCLRaw.lib.e_sqlite3 2.1.11 | CVE-2025-6965 in the native SQLite the full `Microsoft.Data.Sqlite` package bundles | the library references only `SQLitePCLRaw.core` (ADR-0032); apps/tests pick `SQLitePCLRaw.bundle_e_sqlite3` ≥ 2.1.13 |
| `NU1605` Logging.Abstractions downgrade | a project referenced 10.0.0 directly while SignalR.Client 10.0.12 needs ≥ 10.0.12 | do not reference it directly where a dependency already brings a higher one |
| `NU1510` package will not be pruned | `Microsoft.Extensions.Hosting` referenced in a project that has the ASP.NET Core framework reference | remove the redundant PackageReference |
| `RS1032` diagnostic message | a generator message contained `e.g.` (read as several sentences without a final period) | single-sentence messages without abbreviations |
