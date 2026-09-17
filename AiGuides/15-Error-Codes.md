# 15 — Error codes

Source: `src/G9SyncData.Abstractions/G9SyncErrorCodes.cs`. Codes are public API: add, never rename or repurpose. Clients
classify failures by code, never by message (plan P-19).

## How codes travel

| Where | Carrier |
|---|---|
| In process | `G9CSyncException.Code`, `.Retryable`, `.Details` |
| In DTOs | `G9DtSyncError { Code, Message, Retryable, Details }` on Hello/plan/push results |
| Through SignalR | `HubException("[G9S|code|0/1] message")`, parsed back by the client transport |
| Per row | `G9DtRowRejection { Code, Message, SqlState, Constraint, Column, Details }` → `G9CSyncRejection` |
| Per conflict | `G9DtRowConflict { Code, ServerChangedMask, PartiallyApplied }` |
| Run result | `G9CSyncRunResult.Error`, `.Problems` (per subscription) |

`G9SyncErrorCodes.IsRetryable(code)`: `NET-FAILURE`, `SRV-ERROR`, `TOKEN-EXPIRED`, `RSY-EPOCH`, `RSY-HORIZON`, `RSY-AUTH`.

## Codes

| Code | Meaning | Typical handling |
|---|---|---|
| `G9S-NET-FAILURE` | connection, timeout, stream broken | engine retries; UI: offline |
| `G9S-SRV-ERROR` | unexpected server failure (details logged server-side) | engine retries; persistent → support |
| `G9S-CANCELED` | run cancelled or engine disposed | none |
| `G9S-AUTH-401` | credentials refused | re-login |
| `G9S-VER-UPGRADE` | a connect gate refused this app version | show upgrade screen |
| `G9S-VER-PROTOCOL` | wire protocol version mismatch | upgrade app or server |
| `G9S-MODEL-INVALID` | model failed validation (server boot or client build) | developer error |
| `G9S-SCH-UPGRADE` | database schema does not match the model | deploy the migration |
| `G9S-SCH-UNKNOWN-TABLE` | a client table the server does not know | listed in Hello `UnknownTables` |
| `G9S-SUB-UNKNOWN` | subscription not defined on the server | subscription marked failed |
| `G9S-SUB-PARAM` | missing or invalid parameters | fix the subscribe call |
| `G9S-SUB-FORBIDDEN` | auth context disallows the subscription | hide the feature |
| `G9S-RSY-EPOCH` | database restored or capture reinstalled | automatic reconcile |
| `G9S-RSY-HORIZON` | offline longer than tombstone retention | automatic reconcile |
| `G9S-RSY-AUTH` | permissions changed | automatic reconcile |
| `G9S-TOKEN-INVALID` | pull token forged or for another plan | re-plan |
| `G9S-TOKEN-EXPIRED` | pull token past its lifetime | re-plan |
| `G9S-REJ-VALIDATION` | a validator rejected the row | show `G9CSyncRejection`, let the user fix |
| `G9S-REJ-CONSTRAINT` | database constraint (SQLSTATE and constraint name attached) | show, fix, row re-uploads on edit |
| `G9S-REJ-PERMISSION` | row outside the client's writable scope | discard or re-scope |
| `G9S-REJ-DIRECTION` | upload to a DownloadOnly table / include | developer error |
| `G9S-CMD-UNKNOWN` | no server handler for the command's name and version | ship the handler before the app version that records the command |
| `G9S-CMD-REJECTED` | a command handler refused without an application code | show the command's message |
| `G9S-REJ-TOO-LARGE` | one row is larger than the server's announced `MaxPushBytes`; rejected on the device, every other row uploads | shrink the row (attachments belong in files) |
| `G9S-REJ-TOO-MANY` | more rejections than `MaxRejectionsPerPush`: whole push failed | systematic bug; investigate |
| `G9S-REJ-MALFORMED` | frame could not be decoded | library bug |
| `G9S-CON-SERVER-WINS` | client change dropped by ServerWins | canonical row applied |
| `G9S-CON-COLUMNS` | ColumnMerge kept some server columns | canonical row applied |
| `G9S-CON-DELETED` | row was deleted on the server | local row removed |
| `G9S-CON-RESOLVER` | a custom resolver rejected the change | show |
| `G9S-LOCAL-STORE` | local database failure or limit (e.g. > 63 persistent subscriptions) | report |
| `G9S-UNKNOWN` | not classified | a bug: classify it |
