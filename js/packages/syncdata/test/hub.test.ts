import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { MessageType, type HubMessage } from '@microsoft/signalr';
import { describe, expect, it } from 'vitest';
import { G9Long } from '../src/abstractions/long.js';
import { G9SyncHubErrors } from '../src/protocol/protocol.js';
import { fromMessagePackNames, readChangesAvailable, readHelloResult, readPullPlan, readPushResult, readSyncChunk, toMessagePackNames } from '../src/protocol/wire.js';
import { createSyncMessagePackProtocol, G9SyncJsonHubProtocol } from '../src/signalr/protocols.js';
import { bytesToHex, goldenDirectory, hexToBytes, type Json, vectors } from './golden/vectors.js';
import * as samples from './golden/samples.js';

const json = new G9SyncJsonHubProtocol();
const msgpack = createSyncMessagePackProtocol();

function parseBoth(name: string): { json: Json; msgpack: Json } {
  const fromJson = json.parseMessages(vectors.hub.json[name], null as never)[0] as Json;
  const bytes = hexToBytes(vectors.hub.messagepack[name]);
  const fromMsgpack = msgpack.parseMessages(bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.length) as ArrayBuffer, null as never)[0] as Json;
  return { json: fromJson, msgpack: fromMsgpack };
}

function payloadOf(message: Json): unknown {
  if (message.type === MessageType.Completion) return message.result;
  if (message.type === MessageType.StreamItem) return message.item;
  return message.arguments?.[0];
}

/** Both protocols decode the .NET server's message to the same DTO. */
function decodeBoth<T>(name: string, read: (value: unknown) => T): T {
  const { json: j, msgpack: m } = parseBoth(name);
  const fromJson = read(payloadOf(j));
  const fromMsgpack = read(fromMessagePackNames(payloadOf(m)));
  expect(normalize(fromMsgpack)).toEqual(normalize(fromJson));
  return fromJson;
}

function normalize(value: unknown): unknown {
  return JSON.parse(JSON.stringify(value, (_k, v: unknown) => (v instanceof Uint8Array ? 'bytes:' + bytesToHex(v) : v instanceof G9Long ? 'long:' + v.toString() : v)));
}

describe('hub messages from the .NET server (golden)', () => {
  it('hello result', () => {
    const hello = decodeBoth('helloResult', readHelloResult);
    expect(hello.epoch).toBe(samples.epoch);
    expect(hello.serverTimeUtcMs).toBe(Date.UTC(2026, 9, 5, 12, 34, 56, 789));
    expect(hello.maxPushBytes).toBe(4000000000);
    expect(hello.maxPushRows).toBe(5000);
    expect(hello.tables[0]!.typeMismatchColumns).toEqual([{ name: 'Count', clientType: 2, serverType: 3 }]);
    expect(hello.tables[0]!.direction).toBe(1);
    expect(hello.definitions[0]!.dependsOn).toEqual([{ name: 'Sites', required: false }]);
    expect(hello.subscriptions[0]!.action).toBe(2);
    expect(hello.error).toBeNull();
  });

  it('push result keeps int64 masks exact', () => {
    const result = decodeBoth('pushResult', readPushResult);
    expect(result.conflicts[0]!.serverChangedMask.toString()).toBe('2305843009213693957');
    expect(result.conflicts[1]!.serverChangedMask.toString()).toBe('-1');
    expect(bytesToHex(result.canonicalFrames[0]!)).toBe('4739524201000400000000000000');
    expect(result.commandResults[0]!.commandId).toBe(samples.commandId);
    expect(result.error).toEqual({ code: 'G9S-REJ-TOO-MANY', message: 'too many', retryable: false, details: 'max 10' });
    expect(result.accepted[0]!.deleted).toEqual([false, true]);
    expect(result.rejections[0]!.constraint).toBe('UX_Pot_Code');
  });

  it('pull plan', () => {
    const plan = decodeBoth('pullPlan', readPullPlan);
    expect(plan.token).toBe('tok.en');
    expect(plan.estimatedBytes).toBe(5000000000);
    expect(plan.parts[0]!.kind).toBe(1);
    expect(plan.skipped[0]!.action).toBe(6);
  });

  it('chunks, full and empty', () => {
    const full = decodeBoth('fullChunk', readSyncChunk);
    expect(bytesToHex(full.payload!)).toBe('010203faff');
    expect(full.continuation).toEqual({ partIndex: 1, afterKey: 'k', nextSequence: 4 });
    expect(full.reconcileSummary!.mismatched).toEqual([{ tableId: 3, buckets: [0, 255] }]);
    const empty = decodeBoth('emptyChunk', readSyncChunk);
    expect(empty).toEqual({ sequence: 0, payload: null, rowCount: 0, subscriptionKey: null, isFinal: false, continuation: null, reconcileSummary: null });
  });

  it('server poke, error completion, stream end', () => {
    expect(decodeBoth('changesAvailable', readChangesAvailable)).toEqual({ tables: ['Operation.Pot', 'Base.Site'] });
    const { json: j, msgpack: m } = parseBoth('error');
    expect(j.error).toBe(m.error);
    const error = G9SyncHubErrors.tryParse(j.error)!;
    expect([error.code, error.retryable, error.message]).toEqual(['G9S-SRV-BUSY', true, 'The server is busy.']);
    const end = parseBoth('streamEnd');
    expect(end.json.type).toBe(MessageType.Completion);
    expect(end.msgpack.type).toBe(MessageType.Completion);
  });
});

