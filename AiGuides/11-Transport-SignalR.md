# 11 — SignalR transport

Server: `src/G9SyncData.Server.SignalR`. Client: `src/G9SyncData.Client.SignalR`. Both sit on `G9SignalRSuperNetCore`
2.6.x. The transports only translate calls; every rule lives in the engines.

## Hub protocols (ADR-0039)

The server offers **MessagePack and JSON**; each connection chooses in the SignalR handshake. Clients default to MessagePack
(`G9CSignalRSyncTransportOptions.Protocol`).

| | MessagePack (default) | JSON |
|---|---|---|
| Implementation | `G9SignalRSuperNetCore.{Server,Client}.MessagePack` → Nerdbank.MessagePack, serializers from PolyType shapes generated at build time (`G9SyncWireShapes` in `G9SyncData.Protocol`) | `G9SyncJsonContext` (System.Text.Json source generation) |
| Chunk payloads | raw bytes | base64 text (+33%) |
| 500,000-row initial sync (emulator) | 23.1 MB down, 37 MB allocated | 30.7 MB down, 69 MB allocated |
| Cost | ~100–200 ms more for the first connect of a process (code outside the AOT profile is JIT-compiled); warm syncs are faster (no-op 19 ms vs 25 ms) | readable in traces |
| NativeAOT | yes | yes |

A new hub DTO needs a `[GenerateShapeFor<T>]` line in `G9SyncWireShapes` as well as a `[JsonSerializable]` line in `G9SyncJsonContext`;
`SignalRTransportTests` runs every transport test over both protocols.

## Server

```csharp
builder.Services.AddG9SyncDataServer(...).UsePostgreSql(dataSource).UseSignalR(o =>
{
    o.MaximumReceiveMessageSize = 16 * 1024 * 1024;   // push chunks (base64-expanded by JSON; raw bytes over MessagePack)
    o.PokeCoalescence = TimeSpan.FromSeconds(1);
});
app.UseWebSockets();
app.MapG9SyncHub().RequireAuthorization();            // route G9SyncProtocol.DefaultHubRoute = "/g9sync"
```

`UseSignalR()` registers SignalR, inserts `G9SyncJsonContext` at the head of the JSON protocol's resolver chain (other
hubs are unaffected), adds the MessagePack protocol with `G9SyncWireShapes` (`AddG9MessagePackProtocol`), sets the receive limit for
`G9SyncHub` only and adds the poke service. Apps that use G9SignalRSuperNetCore
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
| `Protocol` | `MessagePack` (`Json` for readable traces) |
| `AccessTokenProvider` | none |
| `ConfigureHttpConnection`, `ConfigureBuilder` | hooks |
| `ConnectTimeout` | 20 s |
| `ServerTimeout` / `KeepAliveInterval` | 30 s / 10 s |
| `CallTimeout` (Hello) | 60 s (ADR-0074) |
| `PlanTimeout` (PlanPull: key sets, snapshot-file plan) | 5 min (1.5.1) |
| `PushTimeout` | 10 min |
| `StreamIdleTimeout` (between chunks of Pull, Reconcile, FetchRows) | 120 s |

Error mapping: `HubException` with an envelope → that code; other `HubException` → `G9S-SRV-ERROR`; HTTP 401/403 →
`G9S-AUTH-401` (not retryable); everything else (socket, timeout, "connection is not active") → `G9S-NET-FAILURE`
(retryable). Caller cancellation is rethrown as is.

A call or stream that runs out of its limit is a retryable `G9S-NET-FAILURE`, and the transport STOPS the connection so the retry
starts on a fresh one (ADR-0074): a live connection with pings flowing is no proof that an answer is coming.

**A host without WebSockets.** An IIS host without the "WebSocket Protocol" feature offers only ServerSentEvents and LongPolling;
MessagePack is binary, so the connection runs over LongPolling: one HTTP request per message. It works, but each message is one poll
answer, and the server drops a connection whose next poll does not come within its disconnect timeout (15 s) - hence the byte bound on
pages (ADR-0073). A proxy in front of LongPolling must pass request bodies through byte for byte: Android labels the binary POST
`application/x-www-form-urlencoded` (LES-0066). Install the WebSocket feature on every IIS that hosts the hub. The Lynx client has
only a WebSocket transport and cannot connect to such a host at all.

Concurrent `ConnectAsync` calls share one attempt; each caller waits for it under its own token, and the attempt observes
its own fault, so one that fails after every waiter was canceled is never an unobserved exception (1.5.10, LES-0082). SignalR stateful reconnect is **not** relied on — resumption is
application-level (batch ids, continuations).

## In-memory transport for tests

`G9CInMemorySyncTransport` (`G9SyncData.Testing`) calls a real `G9CSyncServerEngine` in-process, round-trips every DTO
through the same JSON context, maps server exceptions like the hub, and injects faults ([12](12-Testing.md)).

## AOT notes

- Client: AOT-clean. Client-to-server streams must carry reference types (`G9DtSyncChunk` is a class).
- Server: the hub constructor is `[RequiresDynamicCode]` (typed hub proxies); an AOT-published server sees that warning by design.

## Added in 1.1.0

| Option | Side | Default | Meaning |
|---|---|---|---|
| `StatefulReconnect` | client (`G9CSignalRSyncTransportOptions`) | false | ask for SignalR's stateful reconnect (ADR-0069) |
| `AllowStatefulReconnects`, `StatefulReconnectBufferBytes` | server (`G9CSyncSignalROptions`) | false, 4 MiB | allow it; the buffer is per connection |
| `CloseOnAuthenticationExpiration` | server | false | close a hub connection when its token expires. Turn on when tokens can be revoked |
| `ConfigureConnection` | server | null | anything else on `HttpConnectionDispatcherOptions` |

`AddHealthChecks().AddG9SyncEffects(name, maxPendingAge, tags)` registers `G9CSyncEffectsHealthCheck` (ADR-0065). Do not tag it as
a readiness probe.

## 1.1.1 — on G9SignalRSuperNetCore 2.9.0

A dependency release: the four transport packages move from 2.7.0 to 2.9.0 (`Directory.Packages.props`). Nothing in
the sync protocol or the transports' code changes. What 2.8.0 and 2.9.0 bring to this library:

- **Client (2.8.0):** WebSockets first with a real fallback (`G9EConnectionPhase.TransportFallback` says when it
  happened), per-attempt reconnect jitter so a fleet does not come back together after an outage, and opt-in stateful
  reconnect (off here — it needs WebSockets end to end and costs server memory per connection).
- **Client (2.9.0):** additive only — nullable `StoredFileName` on upload results (null from older servers), an
  optional connection-quality monitor, hub-connection extensions. The MessagePack packages' code is unchanged since 2.7.0.
- **Server (2.9.0):** connection limits per hub type; `G9AttrRequireRole` / `Claim` / `Permission` declared on a hub
  CLASS now apply together with the method's own; an auth throttle on the JWT hub; upload ownership. The sync hub
  declares none of these attributes and uses no upload or JWT hub, so its behaviour is unchanged.

Why now: AgriPad pinned the client at 2.9.0 while this family still brought 2.7.0 transitively, so the app ran a
mixed Client / Client.MessagePack pair and the mobile server (built from this source) stayed on 2.7.0.
