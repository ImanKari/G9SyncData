// Twin of G9SyncData.Client.SignalR/G9CSignalRSyncTransport.cs: the sync transport over the G9SignalRSuperNetCore client
// (its jittered reconnect policy and connection states), every failure surfacing as a G9SyncException with the server's
// stable code. Lynx hosts build the client with the native WebSocket of @g9tm/signalr-supernetcore-lynx
// (`createLynxSyncTransport` in @g9tm/syncdata-lynx does).
import { G9Client, type G9ClientOptions } from '@g9tm/signalr-supernetcore-client';
import { HubConnectionState, Subject, type IHubProtocol, type IStreamResult, type ISubscription } from '@microsoft/signalr';
import { G9SyncErrorCodes, G9SyncException, isSyncException } from '../abstractions/errors.js';
import { createAbortController, onAbort } from '../internal/abort.js';
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
import { G9SyncHubErrors, G9SyncProtocol } from '../protocol/protocol.js';
import {
  fromMessagePackNames,
  readChangesAvailable,
  readHelloResult,
  readPullPlan,
  readPushResult,
  readSyncChunk,
  toMessagePackNames,
  type G9WireFormat,
} from '../protocol/wire.js';
import { createSyncMessagePackProtocol, G9SyncJsonHubProtocol } from './protocols.js';

/** The SignalR hub protocol a client speaks (`G9ESyncHubProtocol`). The server accepts both. */
export const G9ESyncHubProtocol = {
  /** Text frames; payloads base64-encoded (+33%). Readable in traces. */
  Json: 0,
  /** Binary frames: payloads as raw bytes. The default. */
  MessagePack: 1,
} as const;
export type G9ESyncHubProtocol = (typeof G9ESyncHubProtocol)[keyof typeof G9ESyncHubProtocol];

/** Options of {@link G9SignalRSyncTransport} (`G9CSignalRSyncTransportOptions`). Durations are milliseconds. */
export interface G9SignalRSyncTransportOptions {
  /** Absolute hub URL, e.g. `https://sync.example.com/g9sync`. */
  url: string;
  /** Hub protocol; MessagePack by default. */
  protocol?: G9ESyncHubProtocol;
  /** Supplies the bearer token for every (re)connect. */
  accessTokenFactory?: () => string | Promise<string>;
  /** SignalR stateful reconnect (ADR-0069); off by default, the server must allow it too. */
  statefulReconnect?: boolean;
  /** How long one connect may take before it counts as a network failure. */
  connectTimeoutMs?: number;
  serverTimeoutMs?: number;
  keepAliveIntervalMs?: number;
  /**
   * How long a single-answer call (Hello, PlanPull) waits for its answer before it is a retryable G9S-NET-FAILURE (ADR-0074).
   * Default 60000; 0 = no limit. A live connection does not guarantee an answer: pings keep it up while the call's message
   * was lost on the way.
   */
  callTimeoutMs?: number;
  /** How long a push may take from its first chunk to its result; a safety net, not a pace. Default 600000; 0 = no limit. */
  pushTimeoutMs?: number;
  /**
   * How long a server stream (Pull, Reconcile, FetchRows) may go without a chunk before it is a retryable G9S-NET-FAILURE; the
   * engine resumes from its last continuation. Default 120000 (a snapshot file may still be building); 0 = no limit (ADR-0074).
   */
  streamIdleTimeoutMs?: number;
  /**
   * Everything else the G9 client takes (transport, headers, WebSocket constructor, skipNegotiation, reconnect policy, log
   * level…) — the twin of ConfigureHttpConnection/ConfigureBuilder. `url`, `protocol` and the timeouts above win.
   */
  client?: Partial<G9ClientOptions>;
  /** Builds the client (tests, hosts with their own wiring); `new G9Client(options)` by default. */
  createClient?: (options: G9ClientOptions) => G9Client;
}

/** The SignalR implementation of {@link G9SyncTransport}. */
export class G9SignalRSyncTransport implements G9SyncTransport {
  readonly client: G9Client;
  readonly format: G9WireFormat;
  private readonly _options: G9SignalRSyncTransportOptions;
  private readonly _handlers: ((changes: G9DtChangesAvailable) => void)[] = [];
  private _connecting: Promise<void> | null = null;

