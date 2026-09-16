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

## Authorisation

- The endpoint authenticates (`MapG9SyncHub().RequireAuthorization()`).
- `IG9SyncAuthContextProvider.ResolveAsync(caller)` turns the caller into a `G9CSyncAuthContext`: user id,
  `ServerParameters` (values for `ServerParameter` declarations) and an optional `SubscriptionFilter`. Called on every
  operation — cache inside if expensive.
- **Auth hash**: a hash of the server parameter values is stored with each client cursor. When a user's permissions
  change, the hash changes and the client reconciles — no app-level permission stamps.
- Pushes are authorised against the client's subscription scopes before and after the change, plus `WriteRule`.
- `IG9SyncConnectGate` runs at Hello first; return an error such as `G9S-VER-UPGRADE` to refuse old apps.

## Upload pipeline hooks (scoped services)

| Hook | Runs | Can |
|---|---|---|
| `IG9UploadValidator` | after staging, before apply | reject rows with a code and message |
| `IG9UploadTransformer` | after validation | rewrite staged values (the changed row returns to the device as canonical) |
| `IG9DeleteHandler` | for delete rows | turn a delete into a soft delete or reject it |
| `IG9ConflictResolver` | for `Custom` policy conflicts | pick or merge |
| `IG9PostCommitHandler` | after the push transaction committed | side effects (never rolls back the push) |

## Server options (`G9CSyncServerOptions`)

| Option | Default | Notes |
|---|---|---|
| `TokenSigningKey` | random per process | **set it** (≥ 32 bytes) with more than one instance, or pulls cannot resume elsewhere |
| `TokenLifetime` | 2 h | pull token validity |
| `PageRows` | 2000 | rows per pull chunk |
| `MaxPushRows` / `MaxPushBytes` | 50 000 / 128 MiB | per push |
| `MaxRejectionsPerPush` | 50 | above it the push fails as a whole (`G9S-REJ-TOO-MANY`) |
| `MaxFetchKeys` | 10 000 | per reconcile fetch |
| `TombstoneRetention` | 30 days | clients offline longer reconcile |
| `CleanupInterval` | 6 h | tombstone purge timer |
| `EnableChangeNotifications` | true | LISTEN for committed changes (drives pokes) |
| `InitializeOnStartup` | true | install/verify capture at host start; a failure stops the host |
