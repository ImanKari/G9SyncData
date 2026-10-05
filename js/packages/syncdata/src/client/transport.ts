// Twin of G9SyncData.Client/Transport/IG9SyncTransport.cs.
import type {
  G9DtChangesAvailable,
  G9DtFetchRowsRequest,
  G9DtHelloRequest,
  G9DtHelloResult,
  G9DtPullContinuation,
  G9DtPullPlan,
  G9DtPullRequest,
  G9DtPushRequest,
  G9DtPushResult,
  G9DtReconcileRequest,
  G9DtSyncChunk,
} from '../protocol/dtos.js';

/**
 * Carries protocol calls to a G9SyncData server. Implementations: SignalR (`@g9tm/syncdata/signalr`, and its Lynx
 * wiring in `@g9tm/syncdata-lynx`) and the in-memory test transport (`@g9tm/syncdata/testing`). Failures surface as
 * G9SyncException; transport faults use G9S-NET-FAILURE.
 */
export interface G9SyncTransport {
  readonly isConnected: boolean;
  /** Subscribes to server "pokes" (tables changed); returns the unsubscribe function. */
  onChangesAvailable(handler: (changes: G9DtChangesAvailable) => void): () => void;
  connect(signal?: AbortSignal): Promise<void>;
  hello(request: G9DtHelloRequest, signal?: AbortSignal): Promise<G9DtHelloResult>;
  push(request: G9DtPushRequest, chunks: AsyncIterable<G9DtSyncChunk>, signal?: AbortSignal): Promise<G9DtPushResult>;
  planPull(request: G9DtPullRequest, signal?: AbortSignal): Promise<G9DtPullPlan>;
  pull(token: string, continuation: G9DtPullContinuation | null, signal?: AbortSignal): AsyncIterable<G9DtSyncChunk>;
  reconcile(request: G9DtReconcileRequest, signal?: AbortSignal): AsyncIterable<G9DtSyncChunk>;
  fetchRows(request: G9DtFetchRowsRequest, signal?: AbortSignal): AsyncIterable<G9DtSyncChunk>;
  dispose(): Promise<void>;
}
