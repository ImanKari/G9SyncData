// @g9tm/syncdata — the TypeScript twin of the G9SyncData .NET client (abstractions, protocol, engine). The SQLite store
// is `@g9tm/syncdata/sqlite`, the SignalR transport `@g9tm/syncdata/signalr`, Node helpers `@g9tm/syncdata/node`, test
// doubles `@g9tm/syncdata/testing`, the pure-JavaScript Brotli decoder `@g9tm/syncdata/brotli-js`; Lynx wiring is the
// separate package `@g9tm/syncdata-lynx`. Every public type keeps its .NET name without the G9C prefix (PARITY.md).

/** The package version; equal to the .NET packages' (G9SyncDataVersion) — one release, both sides. */
export const VERSION = '1.5.0';

// abstractions
export { G9EConflictPolicy, G9EMoveOutPolicy, G9ESyncColumnType, G9ESyncDirection, columnTypeName, isColumnType } from './abstractions/enums.js';
export { G9SyncErrorCodes, G9SyncException, isRetryableCode, isSyncException, type G9SyncExceptionOptions } from './abstractions/errors.js';
export { G9Long, parseJson, stringifyJson } from './abstractions/long.js';
export { EMPTY_GUID, guidToN, newGuid, normalizeGuid, tryNormalizeGuid } from './abstractions/guid.js';
export { G9SyncKey } from './abstractions/key.js';
export { G9SyncDigest } from './abstractions/digest.js';
export { G9SyncParameters, escapeDataString } from './abstractions/parameters.js';

// protocol
export * from './protocol/dtos.js';
export { G9SyncHubErrors, G9SyncProtocol, negotiateFrameVersion, tableGroup } from './protocol/protocol.js';
export {
  bytesField,
  dateTimeMs,
  fromMessagePackNames,
  G9WireGuid,
  GUID_EXTENSION_TYPE,
  readChangesAvailable,
  readContinuation,
  readHelloResult,
  readPullPlan,
  readPushResult,
  readReconcileSummary,
  readSyncChunk,
  toMessagePackNames,
  type G9WireFormat,
} from './protocol/wire.js';
export { G9ECompression, G9EPushRowOp, G9ERowBatchOp, G9RowBatchFormat, G9RowBatchFormatError } from './protocol/rowBatch/format.js';
export { G9RowBatchWriter, type G9DateTimeOffsetValue, type G9DateTimeValue, type G9FrameCompressor } from './protocol/rowBatch/writer.js';
export {
  G9RowBatchReader,
  getDefaultFrameDecompressor,
  openRowBatch,
  openRowBatchSync,
  readFrameHeader,
  setDefaultFrameDecompressor,
  type G9FrameDecompressor,
  type G9FrameHeader,
  type G9OpenRowBatchOptions,
} from './protocol/rowBatch/reader.js';
export { decimalToDouble, formatDecimal, formatDouble, parseDecimal, type DecimalParts } from './internal/dotnetFormat.js';

// client
export { G9SyncClientModel, G9SyncClientModelBuilder, G9SyncClientTable, G9SyncClientTableBuilder, type G9SyncClientColumn, type G9SyncClientIndex } from './client/model.js';
export * from './client/storeTypes.js';
export type { G9SyncTransport } from './client/transport.js';
export { G9ESyncPhase, G9SyncProgressTracker, phaseName, type G9DtSyncProgress } from './client/progress.js';
export {
  G9ESyncOutcome,
  G9ESyncPriority,
  G9SyncPostStep,
  G9SyncRun,
  describeModelProblem,
  isTransientError,
  normalizeError,
  outcomeName,
  priorityName,
  resolveEngineOptions,
  type G9RowsAppliedEvent,
  type G9SubscriptionOptions,
  type G9SyncEngineOptions,
  type G9SyncHealth,
  type G9SyncHostHooks,
  type G9SyncLogger,
  type G9SyncLogLevel,
  type G9SyncModelProblem,
  type G9SyncPostStepContext,
  type G9SyncRequest,
  type G9SyncRunResult,
} from './client/runs.js';
export { G9SyncEngine, stageGeneration } from './client/engine.js';

// helpers the Lynx package and tests share
export { base64ToBytes, bytesToBase64 } from './internal/base64.js';
export { utf8Decode, utf8Encode } from './internal/utf8.js';
export { md5 } from './internal/md5.js';
export { sha256, toHex } from './internal/sha256.js';
export { cancellationError, createAbortController, delay, linkSignals, onAbort, yieldToEventLoop } from './internal/abort.js';
