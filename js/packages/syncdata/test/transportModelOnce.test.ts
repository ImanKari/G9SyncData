import { HubConnectionState } from '@microsoft/signalr';
import type { G9Client } from '@g9tm/signalr-supernetcore-client';
import { describe, expect, it } from 'vitest';
import type { G9DtHelloRequest } from '../src/protocol/dtos.js';
import { G9ESyncHubProtocol, G9SignalRSyncTransport } from '../src/signalr/transport.js';

// Twin of .NET HelloModelOnceTests (1.5.21, AgriPad TR-0069): the transport sends the client model on a connection's first Hello and only
// the server's id for it after that; a server that lost it gets it again at once; a new connection (a reconnect) starts with the full model.

interface Sent {
  tables: number;
  modelHash: string | null;
}

function fake(answers: Array<(sent: Sent) => object>) {
  const sent: Sent[] = [];
  let reconnected: ((connectionId?: string) => void) | null = null;
  const connection = {
    state: HubConnectionState.Connected,
    connectionId: 'conn-1',
    invoke: async (_method: string, request: { tables?: unknown[]; modelHash?: string | null }) => {
      const s = { tables: request.tables?.length ?? 0, modelHash: request.modelHash ?? null };
      sent.push(s);
      const answer = answers.length > 1 ? answers.shift()! : answers[0]!;
      return { tables: [], unknownTables: [], definitions: [], subscriptions: [], ...answer(s) };
    },
    onreconnected: (callback: (connectionId?: string) => void) => {
      reconnected = callback;
    },
    stop: async () => undefined,
  };
  const client = { connection, on: () => undefined, start: async () => undefined, stop: async () => undefined } as unknown as G9Client;
  return {
    client,
    sent,
    reconnect: (id: string) => {
      connection.connectionId = id;
      reconnected?.(id);
    },
  };
}

const flush = () => new Promise((resolve) => setTimeout(resolve, 20));
const model = [{ name: 'Operation.Pot', keyColumn: 'Id', keyType: 7, columns: [] }] as G9DtHelloRequest['tables'];
const request = (tables = model): G9DtHelloRequest => ({ clientId: 'c', protocolVersion: 1, tables, subscriptions: [] });
const serverKnowsModel = (s: Sent) => (s.tables > 0 || s.modelHash === 'M1' ? { modelHash: 'M1' } : { modelRequired: true });

describe('the model once per connection (1.5.21)', () => {
  it('sends the model on the first Hello and only its id on the next ones of the same connection', async () => {
    const f = fake([serverKnowsModel]);
    const transport = new G9SignalRSyncTransport({ url: 'https://sync.example/g9sync', protocol: G9ESyncHubProtocol.Json, createClient: () => f.client });

    await transport.hello(request());
    await transport.hello(request());
    await transport.hello(request());

    expect(f.sent).toEqual([
      { tables: 1, modelHash: null },
      { tables: 0, modelHash: 'M1' },
      { tables: 0, modelHash: 'M1' },
    ]);
  });

  it('sends the model again when the tables are another array (the engine keeps one) or the server never named it', async () => {
    const f = fake([() => ({})]);
    const transport = new G9SignalRSyncTransport({ url: 'https://sync.example/g9sync', protocol: G9ESyncHubProtocol.Json, createClient: () => f.client });

    await transport.hello(request());
    await transport.hello(request());   // an older server: no model id, so the model goes every time

    expect(f.sent.map((s) => s.tables)).toEqual([1, 1]);
  });

  it('a server that lost the model is answered with it inside the same call', async () => {
    const f = fake([serverKnowsModel]);
    const transport = new G9SignalRSyncTransport({ url: 'https://sync.example/g9sync', protocol: G9ESyncHubProtocol.Json, createClient: () => f.client });
    await transport.hello(request());

    const forgetting = fake([() => ({ modelRequired: true }), () => ({ modelHash: 'M2' })]);
    (f.client.connection as unknown as { invoke: unknown }).invoke = (forgetting.client.connection as unknown as { invoke: unknown }).invoke;
    const result = await transport.hello(request());

    expect(forgetting.sent).toEqual([
      { tables: 0, modelHash: 'M1' },
      { tables: 1, modelHash: null },
    ]);
    expect(result.modelRequired).toBe(false);
    expect(result.modelHash).toBe('M2');
  });

  it('a reconnected connection starts with the full model, in the rejoin and after it', async () => {
    const f = fake([serverKnowsModel]);
    const transport = new G9SignalRSyncTransport({ url: 'https://sync.example/g9sync', protocol: G9ESyncHubProtocol.Json, createClient: () => f.client });
    await transport.hello(request());
    await transport.hello(request());

    f.reconnect('conn-2');
    await flush();
    await transport.hello(request());

    expect(f.sent).toEqual([
      { tables: 1, modelHash: null },
      { tables: 0, modelHash: 'M1' },
      { tables: 1, modelHash: null },   // the rejoin: a new connection never holds the model
      { tables: 0, modelHash: 'M1' },   // named again once the new connection sent it
    ]);
  });
});
