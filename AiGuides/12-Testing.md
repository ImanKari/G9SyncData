# 12 — Testing

## Suites

| Project | Needs | Covers |
|---|---|---|
| `tests/G9SyncData.Tests` | nothing | G9RowBatch codec round trips; C#↔SQL digest parity (golden values); parameters and subscription keys; JSON contexts; SQLite store capture, push batching, acknowledgement, apply protection; generated model and indexes |
| `tests/G9SyncData.IntegrationTests` | disposable PostgreSQL 13+ | end-to-end sync between real PostgreSQL and real SQLite, over the in-memory transport and over real SignalR (JSON and MessagePack) |
| `tests/G9SyncData.DeviceTests.Android` + `IntegrationTests/Android` | an Android device or emulator, opt-in (`G9SYNC_ANDROID`) | the client library and large-site syncs on a real Android runtime, with device measurements (below) |
| `js/packages/syncdata` (`npm test`, `npm run test:interop`) | nothing; the interop suite needs the disposable PostgreSQL | the TypeScript twin: golden vectors written by `Protocol/JsGoldenVectorsTests.cs`, the engine and store against the real sample server over both hub protocols, the database it writes compared with the .NET client's, and the bulk release path compared with the row-by-row one (`revocation.test.ts`) ([21](21-TypeScript-And-Lynx.md)) |
| `js/packages/lynx` (`npm test`, `npm run test:lynxsim`, native suites) | the lynxsim suite needs the disposable PostgreSQL | the Lynx package: the Brotli native module's cores, the Android parameter types (`android-bridge-types.mjs`, `LynxBridgeSignatureTest`), and the whole sync in a Lynx-shaped runtime |
| `js/apps/lynx-android-host` (`node scripts/device-test.mjs --install --protocol both`) | an Android emulator, the disposable PostgreSQL, the sample server built Debug | the whole sync on a real Lynx runtime (PrimJS) with the real native modules; see its README |

```powershell
dotnet test tests/G9SyncData.Tests
dotnet test tests/G9SyncData.IntegrationTests
```

## Android device tests (opt-in)

The client library on a real Android runtime — AOT-compiled, trimmed, on Android's SQLite and network stack — is a permanent
validation scenario: device behaviour and device performance are what AgriPad users get, and desktop numbers do not predict them
(the platform MD5 alone was 3.3× slower on Android, LES-0040). The tests run on whatever device or emulator adb sees; they skip
everywhere else.

```powershell
$env:G9SYNC_ANDROID = "auto"            # or a serial from `adb devices`; unset = the Android tests skip
dotnet test tests/G9SyncData.IntegrationTests --filter "FullyQualifiedName~Android"
```

| Variable | Default | Effect |
|---|---|---|
| `G9SYNC_ANDROID` | unset (skip) | `auto` = the only device adb lists; otherwise a serial |
| `G9SYNC_ANDROID_BUILD` | build | `skip` reuses the last build of the device app (the first test builds and installs it once per test process) |
| `G9SYNC_ANDROID_CONFIGURATION` | `Release` | Release mirrors AgriPad's Android Release: profiled AOT, `TrimMode=partial` |
| `G9SYNC_ANDROID_ROWS` | `200000,500000` | site sizes of the large-site sync theory |
| `G9SYNC_ANDROID_PROTOCOLS` | `MessagePack` | hub protocols of that theory (`Json,MessagePack` to compare) |
| `G9SYNC_ANDROID_FRAME_VERSION` | 2 | G9RowBatch layout the device asks for (1 to compare, ADR-0040) |
| `G9SYNC_ANDROID_APPLY_ROWS` / `G9SYNC_ANDROID_COMPRESSION_ROWS` | 500000 | sizes of the apply and codec benchmarks |

The large-site sync needs the disposable PostgreSQL as well (`G9SYNC_TEST_PG`); the other tests need only the device.

