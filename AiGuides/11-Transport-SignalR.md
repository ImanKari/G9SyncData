# 11 — SignalR transport

Server: `src/G9SyncData.Server.SignalR`. Client: `src/G9SyncData.Client.SignalR`. Both sit on `G9SignalRSuperNetCore`
2.5.x. The transports only translate calls; every rule lives in the engines.

## Server

```csharp
builder.Services.AddG9SyncDataServer(...).UsePostgreSql(dataSource).UseSignalR(o =>
{
    o.MaximumReceiveMessageSize = 16 * 1024 * 1024;   // push chunks, base64-expanded by JSON
    o.PokeCoalescence = TimeSpan.FromSeconds(1);
});
app.UseWebSockets();
app.MapG9SyncHub().RequireAuthorization();            // route G9SyncProtocol.DefaultHubRoute = "/g9sync"
```

`UseSignalR()` registers SignalR, inserts `G9SyncJsonContext` at the head of the JSON protocol's resolver chain (other
hubs are unaffected), sets the receive limit for `G9SyncHub` only and adds the poke service. Apps that use G9SignalRSuperNetCore
policy attributes call `AddSignalRSuperNetCoreCore()` as usual.

### Hub methods (`G9SyncProtocol.Methods`)

| Method | Shape |
|---|---|
| `Hello(G9DtHelloRequest)` | → `G9DtHelloResult`; joins the connection to one group per negotiated table |
| `Push(G9DtPushRequest, IAsyncEnumerable<G9DtSyncChunk>)` | client stream in → `G9DtPushResult` |
| `PlanPull(G9DtPullRequest)` | → `G9DtPullPlan` |
| `Pull(string token, G9DtPullContinuation?)` | server stream of `G9DtSyncChunk` |
| `Reconcile(G9DtReconcileRequest)` | server stream (key listings, then a final summary chunk) |
| `FetchRows(G9DtFetchRowsRequest)` | server stream of row chunks |

Listener: `OnChangesAvailable(G9DtChangesAvailable)`.

### Authentication

The hub trusts the endpoint. For bearer tokens over WebSockets, read `access_token` from the query for the hub path:

```csharp
options.Events = new JwtBearerEvents
{
    OnMessageReceived = context =>
    {
        if (context.HttpContext.Request.Path.StartsWithSegments("/g9sync") &&
            context.Request.Query.TryGetValue("access_token", out var token))
            context.Token = token;
        return Task.CompletedTask;
    }
};
```

The engine receives `Context.User` in `G9CSyncCaller` and resolves the auth context from it ([05](05-Server-Model.md)).

### Errors

A `G9CSyncException` becomes `HubException("[G9S|{code}|{0|1}] {message}")` (`G9SyncHubErrors.Format`); any other
exception is logged and sent as `G9S-SRV-ERROR` (retryable) without internals. Streams are guarded the same way, and
capture everything they need before the hub instance is released.

### Pokes

`G9CSyncPokeService` subscribes to `G9CSyncChangeNotifier.TablesChanged` (fed by PostgreSQL `LISTEN`), coalesces per
table for `PokeCoalescence`, and sends `OnChangesAvailable([table])` to group `G9SyncProtocol.TableGroup(table)`. Each
server instance listens to the database itself, so no backplane is needed.

## Client

```csharp
services.AddG9SyncDataClient(model)
    .UseSignalR((sp, o) =>
    {
        o.Url = "https://sync.example.com/g9sync";
        o.AccessTokenProvider = () => sp.GetRequiredService<ITokenSource>().GetAccessTokenAsync();
        o.ConfigureHttpConnection = http => { /* TLS validation, headers, transports */ };
    });
```

`G9CSignalRSyncTransport` derives from `G9SignalRSuperNetCoreClient<,,>`: the G9 jittered reconnect policy and
`StateChanged` lifecycle come with it. Calls use a hand-written proxy (`Server` property, ADR-0015).

| Option | Default |
|---|---|
| `Url` | required |
| `AccessTokenProvider` | none |
| `ConfigureHttpConnection`, `ConfigureBuilder` | hooks |
| `ConnectTimeout` | 20 s |
| `ServerTimeout` / `KeepAliveInterval` | 30 s / 10 s |

Error mapping: `HubException` with an envelope → that code; other `HubException` → `G9S-SRV-ERROR`; HTTP 401/403 →
`G9S-AUTH-401` (not retryable); everything else (socket, timeout, "connection is not active") → `G9S-NET-FAILURE`
(retryable). Caller cancellation is rethrown as is.

Concurrent `ConnectAsync` calls share one attempt. SignalR stateful reconnect is **not** relied on — resumption is
application-level (batch ids, continuations).

## In-memory transport for tests

`G9CInMemorySyncTransport` (`G9SyncData.Testing`) calls a real `G9CSyncServerEngine` in-process, round-trips every DTO
through the same JSON context, maps server exceptions like the hub, and injects faults ([12](12-Testing.md)).

## AOT notes

- Client: AOT-clean. Client-to-server streams must carry reference types (`G9DtSyncChunk` is a class).
- Server: the hub constructor is `[RequiresDynamicCode]` (typed hub proxies); an AOT-published server sees that warning by design.
