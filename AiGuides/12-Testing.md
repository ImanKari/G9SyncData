# 12 — Testing

## Suites

| Project | Needs | Covers |
|---|---|---|
| `tests/G9SyncData.Tests` | nothing | G9RowBatch codec round trips; C#↔SQL digest parity (golden values); parameters and subscription keys; JSON contexts; SQLite store capture, push batching, acknowledgement, apply protection; generated model and indexes |
| `tests/G9SyncData.IntegrationTests` | disposable PostgreSQL 13+ | end-to-end sync between real PostgreSQL and real SQLite, over the in-memory transport and over real SignalR |

```powershell
dotnet test tests/G9SyncData.Tests
dotnet test tests/G9SyncData.IntegrationTests
```

## PostgreSQL for integration tests

- Connection: `G9SYNC_TEST_PG` (a superuser-capable connection string to the `postgres` database), default
  `Host=localhost;Port=15433;Username=postgres;Database=postgres`.
- Each test creates `g9t_<random>` from `template0` and drops it `WITH (FORCE)` — also when the harness fails to start.
  **Never** point this at a shared server.
- No server answering → tests are **skipped** (`[PostgresFact]`), not failed.
- Leaked databases starve the server of connections and make unrelated tests time out (see the log). Check with
  `SELECT datname FROM pg_database WHERE datname LIKE 'g9t_%'`.

Creating a local disposable server (Windows, PostgreSQL binaries at `D:\Apps\pgsql\bin`):

