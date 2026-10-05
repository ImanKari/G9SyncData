// @g9tm/syncdata/testing — the twin of G9SyncData.Testing's fault scripting (G9CTransportFaults). The .NET in-memory
// transport calls a server engine in-process; JavaScript has no server engine, so the TypeScript twin wraps ANY transport
// (typically the SignalR transport against a test server) and injects the same scripted faults (PARITY.md).
import { G9SyncErrorCodes, G9SyncException } from '../abstractions/errors.js';
import { delay } from '../internal/abort.js';
import type { G9SyncTransport } from '../client/transport.js';
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

/** The protocol operations (`G9ETransportOperation`). */
export const G9ETransportOperation = {
  Connect: 0,
  Hello: 1,
  Push: 2,
  PlanPull: 3,
  Pull: 4,
  Reconcile: 5,
  FetchRows: 6,
} as const;
export type G9ETransportOperation = (typeof G9ETransportOperation)[keyof typeof G9ETransportOperation];

const NAMES = ['Connect', 'Hello', 'Push', 'PlanPull', 'Pull', 'Reconcile', 'FetchRows'];

/** Scripted transport failures, each consumed once per affected call (`G9CTransportFaults`). */
export class G9TransportFaults {
  /** Delay (ms) added before every call and every streamed chunk. */
  latencyMs = 0;
  private readonly _failBefore = new Map<number, number>();
  private readonly _failAfter = new Map<number, number>();
  private readonly _breakAfter = new Map<number, number[]>();

  /** The next `times` calls fail before reaching the server. */
  failBefore(operation: G9ETransportOperation, times = 1): this {
    this._failBefore.set(operation, (this._failBefore.get(operation) ?? 0) + times);
    return this;
  }

  /** The next `times` unary calls complete on the server, then the response is lost ("applied but not acknowledged"). */
  failAfter(operation: G9ETransportOperation, times = 1): this {
    this._failAfter.set(operation, (this._failAfter.get(operation) ?? 0) + times);
    return this;
  }

  /** The next `times` streams of the operation break after delivering `chunks` chunks. */
  breakStreamAfter(operation: G9ETransportOperation, chunks: number, times = 1): this {
    if (chunks < 0) throw new RangeError('chunks must be non-negative.');
    const queue = this._breakAfter.get(operation) ?? [];
    for (let i = 0; i < times; i++) queue.push(chunks);
    this._breakAfter.set(operation, queue);
    return this;
  }

  clear(): void {
    this._failBefore.clear();
    this._failAfter.clear();
    this._breakAfter.clear();
  }

  /** @internal */
  takeFailBefore(operation: number): boolean {
    return take(this._failBefore, operation);
  }

  /** @internal */
  takeFailAfter(operation: number): boolean {
    return take(this._failAfter, operation);
  }

  /** @internal Chunks to deliver before breaking, or -1. */
  takeStreamBreak(operation: number): number {
    const queue = this._breakAfter.get(operation);
    return queue && queue.length > 0 ? (queue.shift() as number) : -1;
  }
}

function take(map: Map<number, number>, operation: number): boolean {
  const remaining = map.get(operation) ?? 0;
  if (remaining <= 0) return false;
  map.set(operation, remaining - 1);
  return true;
}

function networkFailure(operation: number, reason: string): G9SyncException {
  return new G9SyncException(G9SyncErrorCodes.NetworkFailure, `Injected fault on ${NAMES[operation]}: ${reason}.`, { retryable: true });
}

/** A transport that injects {@link G9TransportFaults} into another one, and counts calls. */
export class G9FaultInjectingTransport implements G9SyncTransport {
  readonly inner: G9SyncTransport;
  readonly faults = new G9TransportFaults();
  /** The continuation of every pull call, in call order (null for a fresh pull). */
  readonly pullContinuations: (G9DtPullContinuation | null)[] = [];
  private readonly _calls = new Map<number, number>();
  private _disconnected = false;

  constructor(inner: G9SyncTransport) {
    this.inner = inner;
  }

  get isConnected(): boolean {
    return !this._disconnected && this.inner.isConnected;
  }

