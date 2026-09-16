# 04 — Protocol

Source: `src/G9SyncData.Protocol`. Protocol version `G9SyncProtocol.Version = 1`; a Hello from another version is
refused with `G9S-VER-PROTOCOL`. Control messages are JSON DTOs (source-generated `G9SyncJsonContext`, camelCase, nulls
omitted); row data travels as binary **G9RowBatch** frames inside `G9DtSyncChunk.Payload`.

> Changing a DTO shape, a hub name or a frame byte is a protocol change: keep it backward compatible, or bump the version
> with a reader that accepts both, and record an ADR.

## Operations

| Operation | Request → response | Notes |
|---|---|---|
| **Hello** | `G9DtHelloRequest { ClientId, AppVersion, ProtocolVersion, Tables (client model), Subscriptions (name, parameters, cursor, hashes) }` → `G9DtHelloResult { Epoch, ServerTimeUtc, Tables (negotiated), UnknownTables, Definitions, Subscriptions (decision per key), Error }` | column negotiation: server model order, filtered to columns the client has with the same type |
| **Push** | `G9DtPushRequest { ClientId, BatchId, Subscriptions, Tables (column order per table) }` + stream of chunks with `Push` sections → `G9DtPushResult { BatchId, AlreadyApplied, Accepted, Rejections, Conflicts, CanonicalFrames, Error }` | idempotent by `BatchId` |
| **PlanPull** | `G9DtPullRequest { ClientId, Subscriptions, Tables, Backfills }` → `G9DtPullPlan { Token, Parts (key, table, kind, rows, bytes), TotalRows, EstimatedBytes, Cursors, Skipped, Error }` | exact row counts before any data moves |
| **Pull** | `(token, continuation?)` → stream of `G9DtSyncChunk` | resumable anywhere with the same token |
| **Reconcile** | `G9DtReconcileRequest { ClientId, Subscription, Tables (256 digests + counts each) }` → stream: `KeyVersions` chunks, then a final chunk with `G9DtReconcileSummary { Cursor, DefinitionHash, AuthHash, Mismatched }` | |
| **FetchRows** | `G9DtFetchRowsRequest { ClientId, Subscription, TableId, Keys, Columns }` → stream of `Upsert` chunks, final chunk | rows filtered by the subscription's scope and When |
| **OnChangesAvailable** (server → client) | `G9DtChangesAvailable { Tables }` | a hint only |

### Subscription decisions (`G9ESubscriptionAction`)

`Ok` (delta) · `Initial` (no cursor: snapshot) · `Reconcile` (definition or auth hash changed) · `ResyncRequired` (epoch
changed or tombstone horizon passed) · `Unknown` · `InvalidParameters` · `Forbidden`.

### Chunks

`G9DtSyncChunk { Sequence, Payload, RowCount, SubscriptionKey, IsFinal, Continuation, ReconcileSummary }`. A chunk that
carries `Continuation { PartIndex, AfterKey, NextSequence }` is a **safe resume point**: everything up to it is complete.
Pages with no rows are never sent; the last chunk of a stream has `IsFinal = true`.

### Push idempotency

The server stores the last applied `BatchId` and its result per client in the same transaction as the apply. A request
with `BatchId <= last` is not applied again: for `== last` the stored result is replayed with `AlreadyApplied = true`.
The client reuses a batch id until the batch is acknowledged ([08](08-Client-Sqlite.md)).

## Cursor

`v1:{epoch:N}:{pg_snapshot text}` (`G9CSyncCursor`). Opaque to clients. The epoch is the capture installation's id
(`g9sync.install.epoch`); a different epoch means the database was restored or reinstalled → `ResyncRequired`.

## Pull token

`base64url(json payload) "." base64url(HMAC-SHA256(key, json))`. The payload names every planned part and expires after
`TokenLifetime` (`G9S-TOKEN-EXPIRED`); a bad signature is `G9S-TOKEN-INVALID`. Any instance with the same
`TokenSigningKey` can serve it — no session store (P-13).

## G9RowBatch frame

```
header (10 bytes):  'G' '9' 'R' 'B' | version u8 (=1) | compression u8 (0 none, 1 Brotli) | body length i32 LE
body:               section count i32 LE | section*
section:            op u8 | table id varint | key type u8 | column count varint | column types u8[count] | row count i32 LE | row*
```

- The writer compresses with Brotli (quality 4, window 22) when the body exceeds 128 bytes and compression helps;
  otherwise the body is stored raw. Body limit 512 MiB.
- Varints are LEB128 (7 bits per byte); signed values are ZigZag-encoded first.
- Column types are `G9ESyncColumnType` values; key-only sections carry zero columns.

### Rows per op (`G9ERowBatchOp`)

| Op | Row layout | Writer call |
|---|---|---|
| `Upsert` (1) | key · version varint · null bitmap · values | `BeginRow` + `Write*` + `EndRow` |
| `Tombstone` (2) | key · version varint | `AddKey` |
| `MoveOut` (3) | key · version varint | `AddKey` |
| `KeyVersions` (4) | key · version varint · row hash i64 LE | `AddKeyVersion` |
| `Push` (5) | key · push op u8 (1 upsert, 2 delete) · local sequence varint · base version varint · dirty mask i64 LE (bits in the request's column order) · HLC ms zigzag varint · for upserts: null bitmap · values | `BeginPushRow` + `Write*` + `EndRow` |
| `Backfill` (6) | like `Upsert`, but only the backfilled columns (named by the pull request) | `BeginRow` |

The writer enforces the row kind per section and that every declared column is written exactly once in order.

### Keys

| Key type | Encoding |
|---|---|
| Guid | 16 bytes, big-endian (RFC 4122 byte order, i.e. the order of the text form) |
| Int32 / Int64 | zigzag varint |
| String | varint byte length · UTF-8 |

Readers expose keys in canonical text ([ADR-0006](10-Decisions.md)).

### Values

A row with values starts with a **null bitmap** of `ceil(columns / 8)` bytes; bit `i` (LSB first within each byte) set
means column `i` is NULL and has no value bytes.

| Type | Encoding |
|---|---|
| Boolean | 1 byte (0/1) |
| Int32, Int64 | zigzag varint |
| Double | 8 bytes IEEE-754 LE |
| Decimal | 4 × i32 LE (`decimal.GetBits`: low, mid, high, flags — scale preserved) |
| String | varint byte length · UTF-8 |
| Guid | 16 bytes big-endian |
| DateTime | ticks i64 LE · `DateTimeKind` u8 |
| DateTimeOffset | UTC ticks i64 LE · offset minutes i16 LE |
| TimeSpan | ticks zigzag varint |
| Bytes | varint length · bytes |

## Reconciliation hash

Defined once in `G9CSyncDigest` and reproduced in SQL (ADR-0012):

- bucket = first byte of `md5(canonical key)` → `get_byte(decode(md5(key), 'hex'), 0)`
- row hash = first 8 bytes, big-endian signed, of `md5(key ":" version [":" computed text …])` →
  `('x' || left(md5(...), 16))::bit(64)::bigint`
- bucket digest = XOR of row hashes; counts compared too.

Computed values use `G9CSyncDigest.CanonicalText` (booleans `1`/`0`, lower-case GUIDs, invariant numbers). Only Boolean,
Int32, Int64, String and Guid computed columns are supported in hashes.
