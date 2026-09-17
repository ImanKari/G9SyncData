# 05 — Server model, auth and upload pipeline

The server model is declared once in code, validated at `Build()` and again against the live database at boot. A bad
model stops the server; it never reaches a device (P-07). Source: `src/G9SyncData.Server/Model`.

## Registration

```csharp
services.AddG9SyncDataServer(model => { /* tables, subscriptions */ }, options => { /* G9CSyncServerOptions */ })
    .UsePostgreSql(dataSource)          // the store
    .UseSignalR()                       // the transport (G9SyncData.Server.SignalR)
    .UseAuthContextProvider<MyAuth>()   // optional; default reads the user id from claims
    .AddValidator<MyValidator>()        // optional pipeline hooks (scoped)
    .AddConnectGate<MinimumVersionGate>();
```

`AddG9SyncDataServer` registers the model, the engine, the change notifier and a hosted service that initialises the
store, listens for committed changes and purges old tombstones.

## Tables

```csharp
model.Table("Operation", "Pot", t => t
    .Key("Id")                                                  // single column: Guid (default), String, Int32, Int64
    .Column("Code", G9ESyncColumnType.String, nullable: false)
    .Column("Location", G9ESyncColumnType.String, configure: c => c
        .ReadAs("ST_AsText({row}.\"Location\")")                // read expression
        .WriteAs("ST_GeomFromText({value})"))                   // write expression
    .Column("CreatedBy", G9ESyncColumnType.String, configure: c => c.ReadOnly())
    .Computed("CanEdit", G9ESyncColumnType.Boolean, "{row}.\"OwnerId\" = @UserId")   // folded into reconcile hashes
    .Direction(G9ESyncDirection.Bidirectional)                  // DownloadOnly: no client capture at all
    .Conflicts(G9EConflictPolicy.ColumnMerge)
    .OrderedApply(true)                                         // force device-order row-by-row apply
    .WriteRule("{row}.\"IsLocked\" = false"));                  // must hold before and after an uploaded change
```

- **Register parents before children**: uploads apply tables in registration order.
- **Directions.** `Bidirectional` (default); `DownloadOnly` — no device capture, uploads refused; `UploadOnly` — the device writes, the
  server never sends: no download, reconciliation, canonical rows or pokes, and `FetchRows` is refused. Uploaded rows stay on the device
  until the app calls `engine.ForgetUploadedRowsAsync(table, keys?)` (ADR-0037).
- At most 62 physical columns (the change mask is 64 bits, one bit reserved).
- Logical name = `schema.table`; the client refers to tables by it.

### Conflict policies

| Policy | When the server changed the row after the client's base version |
|---|---|
| `ServerWins` (default) | client change dropped, conflict reported, canonical row returned |
| `ClientWins` | client's changed columns applied |
| `ColumnMerge` | client's changed columns the server did **not** change are applied; overlapping ones keep the server value. Tracks per-column versions |
| `LastWriterWins` | newer of client edit time (server-clock corrected) and server change time |
| `Custom` | a registered `IG9ConflictResolver` decides |

## Subscriptions

```csharp
model.Subscription("site-pots", s => s
    .Parameter("SiteId", G9ESyncColumnType.Guid)                   // client-supplied, @SiteId in SQL
    .ServerParameter("AllowedUnits", G9ESyncColumnType.Guid, isList: true)   // from the auth context, never from the client
    .Include("Operation.Pot",
        scopeSql: "{row}.\"SiteId\" = @SiteId AND {row}.\"UnitId\" = ANY(@AllowedUnits)",
        whenSql: "NOT {row}.\"IsDeleted\"",
        allowUpload: true)
    .DependsOn("sites")                                            // parameterless, synced first
    .MoveOut(G9EMoveOutPolicy.Delete)
    .ReconcileEvery(TimeSpan.FromHours(24)));

model.Subscription("sampling-detail", s => s
    .Parameter("SamplingId", G9ESyncColumnType.Guid)
    .Include("Operation.SamplingItem", "{row}.\"SamplingId\" = @SamplingId")
    .Ephemeral(TimeSpan.FromDays(7))                                // per-item, garbage-collected on the device
    .Heavy());                                                      // never started by background runs
```

- **Scope SQL** says which rows belong; **When SQL** filters by row state. A changed row inside the scope that fails
  *When* is delivered as a **move-out**, so the device drops it. A row that leaves the *scope* (e.g. moved to another
  site) is invisible to a delta and is repaired by reconciliation — keep volatile predicates in *When*.