  constructor(options: G9SignalRSyncTransportOptions) {
    if (!options || typeof options.url !== 'string' || options.url.trim() === '') throw new TypeError('A hub URL is required.');
    this._options = options;
    const json = options.protocol === G9ESyncHubProtocol.Json;
    this.format = json ? 'json' : 'messagepack';
    const protocol: IHubProtocol = json ? new G9SyncJsonHubProtocol() : createSyncMessagePackProtocol();
    const clientOptions: G9ClientOptions = {
      ...(options.client ?? {}),
      url: options.url,
      protocol,
      statefulReconnect: options.statefulReconnect ?? false,
      serverTimeoutMs: options.serverTimeoutMs ?? 30000,
      keepAliveIntervalMs: options.keepAliveIntervalMs ?? 10000,
    };
    if (options.accessTokenFactory) clientOptions.accessTokenFactory = options.accessTokenFactory;
    this.client = options.createClient ? options.createClient(clientOptions) : new G9Client(clientOptions);
    this.client.on(G9SyncProtocol.Listeners.OnChangesAvailable, (payload: unknown) => this._onChanges(payload));
  }

  get isConnected(): boolean {
    return this.client.connection.state === HubConnectionState.Connected;
  }

  onChangesAvailable(handler: (changes: G9DtChangesAvailable) => void): () => void {
    this._handlers.push(handler);
    return () => {
      const index = this._handlers.indexOf(handler);
      if (index >= 0) this._handlers.splice(index, 1);
    };
  }

  /** Connects (joins a connect in progress); a connect that takes longer than connectTimeoutMs is a network failure. */
  connect(signal?: AbortSignal): Promise<void> {
    if (this.isConnected) return Promise.resolve();
    if (!this._connecting) {
      const connecting = this._connectCore().finally(() => {
        if (this._connecting === connecting) this._connecting = null;
      });
      this._connecting = connecting;
    }
    const connecting = this._connecting;
    if (!signal) return connecting;
    return new Promise<void>((resolve, reject) => {
      const unsubscribe = onAbort(signal, () => reject(new G9SyncException(G9SyncErrorCodes.Canceled, 'The connect was canceled.')));
      connecting.then(
        () => {
          unsubscribe();
          resolve();
        },
        (error) => {
          unsubscribe();
          reject(error);
        },
      );
    });
  }

  private async _connectCore(): Promise<void> {
    const timeout = createAbortController();
    const timer = setTimeout(() => timeout.abort(), this._options.connectTimeoutMs ?? 20000);
    try {
      await this.client.start(timeout.signal);
    } catch (error) {
      const mapped = mapError(error, false) ?? new G9SyncException(G9SyncErrorCodes.NetworkFailure, messageOf(error), { retryable: true, cause: error });
      throw mapped.code === G9SyncErrorCodes.Canceled
        ? new G9SyncException(G9SyncErrorCodes.NetworkFailure, `The sync connection to ${this._options.url} did not open in time.`, { retryable: true, cause: error })
        : mapped;
    } finally {
      clearTimeout(timer);
    }
    if (!this.isConnected) {
      throw new G9SyncException(G9SyncErrorCodes.NetworkFailure, `The sync connection is ${String(this.client.connection.state)}.`, { retryable: true });
    }
  }

  hello(request: G9DtHelloRequest, signal?: AbortSignal): Promise<G9DtHelloResult> {
    return this._invoke(G9SyncProtocol.Methods.Hello, [this._wire(request)], readHelloResult, this._callTimeoutMs(), signal);
  }

  push(request: G9DtPushRequest, chunks: AsyncIterable<G9DtSyncChunk>, signal?: AbortSignal): Promise<G9DtPushResult> {
    const subject = new Subject<unknown>();
    const result = this._invoke(G9SyncProtocol.Methods.Push, [this._wire(request), subject], readPushResult, this._options.pushTimeoutMs ?? 600000, signal);
    void (async () => {
      try {
        for await (const chunk of chunks) subject.next(this._wire(chunk));
        subject.complete();
      } catch (error) {
        subject.error(error);
      }
    })();
    return result;
  }