describe('requests for the .NET server (js-vectors.json)', () => {
  const messages: { [name: string]: HubMessage } = {
    hello: { type: MessageType.Invocation, invocationId: '1', target: 'Hello', arguments: [samples.helloRequest()], streamIds: [] } as HubMessage,
    push: { type: MessageType.Invocation, invocationId: '2', target: 'Push', arguments: [samples.pushRequest()], streamIds: ['3'] } as HubMessage,
    pushItem: { type: MessageType.StreamItem, invocationId: '3', item: samples.pushChunk() } as HubMessage,
    pull: { type: MessageType.StreamInvocation, invocationId: '4', target: 'Pull', arguments: ['tok.en', samples.continuation()], streamIds: [] } as HubMessage,
    reconcile: { type: MessageType.StreamInvocation, invocationId: '5', target: 'Reconcile', arguments: [samples.reconcileRequest()], streamIds: [] } as HubMessage,
    fetchRows: { type: MessageType.StreamInvocation, invocationId: '6', target: 'FetchRows', arguments: [samples.fetchRowsRequest()], streamIds: [] } as HubMessage,
  };

  function forMsgpack(message: HubMessage): HubMessage {
    const m = message as Json;
    if (m.arguments) return { ...m, arguments: m.arguments.map((a: unknown) => toMessagePackNames(a)) };
    if (m.item) return { ...m, item: toMessagePackNames(m.item) };
    return message;
  }

  it('writes them, and they match the committed file (G9_UPDATE_GOLDEN=1 rewrites it)', () => {
    const out: Json = { generator: 'js/packages/syncdata/test/hub.test.ts', hub: { json: {}, messagepack: {} } };
    for (const name of Object.keys(messages)) {
      out.hub.json[name] = json.writeMessage(messages[name]!) as string;
      out.hub.messagepack[name] = bytesToHex(new Uint8Array(msgpack.writeMessage(forMsgpack(messages[name]!)) as ArrayBuffer));
    }
    // The digests are exact int64 on both wires.
    expect(out.hub.json.reconcile).toContain('[-9223372036854775808,-1,9007199254740993,');
    expect(out.hub.json.reconcile).toContain(',9223372036854775807]');
    const text = JSON.stringify(out, null, 2) + '\n';
    const path = join(goldenDirectory, 'js-vectors.json');
    if (process.env.G9_UPDATE_GOLDEN === '1' || !existsSync(path)) writeFileSync(path, text, 'utf8');
    expect(readFileSync(path, 'utf8').replace(/\r\n/g, '\n')).toBe(text);
  });

  it('the MessagePack request round-trips through the patched protocol', () => {
    // The client-side parser ignores stream invocations, so the round trip goes through a plain invocation.
    const invocation = { type: MessageType.Invocation, target: 'Reconcile', arguments: [samples.reconcileRequest()], streamIds: [] } as HubMessage;
    const written = msgpack.writeMessage(forMsgpack(invocation)) as ArrayBuffer;
    const parsed = msgpack.parseMessages(written, null as never)[0] as Json;
    const digests = fromMessagePackNames(parsed.arguments[0]) as Json;
    expect(digests.tables[0].digests[0].toString()).toBe('-9223372036854775808');
    expect(digests.tables[0].digests[2].toString()).toBe('9007199254740993');
    expect(digests.tables[0].digests[255].toString()).toBe('9223372036854775807');
    expect(digests.clientId.value ?? digests.clientId).toBe(samples.clientId);
  });
});
