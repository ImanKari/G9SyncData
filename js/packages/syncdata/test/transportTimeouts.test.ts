import { HubConnectionState, Subject } from '@microsoft/signalr';
import type { G9Client } from '@g9tm/signalr-supernetcore-client';
import { describe, expect, it } from 'vitest';
import { G9SyncErrorCodes, G9SyncException } from '../src/abstractions/errors.js';
import { G9ESyncHubProtocol, G9SignalRSyncTransport, type G9SignalRSyncTransportOptions } from '../src/signalr/transport.js';

// Twin of SignalRTimeoutTests (ADR-0074): a connection that stays up while a call's answer never comes. Every call has a limit, every
// stream an idle limit, either one ending is a retryable G9S-NET-FAILURE, and the connection is dropped so the retry starts afresh.

interface FakeClient {
  stops: number;
  streams: Subject<unknown>[];
  client: G9Client;
}

function fakeClient(): FakeClient {
  const fake: FakeClient = { stops: 0, streams: [], client: null as unknown as G9Client };
  const connection = {
    state: HubConnectionState.Connected,
    invoke: () => new Promise<unknown>(() => undefined), // the answer never comes
    stream: () => {
      const subject = new Subject<unknown>();
      fake.streams.push(subject);
      return subject;
    },
    stop: async () => {
      fake.stops++;
    },
  };
  fake.client = { connection, on: () => undefined, start: async () => undefined, stop: async () => undefined } as unknown as G9Client;
  return fake;
}

function transport(fake: FakeClient, options: Partial<G9SignalRSyncTransportOptions>): G9SignalRSyncTransport {
  return new G9SignalRSyncTransport({
    url: 'https://sync.example/g9sync',
    protocol: G9ESyncHubProtocol.Json,
    createClient: () => fake.client,
    ...options,
  });
}

async function failureOf(promise: Promise<unknown>): Promise<G9SyncException> {
  try {
    await promise;
  } catch (error) {
    expect(error).toBeInstanceOf(G9SyncException);
    return error as G9SyncException;
  }
  throw new Error('expected a failure');
}

describe('transport limits (ADR-0074)', () => {
  it('a hello whose answer never comes fails as a retryable network failure and drops the connection', async () => {
    const fake = fakeClient();
    const started = Date.now();

    const error = await failureOf(transport(fake, { callTimeoutMs: 50 }).hello({} as never));

    expect(error.code).toBe(G9SyncErrorCodes.NetworkFailure);
    expect(error.retryable).toBe(true);
    expect(error.message).toContain('Hello');
    expect(Date.now() - started).toBeLessThan(2000);
    expect(fake.stops).toBe(1);
  });

  it('plan pull has the same limit', async () => {
    const fake = fakeClient();
    const error = await failureOf(transport(fake, { callTimeoutMs: 50 }).planPull({} as never));
    expect(error.code).toBe(G9SyncErrorCodes.NetworkFailure);
    expect(error.message).toContain('PlanPull');
  });

  it('a caller cancellation stays a cancellation and keeps the connection', async () => {
    const fake = fakeClient();
    const abort = new AbortController();
    const call = transport(fake, { callTimeoutMs: 60000 }).hello({} as never, abort.signal);
    abort.abort();

    const error = await failureOf(call);

    expect(error.code).toBe(G9SyncErrorCodes.Canceled);
    expect(fake.stops).toBe(0);
  });

  it('a stream that goes silent ends with a retryable network failure after the chunks it delivered', async () => {
    const fake = fakeClient();
    const iterator = transport(fake, { streamIdleTimeoutMs: 50 }).pull('token', null)[Symbol.asyncIterator]();

    const first = iterator.next();
    fake.streams[0]!.next({ sequence: 0, rowCount: 3, isFinal: false });
    expect((await first).done).toBe(false);

    const error = await failureOf(iterator.next());

    expect(error.code).toBe(G9SyncErrorCodes.NetworkFailure);
    expect(error.retryable).toBe(true);
    expect(error.message).toContain('Pull');
    expect(fake.stops).toBe(1);
  });

  it('the idle limit does not run while the caller is busy with a chunk', async () => {
    const fake = fakeClient();
    const iterator = transport(fake, { streamIdleTimeoutMs: 50 }).pull('token', null)[Symbol.asyncIterator]();
    const first = iterator.next();
    fake.streams[0]!.next({ sequence: 0, rowCount: 1, isFinal: false });
    await first;

    // The caller takes longer than the limit to come back; the next chunk is already waiting.
    fake.streams[0]!.next({ sequence: 1, rowCount: 1, isFinal: false });
    await new Promise((resolve) => setTimeout(resolve, 150));
    const second = await iterator.next();

    expect(second.done).toBe(false);
    expect(fake.stops).toBe(0);
    await iterator.return?.();
  });

  it('a limit of zero waits as before', async () => {
    const fake = fakeClient();
    const outcome = await Promise.race([
      transport(fake, { callTimeoutMs: 0 }).hello({} as never).then(() => 'answered', () => 'failed'),
      new Promise((resolve) => setTimeout(() => resolve('still waiting'), 150)),
    ]);
    expect(outcome).toBe('still waiting');
  });
});
