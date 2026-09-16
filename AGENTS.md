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
- **Git.** Never commit, push or open a pull request unless the user asks in that turn.

## Environment

- .NET SDK 10.0.1xx or later (`global.json` rolls forward to the latest feature band).
- NativeAOT publish on Windows needs the Visual Studio C++ build tools.
- Shell: PowerShell on Windows; commands in the guides are PowerShell unless marked otherwise.