| Test (`Android/AndroidDeviceTests`) | Proves / measures | Result file (`benchmarks/results/`) |
|---|---|---|
| `The_client_library_tests_pass_on_the_device` | the client unit tests (store, native SQLite, bulk apply, Dotmim retirement, value storage, revocation, abstractions, G9RowBatch codec and layout 2) run on the device through a reflection runner: 77 on 2026-09-17 | `android-unit-latest.md` |
| `The_SQLite_apply_variants_run_on_the_device_and_the_store_path_beats_the_naive_one` | the `--sqlite-apply` variants on device flash and CPU | `android-apply-latest.md` |
| `Frame_codecs_are_measured_on_the_device` | Brotli / Zstandard / frame layouts / snapshot file: size and decompression cost | `android-compression-latest.md` |
| `The_reconciliation_hash_is_computed_without_the_platform_md5_and_faster_than_it_on_the_device` | the managed MD5 (LES-0040) | `android-digest-latest.md` |
| `A_large_site_syncs_on_the_device_over_SignalR_and_matches_the_server_after_every_step(pots, protocol)` | initial → no-op (+10 warm runs) → 1% delta → 5,000-row push → forced reconciliation; after every step the device's pots equal the server's | `android-sync-{pots}-{protocol}-latest.md` |

Each sync step reports: duration and phases, rows and **wire bytes** both ways (a Kestrel connection middleware on the harness server),
device CPU, managed allocations, GC counts, PSS and peak RSS (`/proc/self/status`, sampled), UI-thread frame gaps (a Choreographer
callback: max gap and gaps over 50/100/700 ms), server CPU, database and WAL size, store initialisation time.

**How it works.** `tests/G9SyncData.DeviceTests.Android` (package `com.g9tm.g9syncdata.devicetests`, a plain `net10.0-android` app, no
MAUI) links the client test classes and the benchmarks. The host (`Android/AndroidDevice.cs`) builds it, installs it with `adb install -r`,
starts a scenario with `am start -S -W … --es scenario <name> --es run <id> …`, and polls
`/sdcard/Android/data/<package>/files/runs/<id>/status` until it says `passed` or `failed`; `report.json` and `log.txt` sit beside it
(logcat tag `G9DEVICE`). App-specific external storage needs no debuggable build and no `run-as`. The sync scenario reaches the
harness's Kestrel server at `127.0.0.1` through `adb reverse`; every step is a new process (`am start -S`), so each step is also a cold
app start, as in the field. Tests run one at a time (`[Collection("Android device")]`).

Rules and traps:
- **Never drive the AgriPad app from these tests** (AgriPad AiGuides/25 §0): only the test package is launched. The AgriPad chapter
  `06-Android-Debug-Loop.md` is the source of the adb mechanics used here (install, `am start`, logcat, `dumpsys`).
- Git Bash rewrites `/sdcard/...` arguments into Windows paths: set `MSYS_NO_PATHCONV=1` (or run from PowerShell).
- `System.GC.GetTotalPauseDuration` is 0 on Mono; the report's pause column is informational only.
- **Numbers are only numbers on an idle emulator.** On 2026-09-17 another app working in the background on a swapping emulator made
  the same steps cost ~5× the device CPU (a no-op sync 5.7 s instead of 0.6 s) while every assertion still passed. Before quoting a
  result, check `adb shell top` and `/proc/meminfo`; the report's `cpuMs` for a no-op step (~0.5 s) is a quick sanity check.
- An `am start failed: … daemon not running … cannot connect to daemon` failure is the adb server being restarted underneath the
  run (a Visual Studio session can do it); rerun the test.
- The emulator is x86_64 on a desktop CPU: it validates correctness on the Android runtime and shows relative costs, but absolute
  times are optimistic. **The DOOGEE S99 run remains the required final validation** before adoption ([16](16-AgriPad-Integration.md)).

## PostgreSQL for integration tests

- Connection: `G9SYNC_TEST_PG` (a superuser-capable connection string to the `postgres` database), default
  `Host=localhost;Port=15433;Username=postgres;Database=postgres`.

> **Since 2026-09-16 the development instance is the Docker one in `D:\Postgress`** (PostgreSQL 18.1 on
> **localhost:5432**, user `iman`, password in that compose file), because the WAL capture tests need
> `wal_level=logical` and that instance has it. Tests create and drop their own `g9t_*` / `g9wal_*` databases and
> never touch the `agriwise_production` copy that lives beside them.
>
> **The capture tests skip rather than fail** when the server is not configured for logical replication:
> `[PostgresLogicalFact]` probes `current_setting('wal_level')` and reports why it skipped. A skipped capture test on
> CI means the CI database is not set up, not that the feature is broken - see
> [`20-Infrastructure-PostgreSQL.md`](20-Infrastructure-PostgreSQL.md).
>
> ⛔ **Every capture test must drop its replication slot**, in a `finally`/`DisposeAsync` that runs even when the test
> fails. A leaked slot silently retains WAL forever - the one test-hygiene failure in this suite that can degrade the
> whole machine rather than just the next test run. Verify after a run:
> `select count(*) from pg_replication_slots` must be 0.
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