  planPull(request: G9DtPullRequest, signal?: AbortSignal): Promise<G9DtPullPlan> {
    return this._invoke(G9SyncProtocol.Methods.PlanPull, [this._wire(request)], readPullPlan, this._callTimeoutMs(), signal);
  }

  pull(token: string, continuation: G9DtPullContinuation | null, signal?: AbortSignal): AsyncIterable<G9DtSyncChunk> {
    return this._stream(G9SyncProtocol.Methods.Pull, [token, continuation === null ? null : this._wire(continuation)], signal);
  }

  reconcile(request: G9DtReconcileRequest, signal?: AbortSignal): AsyncIterable<G9DtSyncChunk> {
    return this._stream(G9SyncProtocol.Methods.Reconcile, [this._wire(request)], signal);
  }

  fetchRows(request: G9DtFetchRowsRequest, signal?: AbortSignal): AsyncIterable<G9DtSyncChunk> {
    return this._stream(G9SyncProtocol.Methods.FetchRows, [this._wire(request)], signal);
  }

  async dispose(): Promise<void> {
    this._handlers.length = 0;
    await this.client.stop();
  }

  // ================================================================ plumbing

  private _wire(value: unknown): unknown {
    return this.format === 'messagepack' ? toMessagePackNames(value) : value;
  }

  private _read(value: unknown): unknown {
    return this.format === 'messagepack' ? fromMessagePackNames(value) : value;
  }

  private _callTimeoutMs(): number {
    return this._options.callTimeoutMs ?? 60000;
  }

  private async _invoke<T>(method: string, args: unknown[], read: (value: unknown) => T, timeoutMs: number, signal?: AbortSignal): Promise<T> {
    if (signal && signal.aborted) throw new G9SyncException(G9SyncErrorCodes.Canceled, 'The call was canceled.');
    let unsubscribe: () => void = () => undefined;
    let timer: ReturnType<typeof setTimeout> | null = null;
    let timedOut = false;
    try {
      const call = this.client.connection.invoke(method, ...args);
      const raw =
        signal || timeoutMs > 0
          ? await new Promise<unknown>((resolve, reject) => {
              if (signal) unsubscribe = onAbort(signal, () => reject(new G9SyncException(G9SyncErrorCodes.Canceled, 'The call was canceled.')));
              if (timeoutMs > 0) {
                timer = setTimeout(() => {
                  timedOut = true;
                  reject(
                    new G9SyncException(G9SyncErrorCodes.NetworkFailure, `The sync server did not answer ${method} within ${Math.round(timeoutMs / 1000)} s.`, {
                      retryable: true,
                    }),
                  );
                }, timeoutMs);
              }
              call.then(resolve, reject);
            })
          : await call;
      return read(this._read(raw));
    } catch (error) {
      // ADR-0074: the answer did not come in time. The connection is not trusted any more (the server runs one invocation per
      // connection at a time, and whatever lost this answer may lose the next): drop it, so the retry runs on a fresh one.
      if (timedOut) await this._dropConnection();
      throw mapError(error, signal ? signal.aborted : false) ?? error;
    } finally {
      if (timer !== null) clearTimeout(timer);
      unsubscribe();
    }
  }

  /** Ends the connection after a call or stream ran out of time (ADR-0074); the next call connects afresh. Never throws. */
  private async _dropConnection(): Promise<void> {
    try {
      await this.client.connection.stop();
    } catch {
      // Stopping a connection that is already gone may throw; the next call connects afresh either way.
    }
  }

