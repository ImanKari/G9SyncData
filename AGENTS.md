# Agent instructions — G9SyncData

G9SyncData is a redistributable library (NuGet family). Treat every public type, hub method name, error code and
frame byte as public API.

## Before the first change

1. Read [AiGuides/00-AiGuide.md](AiGuides/00-AiGuide.md) — the chapter map — then the chapters your task touches.
2. Read [AiGuides/01-Project-Rules.md](AiGuides/01-Project-Rules.md). Its rules are not optional.
3. Check [AiGuides/10-Decisions.md](AiGuides/10-Decisions.md) before changing anything a decision covers. Reversing a
   decision means a new ADR that supersedes it, not a silent edit.

## Always applies

- **NativeAOT is a requirement, not a goal.** Every `src/` library imports `build/G9SyncData.Library.props`
  (`IsAotCompatible`, trim/AOT analyzers, warnings as errors). No reflection, no `Activator`, no `MakeGenericType`, no
  reflection-based JSON. The sample `samples/G9SyncData.Samples.AotClient` must build and publish with zero warnings.
  See [14-AOT-And-Trimming.md](AiGuides/14-AOT-And-Trimming.md).
- **The build is a seam.** Never import a parent `Directory.Build.props` / `Directory.Packages.props` (ADR-0002). Package
  versions live only in `Directory.Packages.props`; a new package needs a reason in `10-Decisions.md`.
- **Guides move with the code.** A structural change, a new public type/option/table/code, a non-trivial fix or a
  decision updates the guides in the SAME change. Surprises and root causes go into
  [18-Engineering-Log.md](AiGuides/18-Engineering-Log.md).
- **Tests move with the code.** Every behaviour change or bug fix adds or updates a test. Run both suites before you say
  a change is done ([12-Testing.md](AiGuides/12-Testing.md)).
- **Databases.** Integration tests create and drop their own databases, so they only ever run against a disposable
  PostgreSQL server (`G9SYNC_TEST_PG`, default `localhost:15433`). Never point tests or samples at a shared or production
  database. Never print or store passwords.
- **Two clients, one change.** `js/` holds the TypeScript/Lynx twin of the client packages (`@g9tm/syncdata`,
  `@g9tm/syncdata-lynx`). Anything added to or fixed in either client is done in the other in the same change, with tests
  on both sides and `js/packages/syncdata/PARITY.md` updated; wire, hash, storage and schema changes regenerate the golden
  vectors (`G9_UPDATE_GOLDEN=1`). Run `npm run verify` in `js/` (and `npm run test:interop -w packages/syncdata`) next to
  the .NET suites. See [21-TypeScript-And-Lynx.md](AiGuides/21-TypeScript-And-Lynx.md).
- **One version, one scope.** Every npm package of `js/` is published under the npm organization `g9tm` (`@g9tm/…`) and
  carries exactly `G9SyncDataVersion` — the .NET packages' version, never one of its own (`js/scripts/check-versions.mjs`).
- **CI is Azure Pipelines only** (definition 28). It tests, packs and publishes NuGet + npm, then mirrors the repository
  to GitHub. GitHub is a read-only copy: no workflows there (`.github/workflows` must not exist).
- **Consumers move with a release.** After a new version is on nuget.org/npm, upgrade every consumer (AgriPad app,
  Server.Mobile, …) and bump the consumer's own version.
- **Git.** Never commit, push or open a pull request unless the user asks in that turn.

## Environment

- .NET SDK 10.0.1xx or later (`global.json` rolls forward to the latest feature band).
- Node.js 22.12+ for `js/` (Node 24 here); JDK 17+ and Gradle for the Lynx module's Android checks; WSL Ubuntu with
  GNUstep for its iOS core test.
- NativeAOT publish on Windows needs the Visual Studio C++ build tools.
- Shell: PowerShell on Windows; commands in the guides are PowerShell unless marked otherwise.