## Running the suite in both capture modes

The integration suite is run **twice**: once as is (trigger capture) and once with the WAL consumer:

```powershell
$env:G9SYNC_TEST_CAPTURE = "wal"; dotnet test tests/G9SyncData.IntegrationTests; $env:G9SYNC_TEST_CAPTURE = $null
```

In WAL mode every test database gets its own publication and slot (named after the database), the harness starts the
consumer, and **every harness server write waits until it has been captured** (`SyncHarness.WaitForCaptureAsync`), so tests
keep reading "committed means tracked" in both modes. A test that writes through its own connection must call it too.

`Capture/WalCaptureModeTests` forces WAL mode regardless of the variable and covers what only WAL mode has: a push with the
consumer stopped, column merge against an unrecorded server edit, a push recorded once, a no-op update, seeding of rows that
predate WAL mode, and a capture gap repaired by a rescan. `Capture/LogicalReplicationChangeSourceTests` covers pgoutput
decoding itself, including that an unconfirmed batch is redelivered (LES-0015).

**After every run, `select count(*) from pg_replication_slots` must be 0.** The harness drops each slot before its database;
a leaked slot retains WAL on the machine indefinitely.

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
| `Preempting_a_run_cancels_it_outside_the_queue_lock` | a cancellation callback that re-enters the engine completes (LES-0036; failed with a 5 s timeout before) |
| **SignalRTransportTests** (each over JSON and MessagePack, ADR-0039) | |
| `A_device_downloads_and_uploads_over_a_real_signalr_connection` | SignalR transport, streams both ways, reconciliation |
| `A_server_refusal_reaches_the_client_with_its_stable_code` | error envelope through `HubException` |
| `A_committed_server_change_pokes_devices_holding_the_table` | LISTEN → notifier → groups → client event |
| `Json_and_MessagePack_clients_of_one_server_hold_the_same_rows_and_the_binary_one_uses_fewer_bytes` | identical rows; MessagePack < 85% of JSON's bytes (no base64) |
| **FrameLayoutNegotiationTests** (ADR-0040) | |
| `Clients_asking_for_either_frame_layout_get_it_for_every_frame_and_end_with_the_servers_rows_through_pull_push_reconcile_and_fetch` | a recording transport checks the layout byte of every pull, reconcile and fetch frame; the rows equal the server's after each step (mutation-checked: a fetch path ignoring the request fails it) |
| `A_request_that_does_not_state_a_layout_gets_row_wise_frames_…` | an older client (no `FrameVersion`) gets layout 1 |
| **Migration/DotmimCutoverTests** (ADR-0038) | |
| `Offline_work_from_the_Dotmim_era_reaches_the_server_once_and_the_device_then_syncs_normally` | a device file with Dotmim's exact triggers, tracking and scope tables: triggers and tables dropped, unsent upserts and deletes imported, pushed once; later deltas and local edits work |
| `Cutting_over_twice_and_syncing_in_between_uploads_nothing_twice` | idempotent cut-over |
| **G9SyncData.Tests / Client/SqliteDotmimRetirementTests** (ADR-0038) | only Dotmim's triggers go (a look-alike app trigger and an app audit trigger stay; identification by name AND body); unsent work becomes pending with distinct push sequences; running again changes nothing; scope tables go once no tracking table is left and downloads skip triggers again (event 20110 before, none after); options; a database without Dotmim is untouched; tables outside the model are refused |
| **G9SyncData.Tests / Protocol/RowBatchColumnLayoutTests** (ADR-0040) | 300 random frames (every op, key type, column type, nulls) read identically in both layouts and convert back byte for byte; empty frames and sections; 20,000 damaged bodies raise only `FormatException`; a page of AgriPad pots is < 90% as large |
| **G9SyncData.Tests / Client/SqliteApplyValueTests** | every column type (GUID, Int64 and String keys × decimal as text × DateTime as ticks) is stored in the exact form the app reads — including '' and a zero-length blob — through the allocation-free apply |
| **G9SyncData.Tests / Client/SqliteSyncStoreTests** `Rows_marked_dirty_together_each_get_their_own_push_sequence` | LES-0039: `MarkDirtyAsync` of many rows gave them one `local_seq`, which the server's staging index refused (23505) |
| **G9SyncData.Tests / Abstractions/AbstractionsTests** | the span digest equals the string digest; `G9CMd5` matches the RFC 1321 vectors and the platform MD5 for inputs of 0–300 bytes |
| **ServerFeedAndTelemetryTests** | |
| `Server_code_reads_the_exact_change_stream_of_a_table_page_by_page` | change feed (ADR-0018) |
| `Client_and_server_publish_run_and_row_metrics_and_activities` | telemetry (ADR-0019) |

