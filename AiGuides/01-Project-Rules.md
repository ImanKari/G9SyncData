# 01 — Project rules

These rules exist because G9SyncData is a library: every consumer inherits its dependencies, its warnings and its
mistakes. When a rule and a shortcut disagree, the rule wins.

## Naming (G9 family convention)

| Prefix | Kind | Example |
|---|---|---|
| `G9C` | class | `G9CSyncEngine`, `G9CSqliteSyncStore` |
| `G9Dt` | data/DTO type (class or record) | `G9DtPullPlan`, `G9DtSyncProgress` |
| `G9E` | enum | `G9ESyncPhase` |
| `G9Attr` | attribute (class name ends in `Attribute`) | `G9AttrSyncTableAttribute` |
| `IG9` | interface | `IG9SyncTransport` |
| `G9A` | abstract base (from G9SignalRSuperNetCore) | `G9AHubBase<,>` |

Exceptions that follow platform convention: DI extension classes (`G9Sync…Extensions`, in namespace
`Microsoft.Extensions.DependencyInjection`), the hub (`G9SyncHub`), static helper classes (`G9SyncProtocol`,
`G9SyncErrorCodes`, `G9SyncHubErrors`), JSON contexts (`G9SyncJsonContext`).

- Files: one public type per file, file name = type name; partial classes split by concern as `Type.Concern.cs`.
- Namespaces follow folders, file-scoped. DI extensions deliberately use `Microsoft.Extensions.DependencyInjection` so
  `services.AddG9SyncData…()` / `builder.UseSqlite()` need no extra `using` — a new extension that does not do this is a bug
  (see the 2026-09-15 log entry).
- Wire and SQL identifiers are lower snake case on SQLite (`g9_rows`, `local_seq`) and PostgreSQL (`g9sync.clients`).

## NativeAOT and trimming (ADR-0003)

- Every `src/` library imports `build/G9SyncData.Library.props`. Warnings are errors, so an IL2xxx/IL3xxx finding breaks
  the build. Do not suppress one without an ADR.
- JSON only through source-generated contexts: `G9SyncJsonContext` (wire DTOs) and `G9SyncClientJsonContext` (client
  metadata). A new serialised type is registered there in the same change.
- Logging only through `[LoggerMessage]` partial methods with an explicit `EventId`. Ranges: `200xx` client engine,
  `201xx` SQLite store, `202xx` client SignalR transport; `210xx` server engine, hosting and notifier, `211xx` server
  SignalR; `220xx` PostgreSQL store. A new package takes the next free hundred on its side. Grep `EventId =` for the next
  free id; ids are part of operators' log filters, so never reuse one.
- Regexes through `[GeneratedRegex]`.
- No `Activator`, `Type.GetType`, `MakeGenericType`, `Expression.Compile`, or reflection over entity properties at runtime —
  the source generator does that at compile time.
- DI registration methods with open generic type parameters carry `[DynamicallyAccessedMembers(PublicConstructors)]`.
- The one accepted `[RequiresDynamicCode]` is the SignalR hub constructor (ASP.NET Core typed hubs need it); it is
  surfaced, not hidden.

## Errors

- Expected failures throw `G9CSyncException` with a code from `G9SyncErrorCodes`. Callers switch on `Code`, never on
  message text. Codes are public API: add, never rename or repurpose ([15-Error-Codes.md](15-Error-Codes.md)).
- A per-row problem in a push is a **rejection** in the result, never an exception for the whole batch — except the
  deliberate cap `G9S-REJ-TOO-MANY`.
- Transports map infrastructure failures to `G9S-NET-FAILURE` (retryable) and server failures to the server's code.

## Async and threading

- Library code uses `ConfigureAwait(false)` on every await.
- Store methods are safe to call concurrently; the SQLite store serialises its own writes with an internal lock.
- `DisposeAsync` is idempotent everywhere (the harness double-disposes on purpose).
- Never block on async (`.Result`, `.Wait()`), never `GC.Collect()` (plan P-16).

## SQL safety

- Values are always parameters. Identifiers only through the quoting helpers (`PgNames.Quote`, `SqliteSql.Quote`) and
  only from the model, never from a request.
- Model SQL templates (`{row}`, `{value}`, `@Param`) are trusted server configuration, validated at server boot.
- The server engine must fail the SERVER on a bad model (boot), never a device sync (plan P-07).

## Packages (ADR-0002)

- Versions live only in `Directory.Packages.props` (central package management). Versions are minimums for consumers;
  keep them as low as security and API needs allow.
- Adding a package to a `src/` project needs a line in [10-Decisions.md](10-Decisions.md).
- Never import a parent `Directory.Build.props` / `Directory.Packages.props`.

## Tests

- Behaviour changes and bug fixes come with a test in `tests/G9SyncData.Tests` (no database) or
  `tests/G9SyncData.IntegrationTests` (real PostgreSQL + SQLite).
- Integration tests only run against a disposable PostgreSQL server; they skip, not fail, when none answers.
- A change is done when `dotnet build G9SyncData.slnx` is clean and both test suites pass.

## Documentation

- Public types and members carry XML docs that say what they guarantee, not what they are called.
- Guides follow the mandatory rule in [00-AiGuide.md](00-AiGuide.md).
- Comments explain why; plan requirement ids (`P-xx`, `D-xx`, `Q-xx`) refer to [`plan/G9SyncData.md`](plan/G9SyncData.md).

## Git and safety

- Never commit, push or open a pull request unless the user asks in that turn.
- Never print or store credentials; read connection strings from configuration or environment at runtime.
