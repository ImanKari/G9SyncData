import { HubConnectionState } from '@microsoft/signalr';
import type { G9Client } from '@g9tm/signalr-supernetcore-client';
import { describe, expect, it } from 'vitest';
import { G9ESyncHubProtocol, G9SignalRSyncTransport } from '../src/signalr/transport.js';

// Twin of .NET SignalRTransportTests.After_the_connection_reconnects_by_itself_... (S-2, LES-0087): the server joins a connection to its
// tables' poke groups in Hello only, and a connection that reconnected by itself is a new connection id, so the transport sends the last
// accepted Hello again on reconnected.

function fake() {
  const calls: string[] = [];
  let reconnected: ((connectionId?: string) => void) | null = null;
  const connection = {
    state: HubConnectionState.Connected,
    invoke: async (method: string) => {
      calls.push(method);
      return { tables: [], unknownTables: [], definitions: [], subscriptions: [] };
    },
    onreconnected: (callback: (connectionId?: string) => void) => {
      reconnected = callback;
    },
    stop: async () => undefined,
  };
  const client = { connection, on: () => undefined, start: async () => undefined, stop: async () => undefined } as unknown as G9Client;
  return { client, calls, reconnect: () => reconnected?.('new-id') };
}

const flush = () => new Promise((resolve) => setTimeout(resolve, 20));

describe('poke groups after a reconnect (LES-0087)', () => {
  it('sends the last accepted hello again when the connection reconnects by itself', async () => {
    const f = fake();
    const transport = new G9SignalRSyncTransport({ url: 'https://sync.example/g9sync', protocol: G9ESyncHubProtocol.Json, createClient: () => f.client });

    await transport.hello({ clientId: 'c' } as never);
    f.reconnect();
    await flush();

    expect(f.calls.filter((m) => m === 'Hello')).toHaveLength(2);
  });

  it('sends nothing on a reconnect before any hello was accepted', async () => {
    const f = fake();
    new G9SignalRSyncTransport({ url: 'https://sync.example/g9sync', protocol: G9ESyncHubProtocol.Json, createClient: () => f.client });

    f.reconnect();
    await flush();

    expect(f.calls).toHaveLength(0);
  });
});