| **Scopes/EfCoreScopeTests** (ADR-0029) | |
| `An_EF_query_scope_delivers_exactly_what_the_equivalent_SQL_scope_delivers_in_snapshots_and_deltas` | EF scope ≡ SQL scope over pages, inserts, edits, soft deletes, moves |
| `The_application_query_filters_decide_what_a_device_receives` | a named EF query filter applies to sync visibility |
| `Revoking_a_membership_removes_exactly_the_revoked_rows_once_the_permission_stamp_changes` | revocation without re-initialisation; pins the "stamp needed" rule |
| `Uploads_are_authorised_by_the_EF_query_before_and_after_the_change` | push authorisation through a provider scope (insert outside, move out) |
| `A_provider_parameter_named_like_a_declared_parameter_keeps_the_provider_value` | EF 10 names `@SiteId`; the binder must not rebind it (asserts the premise too) |
| `A_scope_query_over_the_wrong_entity_stops_the_server_at_startup` | startup probe, entity/table check |
| `A_scope_query_over_a_table_the_database_lacks_stops_the_server_at_startup` | two probes (LES-0023) |
| `With_provider_validation_off_a_broken_scope_fails_the_device_sync_instead_of_the_deployment` | the opt-out's behaviour |
| **Scopes/KeySetTests** (ADR-0030) | |
| `A_provider_snapshot_is_paged_through_one_key_set_that_is_released_when_the_part_completes` | multi-page key-set part, release |
| `Identical_parts_requested_concurrently_share_one_build` | advisory-lock single build, content identity |
| `A_key_set_lost_mid_part_is_rebuilt_and_no_row_is_skipped_or_repeated` | loss detection, rebuild, exact continuation |
| `A_row_changed_after_the_build_is_left_to_the_next_delta_which_applies_the_scope_live` | the build-snapshot guard; a moved-out row is never sent |
| `An_idle_key_set_is_swept_with_its_keys_and_a_later_read_rebuilds_it` | sweep, recovery |
| `Reconciliation_pages_a_provider_scope_through_a_transaction_local_key_table` | multi-page reconciliation, revocation |
| `A_SQL_template_scope_uses_a_key_set_unless_configured_otherwise_and_delivers_the_same_rows` | both template paths agree |

| **Caching/DigestCacheIntegrationTests** (ADR-0024) | |
| `Changes_made_since_an_entry_was_valid_are_applied_and_a_second_reconciliation_is_served_from_the_cache` | incremental edit, delete, insert, move out of scope, When failure; hit asserted |
| `A_change_in_another_table_the_scope_reads_forces_derivation_instead_of_a_stale_answer` | dependency tables via EXPLAIN VERBOSE (LES-0026); hit asserted |
| `A_delete_whose_tombstone_was_purged_after_the_entry_is_still_removed` | tombstone horizon check |
| `More_changes_than_the_incremental_limit_are_derived_and_still_exact` | `MaxIncrementalKeys` fallback |
| `A_scope_reading_a_table_that_is_not_synced_is_never_cached_and_stays_correct` | untracked dependency → not cached |
| **G9SyncData.Tests / Server/DigestCacheTests** | XOR bookkeeping vs recomputation after 5,000 random operations (Guid, Int64, String keys); check-out exclusivity; later snapshot wins; LRU byte ceiling; MaxAge |