- Parameter types: Boolean, Int32, Int64, String, Guid; lists with `isList: true`. Names starting with `g9` are reserved.
- Declared `@Name` tokens are renamed per subscription when bound, so two subscriptions with the same parameter can
  share one statement; undeclared `@x` fails boot validation.
- **Definition hash**: any change to parameters, includes, SQL, computed columns or move-out policy changes the hash;
  clients holding the old hash reconcile automatically.

### Visibility as a query (scope providers, ADR-0029)

An include can take its membership from an `IG9SyncScopeProvider` instead of a SQL string. The provider returns, per request,
a SELECT of the table's keys; the store embeds it in its own statements and never runs it separately, so membership is read in
the same snapshot as the rows it gates. `G9SyncData.Server.EfCore` provides one over the application's `DbContext`:

```csharp
model.Subscription("farm-pots", s => s
    .Parameter("FarmId", G9ESyncColumnType.Guid)
    .ServerParameter("PermissionStamp", G9ESyncColumnType.String)          // see the rule below
    .IncludeQuery<AgricultureDbContext, Pot>("Operation.Pot", (db, scope) =>
    {
        var farm = scope.Get<Guid>("FarmId");
        var user = scope.Auth.UserId;
        return db.Pots.Where(p => p.Ridge.Block.Field.FarmId == farm
                                  && db.UserFarms.Any(u => u.FarmId == farm && u.UserId == user));
    },
    whenSql: "NOT {row}.\"IsDeleted\"",
    version: "1"));                                                          // bump when the query's MEANING changes
```