  callCount(operation: G9ETransportOperation): number {
    return this._calls.get(operation) ?? 0;
  }

  /** Simulates a dropped connection: the next call fails until the engine reconnects. */
  disconnect(): void {
    this._disconnected = true;
  }

  onChangesAvailable(handler: (changes: G9DtChangesAvailable) => void): () => void {
    return this.inner.onChangesAvailable(handler);
  }

  async connect(signal?: AbortSignal): Promise<void> {
    await this._begin(G9ETransportOperation.Connect, signal);
    this._disconnected = false;
    await this.inner.connect(signal);
  }

  hello(request: G9DtHelloRequest, signal?: AbortSignal): Promise<G9DtHelloResult> {
    return this._unary(G9ETransportOperation.Hello, signal, () => this.inner.hello(request, signal));
  }

  push(request: G9DtPushRequest, chunks: AsyncIterable<G9DtSyncChunk>, signal?: AbortSignal): Promise<G9DtPushResult> {
    return this._unary(G9ETransportOperation.Push, signal, () => this.inner.push(request, chunks, signal));
  }

  planPull(request: G9DtPullRequest, signal?: AbortSignal): Promise<G9DtPullPlan> {
    return this._unary(G9ETransportOperation.PlanPull, signal, () => this.inner.planPull(request, signal));
  }

  pull(token: string, continuation: G9DtPullContinuation | null, signal?: AbortSignal): AsyncIterable<G9DtSyncChunk> {
    this.pullContinuations.push(continuation ? { ...continuation } : null);
    return this._stream(G9ETransportOperation.Pull, signal, () => this.inner.pull(token, continuation, signal));
  }

  reconcile(request: G9DtReconcileRequest, signal?: AbortSignal): AsyncIterable<G9DtSyncChunk> {
    return this._stream(G9ETransportOperation.Reconcile, signal, () => this.inner.reconcile(request, signal));
  }

  fetchRows(request: G9DtFetchRowsRequest, signal?: AbortSignal): AsyncIterable<G9DtSyncChunk> {
    return this._stream(G9ETransportOperation.FetchRows, signal, () => this.inner.fetchRows(request, signal));
  }

  dispose(): Promise<void> {
    return this.inner.dispose();
  }

  private async _begin(operation: number, signal?: AbortSignal): Promise<void> {
    if (signal && signal.aborted) throw new G9SyncException(G9SyncErrorCodes.Canceled, 'The call was canceled.');
    this._calls.set(operation, (this._calls.get(operation) ?? 0) + 1);
    if (this.faults.latencyMs > 0) await delay(this.faults.latencyMs, signal);
    if (this.faults.takeFailBefore(operation)) throw networkFailure(operation, 'the call failed before reaching the server');
  }

  private _ensureConnected(): void {
    if (this._disconnected) throw new G9SyncException(G9SyncErrorCodes.NetworkFailure, 'The transport is not connected.', { retryable: true });
  }

  private async _unary<T>(operation: number, signal: AbortSignal | undefined, call: () => Promise<T>): Promise<T> {
    await this._begin(operation, signal);
    this._ensureConnected();
    const result = await call();
    if (this.faults.takeFailAfter(operation)) throw networkFailure(operation, 'the response was lost after the server completed the call');
    return result;
  }

  private _stream(operation: number, signal: AbortSignal | undefined, open: () => AsyncIterable<G9DtSyncChunk>): AsyncIterable<G9DtSyncChunk> {
    // eslint-disable-next-line @typescript-eslint/no-this-alias
    const self = this;
    return {
      async *[Symbol.asyncIterator](): AsyncGenerator<G9DtSyncChunk> {
        await self._begin(operation, signal);
        self._ensureConnected();
        const breakAfter = self.faults.takeStreamBreak(operation);
        let delivered = 0;
        for await (const chunk of open()) {
          if (breakAfter >= 0 && delivered >= breakAfter) throw networkFailure(operation, `the stream broke after ${delivered} chunks`);
          if (self._disconnected) throw networkFailure(operation, 'the connection dropped');
          if (self.faults.latencyMs > 0) await delay(self.faults.latencyMs, signal);
          delivered++;
          yield chunk;
        }
      },
    };
  }
}