| **G9SyncData.Tests / Server/EfCoreModelExtractorTests** (LES-0034 additions) | owned JSON document = one text column cast to `jsonb`; owned/complex types split into the table contribute columns with the table's nullability; exclusion/read-only by member path; the EF writer demands a converter for a writable JSON document |
| **Writes/RequiredColumnTests** (LES-0034, LES-0035) | |
| `A_required_column_uploads_cannot_fill_stops_the_server_at_startup` | NOT NULL, no default, not writable, no writer → `G9S-MODEL-INVALID` |
| `A_row_writer_that_supplies_a_required_column_owns_the_check_and_the_value` | a writer takes over the check and fills the column |
| `Rows_a_writer_returns_carry_every_column_the_device_holds_including_server_owned_ones` | canonical rows in the device's columns (`HeldTables`) |
| `An_insert_through_EF_Core_saves_the_owned_json_document_its_constructor_created` | `db.Add` tracks owned JSON (failed with `State = Added`) |
| **Writes/EfCoreRowWriterTests** (ADR-0025 rows) | |
| `Uploads_go_through_SaveChanges_so_the_application_stamps_them_and_the_device_receives_the_stamped_row` | interceptor runs; canonical rows |
| `A_row_the_application_rejects_is_isolated_while_the_others_are_written` | `G9CRowRejectedException` + bisection |
| `A_database_error_inside_SaveChanges_is_isolated_with_its_real_sqlstate` | DbUpdateException unwrapped to 23505 |
| `Only_the_columns_the_policy_applies_are_written_so_a_concurrent_server_edit_survives` | apply mask honoured by the writer |
| `Deletes_go_through_the_context` | deletes seen by the interceptor |
| `A_writer_for_an_entity_that_maps_to_no_synced_table_stops_the_server_at_startup` | no silent no-op writers |
| **Writes/CommandTests** (ADR-0025 commands) | |
| `A_swap_that_row_writes_cannot_express_succeeds_as_a_command_and_both_sides_agree` | E-09 class: rows collide (23505), the command succeeds; EF handler |
| `A_rejected_command_keeps_nothing_on_the_server_and_restores_the_device` | savepoint rollback of a partial write; device reverts |
| `A_command_whose_result_was_lost_is_executed_once` | replayed batch |
| `A_command_resent_in_a_new_batch_after_its_result_was_lost_returns_the_stored_outcome_without_running_again` | command log idempotency |
| `A_pending_command_survives_an_app_restart_and_runs_on_the_next_sync` | durable outbox |
| `A_pull_does_not_overwrite_a_row_its_pending_command_touched_and_the_result_settles_it` | protection + settle |
| `An_unknown_command_is_rejected_with_a_stable_code_and_the_device_is_restored` | `G9S-CMD-UNKNOWN` |
| `A_row_a_command_creates_outside_the_device_scope_is_not_kept_on_the_device` | visibility of canonical rows |
| `A_command_recorded_on_the_apps_own_connection_commits_with_the_apps_other_writes` | A-11: a raw app handle in its own transaction; invisible until the app commits; effects are not row changes |
| `A_command_on_the_apps_connection_is_undone_by_the_apps_rollback_and_by_its_own_failure` | savepoint rollback, app transaction survives, `BEGIN IMMEDIATE` path, `g9_ctx` reset |
| `A_connection_open_on_another_database_is_refused` | no silently lost command |
| **G9SyncData.Tests / Client/SqliteNativeTests** (ADR-0032) | lazy statement compilation; reader finishes the command; rebinding and change counts; value round trips; missing parameter; error codes; `BEGIN IMMEDIATE` lock; pooled handle rollback; `ClearPool` with a handle in use; borrowed handles; nested transactions; rejected connection strings; trigger execution switched per connection (prepared statements recompile), a handle returned with triggers off never reused, never switched on a borrowed handle |
| **G9SyncData.Tests / Client/SqliteBulkApplyTests** (ADR-0034) | plain indexes rebuilt byte-for-byte (partial, descending, app-created) and unique ones untouched; no rebuild for a non-empty table or below the threshold; a failed large apply rolls back rows AND dropped indexes and can be applied again; a unique duplicate still fails; every pooled handle afterwards has triggers on and `synchronous=FULL`; command triggers and capture still work; an app trigger keeps execution on and fires once per downloaded row; all fast paths off give the same result. Mutation-checked: removing the restores fails 4 tests |