- **The application's EF filters apply** — global and named query filters, converters, navigations. Opt out per query with
  `IgnoreQueryFilters()` / `IgnoreQueryFilters(["name"])` when a filter depends on an HTTP request the sync call does not have
  (Agriwise's state-machine filter reads the current user from `IHttpContextAccessor`).
- **The DbContext** comes from `IDbContextFactory<TContext>` when registered, otherwise from a fresh DI scope. It is only used to
  translate; `CreateDbCommand()` never opens a connection.
- ⚠ **Permissions read only inside the query tell no device to look again.** Surface them as a server parameter (a permission
  stamp is enough): a changed value changes the auth hash and every affected device reconciles, dropping exactly what it lost.
  Pinned by `Revoking_a_membership_removes_exactly_the_revoked_rows_once_the_permission_stamp_changes`.
- **Row state stays in `whenSql`.** A changed row failing *when* is sent as a delete by the next delta; a row leaving the query
  is only noticed by reconciliation.
- **Uploads are authorised by the same query**, before and after the change, inside the push transaction.
- **Startup probe** (`G9CPostgresSyncOptions.ValidateScopeProviders`, on by default): each provider is built with typed sample
  values and again with every value null, and both are EXPLAINed. A query over the wrong entity, an untranslatable query, a
  missing table or a key-type mismatch stops the server with `G9S-MODEL-INVALID`. A query must therefore tolerate null
  parameters — which it must anyway, because a missing server parameter binds null at run time.
- **Version**: a delegate cannot be compared across deployments, so the fingerprint carries `version`; bump it when the query's
  meaning changes and devices reconcile once.
- A consumer without EF implements `IG9SyncScopeProvider` directly with SQL; parameters are renamed per statement, so any name is safe.

## Authorisation

- The endpoint authenticates (`MapG9SyncHub().RequireAuthorization()`).
- `IG9SyncAuthContextProvider.ResolveAsync(caller)` turns the caller into a `G9CSyncAuthContext`: user id,
  `ServerParameters` (values for `ServerParameter` declarations) and an optional `SubscriptionFilter`. Called on every
  operation — cache inside if expensive.
- **Auth hash**: a hash of the server parameter values is stored with each client cursor. When a user's permissions
  change, the hash changes and the client reconciles — no app-level permission stamps.
- Pushes are authorised against the client's subscription scopes before and after the change, plus `WriteRule` — scope
  providers included.
- `IG9SyncConnectGate` runs at Hello first; return an error such as `G9S-VER-UPGRADE` to refuse old apps.

## Conflict rules (ADR-0031)

```csharp
.Table("Operation", "Pot", t => t
    .Key("Id") /* … */
    .Conflicts(G9EConflictPolicy.ServerWins)                                   // for conflicts no rule matches
    .OnConflict(rules => rules
        .WhenAsync("site-closed", async (c, ct) => await IsClosedAsync(c.Connection, c.Client("SiteId"), ct))
            .Reject("APP-SITE-CLOSED", "The site was closed while you were offline.")
        .When("gone", c => c.ServerDeleted && !c.ClientDeletes).Reject("APP-GONE")
        .When("status-only-newer", c => c.ChangedOnly("Status") && c.ClientIsNewer).TakeClient()
        .When("device-owns-note", c => c.Overlapping.Contains("Note")).ApplyColumns("Note")
        .When("disjoint", c => c.Disjoint).MergeColumns()))
```

- Rules run only for CONFLICTS: uploads of rows the server changed since the device's base version. First match wins.
- Facts: `ClientChanged`, `ServerChanged` (null when unknown), `Overlapping`, `Disjoint`, `ChangedOnly(...)`, `ServerDeleted`,
  `ClientDeletes`, `ClientEditedUtc`/`ServerChangedUtc`/`ClientIsNewer`, `Client(col)`/`Server(col)`, `Auth`, `Connection`/`Transaction`,
  `Services`.
- Actions: `TakeServer`, `TakeClient`, `MergeColumns`, `ApplyColumns(...)`, `Reject(code, message)`, `Decide((c, ct) => outcome)`.
- A table with rules tracks per-column versions. Rules cannot be combined with `G9EConflictPolicy.Custom`.

## Tables from an EF Core model (`G9SyncData.Server.EfCore`)

```csharp
// Deferred: the model is built at host start from the application's OWN context registration (never construct a context
// by hand for this - EF caches one model per context type, so a differently configured context would replace the app's model).
builder.Services.AddG9SyncDataServer((services, model) => model.FromEfCore<AgricultureDbContext>(services, ef =>
{
    ef.DefaultDirection = G9ESyncDirection.Bidirectional;
    ef.Entity<AttributeDefinition>(e => e.Direction(G9ESyncDirection.DownloadOnly));
    ef.Entity<EntityAttributeValue>(e => e
        .Exclude("Buid", "SerialNumber")                       // CLR member path or column name
        .Property("AttachmentCount", p => p.ReadOnly()));      // server-owned JSON document
}).Subscription(/* … */));
```

Everything the EF model knows is READ, not restated: schema, table, key, column names, provider types (value converters win over
CLR types), nullability, store-generated columns (read-only), spatial columns (text on the wire, `::geometry` on write) and indexes.
Problems are collected and reported together at server start (`G9S-MODEL-INVALID`).

**Owned types and complex properties** (LES-0034) — their columns live in the owner's row too:

| Mapping | Sync columns |
|---|---|
| `OwnsOne(...).ToJson()` / `OwnsMany(...).ToJson()` / `ComplexProperty(...).ToJson()` | ONE `String` column named after the container column, read as `{row}."Col"::text`, written as `{value}::jsonb` (the column's store type) |
| owned type or complex property split into the owner's table | its scalar columns (`Audit_By`, `Dimensions_Width`), recursively |
| owned type in a table of its own | none: it is a different table |

Nullability comes from the relational table, so an optional owned type's columns are nullable even for non-nullable properties.
`Exclude`/`Property` accept the member path (`Counts`, `Dimensions.Width`) or the column name.

## Writes through the application (ADR-0025)

**Row writers.** Uploaded rows of a table are written by application code instead of the store's SQL; everything around the write
(authorisation, validators, transformers, conflict policy, capture, bisection, results) is unchanged:

```csharp
.WriteThroughEfCore<AgricultureDbContext>(ef => ef
    .Entity<Placement>()
    .Entity<Pot>(pot => pot.Convert("Location", wkbHex => WkbToPoint((string?)wkbHex))))   // columns the store writes with SQL casts
```

**Owned and complex columns through EF.** A JSON document column is written to its owned navigation or complex property only with a
converter — `Convert("Counts", json => JsonSerializer.Deserialize<TallyCount>((string)json!, options))` — or is declared
read-only when the server owns it (EF then keeps the entity's own value, e.g. a new owned instance's defaults on insert). Scalar
columns of a complex property split into the table are written directly; columns of an owned type split into the table cannot be
written through EF (exclude them or make them read-only). Each case is checked at server start.

`SaveChangesAsync` runs on a context bound to the push transaction, so the application's SaveChanges logic and interceptors apply to
device writes. Throw `G9CRowRejectedException(code, message)` from that logic to reject one row with your own code; the store isolates
it by bisection. Written rows return to the device as canonical rows. A context with `EnableRetryOnFailure` cannot join the push
transaction — register one without it for G9SyncData. Custom writers implement `IG9RowWriter` (`AddRowWriter<T>()`).

**Commands.** A named intent recorded on the device and executed by the server's own code:

```csharp
.AddCommand("replant", AppJson.Default.Replant, async (context, cmd, ct) =>
{
    await using var bound = await context.OpenDbContextAsync<AgricultureDbContext>(ct);
    await placements.ReplantAsync(bound.Db, cmd.PotId, cmd.TreeId, ct);   // existing service, existing rules, the right order
    await bound.Db.SaveChangesAsync(ct);
    return G9CCommandResult.Ok();                                          // or Rejected("APP-CODE", "…") / Superseded()
}, minVersion: 1, maxVersion: 1)
```

Guarantees: once per command id (a stored outcome is returned for a retry or a resend), device order, before the pushed rows, each
command isolated by a savepoint (a refusal keeps nothing it wrote), canonical rows for everything the command touched or changed.
Handlers must be deterministic with respect to the data they read. Unknown name/version → `G9S-CMD-UNKNOWN`.

## Digest cache (ADR-0024)

```csharp
builder.Services.AddG9SyncDataServer(model, options)
    .UsePostgreSql(dataSource)
    .UseDigestCache(cache =>
    {
        cache.MaxBytes = 256L * 1024 * 1024;       // LRU ceiling
        cache.MaxAge = TimeSpan.FromHours(6);      // idle entries dropped
        cache.MaxIncrementalKeys = 20_000;         // beyond this many changed keys, derive instead
    });
```

Reconciliations of a view (a subscription include for one set of parameter and auth values) are served from memory once the
store has proven the cached entry exact at the reconciliation's snapshot; otherwise they are derived exactly as without the cache.
Enabling, disabling, restarting or evicting never changes a result, only its cost. A view is cacheable when every table its scope
reads is synced (tracked) — so sync the parent tables a scope joins through — and it has no computed columns in its hash. A
scope that reads other tables inside a **function** must be listed in `G9CPostgresSyncOptions.DigestCacheExcludedSubscriptions`.

## Upload pipeline hooks (scoped services)

| Hook | Runs | Can |
|---|---|---|
| `IG9UploadValidator` | after staging, before apply | reject rows with a code and message |
| `IG9UploadTransformer` | after validation | rewrite staged values (the changed row returns to the device as canonical) |
| `IG9DeleteHandler` | for delete rows | turn a delete into a soft delete or reject it |
| `IG9ConflictResolver` | for `Custom` policy conflicts | pick or merge |
| `IG9PostCommitHandler` | in-process, right after the push transaction committed | best-effort notifications: metrics, caches, UI hints. A throw is logged, a replayed batch skips it, a crash loses it |
| `IG9EffectHandler` | recorded IN the push transaction, delivered by a worker with retry | work that must not be lost: workflow transitions, projections, external calls. At-least-once, so make it idempotent on `EffectId` (ADR-0052) |

## Server options (`G9CSyncServerOptions`)

| Option | Default | Notes |
|---|---|---|
| `TokenSigningKey` | random per process | **set it** (≥ 32 bytes) with more than one instance, or pulls cannot resume elsewhere |
| `TokenLifetime` | 2 h | pull token validity |
| `PageRows` | 2000 | rows per pull chunk |
| `MaxPushRows` / `MaxPushBytes` | 50 000 / 128 MiB | per push; announced in Hello, devices batch to them and reject a single row larger than the byte limit locally (`G9S-REJ-TOO-LARGE`, ADR-0037) |
| `MaxRejectionsPerPush` | 50 | above it the push fails as a whole (`G9S-REJ-TOO-MANY`) |
| `MaxFetchKeys` | 10 000 | per reconcile fetch |
| `TombstoneRetention` | 30 days | clients offline longer reconcile |
| `CleanupInterval` | 6 h | tombstone purge timer |
| `EnableChangeNotifications` | true | LISTEN for committed changes (drives pokes) |
| `InitializeOnStartup` | true | install/verify capture at host start; a failure stops the host |