```powershell
& D:\Apps\pgsql\bin\initdb.exe -D $env:LOCALAPPDATA\g9sync-pg -U postgres -A trust
Start-Process D:\Apps\pgsql\bin\pg_ctl.exe -ArgumentList "-D $env:LOCALAPPDATA\g9sync-pg -o `"-p 15433`" -l $env:LOCALAPPDATA\g9sync-pg.log start" -WindowStyle Hidden
& D:\Apps\pgsql\bin\pg_isready.exe -h localhost -p 15433
```

Two traps: Windows reserves port ranges (check `netsh interface ipv4 show excludedportrange protocol=tcp`; 55433 was inside
one), and `pg_ctl -w start` from a tool that waits for the child's output pipe hangs even though the server started —
launch it detached with a log file.

## Harness (`tests/G9SyncData.IntegrationTests/Infrastructure`)

`SyncHarness.CreateAsync(output, configure, transport, model, server, seedBeforeCapture)`:

| Parameter | Use |
|---|---|
| `configure` | server options (e.g. `MaxRejectionsPerPush`) |
| `transport` | `InMemory` (plain provider, no hosted services) or `SignalR` (Kestrel on `127.0.0.1:0`, listener and pokes running) |
| `model` | server model variant — `TestSchema.ConfigureServer(m, new ServerModelOptions(PotConflicts, IncludePotNote, SiteDirection))` |
| `server` | extra registrations: `UseAuthContextProvider<MutableAuthProvider>()`, `AddValidator<…>()`, `AddConnectGate<…>()`, singletons |
| `seedBeforeCapture` | rows written before capture is installed (no tracking history — how existing production data looks) |

- `TestSchema`: a farm domain with a partial unique index, a row-state filter, a parameterised dependent subscription, a
  permission-scoped subscription (`allowed-pots`, server parameter `AllowedSites`), decimals, timestamps and ColumnMerge.
- `harness.CreateClientAsync(configure, caller, prepareDatabase)` — a device; `prepareDatabase` writes the SQLite file
  first (data from an earlier sync system). `harness.CreateSubscribedClientAsync(site)` — subscribed and synced.
- `harness.CreateAdditionalServerAsync(model)` — a second engine on the same database (the next deployment); assign it to
  `client.InMemory.Engine` to simulate a server upgrade between runs.
- `client.ExecAsync(sql)` writes like the app (its own connection, so capture triggers fire); `client.SyncOkAsync()`
  asserts success and prints error code, problems and counts otherwise.
- `TestHooks.cs`: `MutableAuthProvider`, `HookRecorder`, `NegativeStatusValidator`, `UpperCaseCodeTransformer`,
  recording delete/post-commit handlers, `NoteDrivenConflictResolver`, `MinimumVersionGate`, `RecordingProgress`, `Wait`.

## Fault injection (`G9CTransportFaults`)

| Call | Simulates |
|---|---|
| `FailBefore(op, times)` | the call never reached the server |
| `FailAfter(op, times)` | the server completed the call, the response was lost (push applied, ack lost) |
| `BreakStreamAfter(op, chunks, times)` | a pull/reconcile/fetch stream dies mid-way |
| `Latency` | delay per call and per chunk (keeps a run busy for queue tests) |

## What the integration tests prove

| Class / test | Requirement |
|---|---|
| **EndToEndSyncTests** | |
| `Initial_pull_then_delta_applies_updates_deletes_inserts_and_move_outs` | snapshot, delta, tombstones, When-filter move-outs, decimal/timestamp fidelity, delta size |
| `Local_inserts_updates_and_deletes_upload_and_stop_being_pending` | capture → push → server state |
| `A_transaction_still_open_during_a_pull_is_delivered_by_the_next_pull` | P-01 / ADR-0005 snapshot exactness |
| `A_unique_violation_rejects_only_the_offending_row_with_its_real_sqlstate` | ADR-0017 bisection, rejection lifecycle |
| `Column_merge_keeps_a_server_edit_and_applies_a_device_edit_to_another_column` | ColumnMerge |
| `A_broken_download_resumes_from_the_last_staged_page` | resumable pull |
| `A_lost_push_acknowledgement_is_retried_without_duplicates_or_rejections` | push idempotency |
| `A_client_behind_the_tombstone_horizon_reconciles_and_removes_deleted_rows` | horizon → reconciliation |
| `An_unrelated_long_transaction_does_not_keep_clients_beyond_the_tombstone_horizon` | exact horizon check under a pinned xmin (2026-09-16 bug) |
| `Unsubscribing_deletes_only_rows_no_other_subscription_holds` | membership, move-out policy |
| `A_change_uploaded_by_one_device_reaches_another_device` | multi-device |
| **ServerPipelineTests** | |
| `An_app_validator_rejects_rows_with_its_own_code_while_the_rest_upload` | validators; app codes reported (2026-09-16 bug) |
| `A_transformer_rewrites_uploaded_values_and_the_device_receives_the_canonical_row` | transformers, canonical rows |
| `Delete_and_post_commit_handlers_see_exactly_the_committed_keys` | delete and post-commit hooks |
| `A_custom_conflict_resolver_accepts_rejects_or_keeps_the_server_row_per_row` | Custom policy, per-row decisions |
| `Last_writer_wins_compares_the_device_edit_time_with_the_server_change_time` | LastWriterWins, HLC |
| `Client_wins_applies_the_device_edit_over_a_newer_server_edit` | ClientWins |
| `Too_many_rejections_roll_back_the_whole_push` | `MaxRejectionsPerPush` |
| `A_connect_gate_refuses_an_outdated_app_with_a_stable_code` | connect gates, `G9S-VER-UPGRADE` |
| `A_download_only_table_never_captures_or_uploads_device_edits` | `DownloadOnly` |
| **SubscriptionLifecycleTests** | |
| `A_permission_change_reconciles_and_removes_rows_the_user_may_no_longer_see` | auth hash → reconcile (plan P-11) |
| `A_forbidden_subscription_is_reported_and_marked_failed_while_the_rest_sync` | subscription filter, failure keys |
| `A_row_moved_out_of_a_subscription_scope_is_removed_by_reconciliation` | scope limitation and its repair |
| `An_expired_ephemeral_subscription_releases_only_rows_no_other_subscription_holds` | ephemeral TTL, shared membership |
| `A_column_the_server_starts_serving_is_backfilled_without_redownloading_rows` | column negotiation + backfill across a deployment |
| `Rows_already_on_the_device_are_adopted_and_only_differences_move` | adoption + `MarkDirtyAsync` migration path |
| **EngineBehaviourTests** | |
| `Post_steps_run_after_the_data_and_progress_never_goes_backwards` | post steps, monotonic progress |
| `A_user_run_preempts_a_background_run_which_is_queued_again` | pre-emption, re-queue (ADR-0020) |
| `Status_reads_answer_during_a_run_and_identical_queued_requests_coalesce` | P-20 status reads, coalescing |
| **SignalRTransportTests** | |
| `A_device_downloads_and_uploads_over_a_real_signalr_connection` | SignalR transport, streams both ways |
| `A_server_refusal_reaches_the_client_with_its_stable_code` | error envelope through `HubException` |
| `A_committed_server_change_pokes_devices_holding_the_table` | LISTEN → notifier → groups → client event |
| **ServerFeedAndTelemetryTests** | |
| `Server_code_reads_the_exact_change_stream_of_a_table_page_by_page` | change feed (ADR-0018) |
| `Client_and_server_publish_run_and_row_metrics_and_activities` | telemetry (ADR-0019) |

Still untested: see [17-Known-Limitations.md](17-Known-Limitations.md).

## Rules

- A bug fix starts with a failing test when the harness can express it.
- Test names state the behaviour, in sentence case with underscores.
- Never assert on log text or exception messages — assert codes, counts and data.
- Metric assertions use `>=`: listeners are process-wide and tests run in parallel.