| **Conflicts/ConflictRuleTests** (ADR-0031) | |
| `An_edit_of_a_row_the_server_deleted_is_rejected_by_rule_with_the_application_code` | `ServerDeleted` → `Reject` |
| `A_status_only_edit_newer_than_the_server_change_takes_the_device_value` | `ChangedOnly` + `ClientIsNewer` → `TakeClient` |
| `When_both_sides_change_the_note_the_device_keeps_it_and_the_server_keeps_its_other_columns` | `Overlapping` → `ApplyColumns`, partial conflict reported |
| `Disjoint_edits_merge` | `Disjoint` → `MergeColumns` |
| `A_rule_can_decide_by_looking_up_other_data_in_the_push_transaction` | `WhenAsync` querying another table |
| `A_conflict_no_rule_matches_falls_back_to_the_table_policy` | fallback |
| `Rules_and_the_Custom_policy_cannot_be_combined` | model validation |

| **Revocation/RevocationPolicyTests** (ADR-0026) | |
| `Revoked_rows_without_pending_changes_are_deleted_and_counted` | default strict removal |
| `A_revoked_row_with_an_unsynchronised_edit_is_quarantined_and_can_be_restored_when_access_returns` | offline edit + revocation; quarantine; restore uploads only the user's columns |
| `A_policy_can_keep_revoked_rows_marked_and_say_why_until_they_are_visible_again` | MarkRevoked, rule/reason, unmarking |
| `RetainWhile_keeps_revoked_rows_until_its_condition_is_released` | re-evaluation at the end of runs |
| `A_custom_rule_can_redact_the_row_it_keeps` | editor; redaction never uploaded |
| `Unsubscribing_applies_the_policy_with_the_unsubscribed_reason` | unsubscribe path |
| **G9SyncData.Tests / Client/RevocationPolicyUnitTests** | defaults, first match, unique names |

| **Resync/EpochAndRestoreTests** | |
| `Reinstalling_capture_changes_the_epoch_and_devices_reconcile_to_the_rows_including_changes_made_without_capture` | epoch → `ResyncRequired` → reconcile; WAL rescan |
| `A_restored_backup_rotates_the_epoch_and_devices_converge_on_the_restored_rows_keeping_their_unsynced_edits` | database OID changed → epoch rotated, key sets purged; unsynced device edits survive |
| `A_copied_database_rotates_its_epoch_once_and_keeps_it_across_restarts` | `CREATE DATABASE … TEMPLATE` copy; rotation is not repeated |
| `A_cursor_newer_than_the_database_is_never_trusted` | point-in-time restore / failover: `pg_snapshot_xmax(cursor)` ahead of the database |
| **Resilience/SignalRConnectionLossTests** | |
| `A_pull_cut_mid_stream_reconnects_and_resumes_from_its_continuation` | real SignalR connection aborted by a hub filter mid-stream; resume, no duplicate rows |
| `A_push_whose_answer_is_lost_with_the_connection_is_applied_exactly_once` | connection dies after the server committed |
| `A_connection_lost_between_runs_is_re_established_by_the_next_run` | reconnect on the next run |
| **Resilience/MultiInstanceTests** | |
| `Every_call_may_land_on_a_different_instance_that_shares_the_signing_key` | `RoutingTransport` alternates instances per call: plan on one, pull and push on others |
| `An_instance_without_the_shared_key_refuses_foreign_tokens_and_the_device_recovers_on_the_next_run` | `G9S-TOKEN-INVALID`, recovery |
| **Keys/KeyTypesEndToEndTests** | |
| `Text_and_integer_keys_stay_exact_through_snapshot_delta_push_delete_and_reconciliation` | String and Int64 keys end to end (case, spaces, large values) |
| `Reconciliation_repairs_text_and_integer_key_tables_after_tombstones_were_purged` | horizon repair for non-GUID keys |
| `A_computed_column_that_changed_without_its_row_is_repaired_by_reconciliation` | computed columns in reconciliation hashes |
| **Writes/EfCoreJsonAndComplexWriteTests** | |
| `Json_documents_and_complex_properties_download_and_upload_through_the_applications_context` | owned JSON with a converter and complex scalars through the EF writer, both directions |
| `A_document_the_converter_cannot_read_rejects_only_its_row` | conversion failure → `G9S-REJ-VALIDATION` for that row only |
| **Writes/UploadOnlyLimitsAndWriteRuleTests** | |
| `Upload_only_rows_go_up_and_never_come_down_back_or_out` | `UploadOnly`: no download, reconcile, canonical rows or pokes; `ForgetUploadedRowsAsync` |
| `The_servers_row_limit_is_announced_and_the_device_batches_to_it` | Hello `MaxPushRows` |
| `The_servers_byte_limit_splits_batches_and_a_row_that_can_never_fit_is_rejected_alone` | Hello `MaxPushBytes`, byte-budget batches, `G9S-REJ-TOO-LARGE` locally |
| `A_write_rule_holds_before_and_after_every_uploaded_change` | `WriteRule` before and after |
| **Scopes/PagingCostTests** (ADR-0035) | |
| `A_snapshot_page_deep_in_a_large_part_reads_only_about_its_own_rows_of_the_tracking_table` | EXPLAIN ANALYZE of the real page at 15,000 of 20,000 rows: ≤ 3 × page per relation (15,621 without the bound) |
| `A_delta_page_deep_in_a_large_part_reads_only_about_its_own_rows_of_the_base_table` | same for a delta page (25,000 pots without the bound) |
| `A_large_key_set_refreshes_its_tables_statistics_so_a_deep_page_is_read_in_key_order_without_a_sort` | `ANALYZE` after a large build; no Sort / Bitmap Heap Scan; the part still delivers every row once, in order |