  private _stream(method: string, args: unknown[], signal?: AbortSignal): AsyncIterable<G9DtSyncChunk> {
    // eslint-disable-next-line @typescript-eslint/no-this-alias
    const self = this;
    return {
      [Symbol.asyncIterator](): AsyncIterator<G9DtSyncChunk> {
        const queue: G9DtSyncChunk[] = [];
        let done = false;
        let failure: unknown = null;
        let wake: (() => void) | null = null;
        let subscription: ISubscription<unknown> | null = null;
        const notify = (): void => {
          const w = wake;
          wake = null;
          if (w) w();
        };
        let unsubscribeAbort: () => void = () => undefined;
        // ADR-0074: the idle limit runs only while the iterator waits for the next chunk, never while the caller processes one.
        const idleMs = self._options.streamIdleTimeoutMs ?? 120000;
        let idleTimer: ReturnType<typeof setTimeout> | null = null;
        let idleExpired = false;
        const stopIdle = (): void => {
          if (idleTimer !== null) clearTimeout(idleTimer);
          idleTimer = null;
        };
        const armIdle = (): void => {
          stopIdle();
          if (idleMs <= 0 || done) return;
          idleTimer = setTimeout(() => {
            idleTimer = null;
            if (done) return;
            idleExpired = true;
            failure = new G9SyncException(G9SyncErrorCodes.NetworkFailure, `The sync server sent nothing on ${method} for ${Math.round(idleMs / 1000)} s.`, {
              retryable: true,
            });
            done = true;
            subscription?.dispose();
            notify();
          }, idleMs);
        };
        const start = (): void => {
          if (subscription || done) return;
          let stream: IStreamResult<unknown>;
          try {
            stream = self.client.connection.stream(method, ...args);
          } catch (error) {
            failure = error;
            done = true;
            return;
          }
          subscription = stream.subscribe({
            next: (item) => {
              queue.push(readSyncChunk(self._read(item)));
              notify();
            },
            error: (error) => {
              failure = error;
              done = true;
              notify();
            },
            complete: () => {
              done = true;
              notify();
            },
          });
          unsubscribeAbort = onAbort(signal, () => {
            failure = new G9SyncException(G9SyncErrorCodes.Canceled, 'The stream was canceled.');
            done = true;
            subscription?.dispose();
            notify();
          });
        };
        const finish = (): void => {
          stopIdle();
          unsubscribeAbort();
          if (subscription && !done) subscription.dispose();
          done = true;
        };
        return {
          async next(): Promise<IteratorResult<G9DtSyncChunk>> {
            start();
            for (;;) {
              const item = queue.shift();
              if (item) {
                stopIdle();
                return { value: item, done: false };
              }
              if (done) {
                stopIdle();
                unsubscribeAbort();
                if (idleExpired) await self._dropConnection();
                if (failure !== null) throw mapError(failure, signal ? signal.aborted : false) ?? failure;
                return { value: undefined as unknown as G9DtSyncChunk, done: true };
              }
              if (idleTimer === null) armIdle();
              await new Promise<void>((resolve) => {
                wake = resolve;
              });
            }
          },
          async return(): Promise<IteratorResult<G9DtSyncChunk>> {
            finish();
            return { value: undefined as unknown as G9DtSyncChunk, done: true };
          },
        };
      },
    };
  }

  private _onChanges(payload: unknown): void {
    const changes = readChangesAvailable(this._read(payload));
    for (const handler of this._handlers.slice()) {
      try {
        handler(changes);
      } catch {
        // A ChangesAvailable handler threw (logged by the .NET transport); it must not break the connection.
      }
    }
  }
}

function messageOf(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

/**
 * The sync exception for a transport failure (`Map`): the server's `[G9S|code|retryable]` marker wins; any other hub
 * error is a retryable server error; 401/403 is Unauthorized; the rest is a retryable network failure. Null leaves the
 * error as it is (an already mapped one).
 */
export function mapError(error: unknown, canceled: boolean): G9SyncException | null {
  if (isSyncException(error)) return null;
  if (canceled) return new G9SyncException(G9SyncErrorCodes.Canceled, 'The call was canceled.', { cause: error });
  const message = messageOf(error);
  const marked = G9SyncHubErrors.tryParse(message);
  if (marked) return marked;
  const status = (error as { statusCode?: unknown }).statusCode;
  if (status === 401 || status === 403) return new G9SyncException(G9SyncErrorCodes.Unauthorized, 'The sync server refused the credentials.', { cause: error });
  if (error instanceof Error && error.name === 'AbortError') return new G9SyncException(G9SyncErrorCodes.Canceled, message, { cause: error });
  // A completion error from the hub: SignalR words it "An unexpected error occurred invoking '…' on the server."
  if (/on the server|HubException/i.test(message)) return new G9SyncException(G9SyncErrorCodes.ServerError, message, { retryable: true, cause: error });
  return new G9SyncException(G9SyncErrorCodes.NetworkFailure, message, { retryable: true, cause: error });
}