`SyncClient.SyncUntilSettledAsync` tolerates the conservative `ResyncRequired` a freshly purged tombstone can report under parallel load (LES-0029).

Parallel tests must never call `SqliteConnection.ClearAllPools()`; use `SyncHarness.ClearPoolOf(path)` (LES-0030).

Both test assemblies initialise SQLitePCLRaw in a module initializer (`SqliteProviderInitializer`): the store no longer does it
(ADR-0032). They reference Microsoft.Data.Sqlite.Core only to inspect device files. `-p:G9SqliteBundleVersion=3.0.5` runs either
suite on the SQLitePCLRaw 3.x stack; the store binary stays compiled against 2.1.13.

A cache test must assert the cache was USED (hits), not only the final state: a forced reconcile on a device's first sync is a pull
and never touches the cache (LES-0027).

The EF tests configure the DbContext with an unreachable server (`EfScopeFixture.NeverOpened`): a provider that executed its
query instead of translating it would fail them.

Still untested: see [17-Known-Limitations.md](17-Known-Limitations.md).

## Benchmarking against a copy of Agriwise data (scope benchmark)

The scope benchmark (`--scopes`, [19](19-Performance.md)) needs real data shapes, and the engine installs triggers, so it runs
on a **copy**, never on `agriwise_production` itself. Only the columns the benchmark reads are copied; the source is only read:

```bash
P="psql -h localhost -U iman"          # PGPASSWORD from D:\Postgress\docker-compose.yml, never written to a file
$P -d postgres -c "create database g9bench_scope"
$P -d g9bench_scope -c 'create schema farm;
  create table farm."Field" ("Id" uuid primary key, "FarmId" uuid not null);
  create table farm."Block" ("Id" uuid primary key, "FieldId" uuid not null);
  create table farm."Ridge" ("Id" uuid primary key, "BlockId" uuid not null);
  create table farm."Pot" ("Id" uuid primary key, "RidgeId" uuid not null, "Index" integer not null, "VarietyId" uuid null,
    "Gender" integer not null, "SerialNumber" varchar null, "CreatedTime" timestamp not null, "UpdatedTime" timestamp null);'
$P -d agriwise_production -c 'COPY (SELECT "Id","FarmId" FROM "Operation"."Field") TO STDOUT' | $P -d g9bench_scope -c 'COPY farm."Field" FROM STDIN'
# … the same for Block ("Id","FieldId"), Ridge ("Id","BlockId") and Pot (the eight columns above)
$P -d g9bench_scope -c 'create index on farm."Pot" ("RidgeId")' -c 'create index on farm."Ridge" ("BlockId")' \
   -c 'create index on farm."Block" ("FieldId")' -c 'create index on farm."Field" ("FarmId")' -c 'analyze'
```

1.28 M pots copy in about 4 seconds. `pg_dump -t` does not work for this (LES-0025). The benchmark refuses any database whose
name does not start with `g9bench`. Drop the copy when done.

## Rules

- A bug fix starts with a failing test when the harness can express it.
- Test names state the behaviour, in sentence case with underscores.
- Never assert on log text or exception messages — assert codes, counts and data.
- Metric assertions use `>=`: listeners are process-wide and tests run in parallel.
