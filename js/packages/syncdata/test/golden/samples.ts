// The TypeScript side of tests/G9SyncData.Tests/Protocol/JsGoldenSamples.cs: the same requests, field for field. The .NET
// test deserializes what the TypeScript protocols write from these and compares them with its own samples.
import { G9Long } from '../../src/abstractions/long.js';
import type {
  G9DtFetchRowsRequest,
  G9DtHelloRequest,
  G9DtPullContinuation,
  G9DtPushRequest,
  G9DtReconcileRequest,
  G9DtSubscriptionRef,
  G9DtSyncChunk,
} from '../../src/protocol/dtos.js';

export const clientId = '6f1c2a7e-3b4d-4e5f-8a9b-0c1d2e3f4a5b';
export const epoch = '0f8fad5b-d9cb-469f-a165-70867728950e';
export const commandId = 'a1b2c3d4-e5f6-4a7b-8c9d-0e1f2a3b4c5d';

export function subscription(): G9DtSubscriptionRef {
  return { name: 'Site', parameters: { siteId: '0f8fad5b-d9cb-469f-a165-70867728950e', year: '2026' }, cursor: 'c:42', definitionHash: 'def1', authHash: 'auth1' };
}

export function helloRequest(): G9DtHelloRequest {
  return {
    clientId,
    appVersion: '6.0.1',
    protocolVersion: 1,
    tables: [
      {
        name: 'Operation.Pot',
        keyColumn: 'Id',
        keyType: 7,
        columns: [
          { name: 'Code', type: 6, nullable: false },
          { name: 'Weight', type: 5, nullable: true },
        ],
      },
    ],
    subscriptions: [subscription()],
  };
}

export function pushRequest(): G9DtPushRequest {
  return {
    clientId,
    batchId: 77,
    subscriptions: [subscription()],
    tables: [{ tableId: 3, columns: ['Code', 'Weight'] }],
    commands: [{ commandId, sequence: 5, name: 'Pot.Move', version: 2, payload: '{"to":"r1"}', createdUtcMs: 1759667696789, touched: [{ tableId: 3, key: 'k1' }] }],
    heldTables: [{ tableId: 4, columns: ['Name'] }],
    frameVersion: 2,
  };
}

export function pushChunk(): G9DtSyncChunk {
  return {
    sequence: 0,
    payload: new Uint8Array([0x47, 0x39, 0x52, 0x42, 1, 0, 4, 0, 0, 0, 0, 0, 0, 0]),
    rowCount: 0,
    subscriptionKey: null,
    isFinal: true,
    continuation: null,
    reconcileSummary: null,
  };
}

export function continuation(): G9DtPullContinuation {
  return { partIndex: 2, afterKey: 'after', nextSequence: 9 };
}

export function reconcileRequest(): G9DtReconcileRequest {
  const digests: G9Long[] = [];
  const counts: number[] = [];
  for (let i = 0; i < 256; i++) {
    digests.push(G9Long.ZERO);
    counts.push(0);
  }
  digests[0] = G9Long.MIN_VALUE;
  digests[1] = G9Long.MINUS_ONE;
  digests[2] = G9Long.parse('9007199254740993');
  digests[255] = G9Long.MAX_VALUE;
  counts[0] = 1;
  counts[255] = 2000000;
  return { clientId, subscription: subscription(), tables: [{ tableId: 3, computedColumns: ['Weight'], digests, counts }], frameVersion: 2 };
}

export function fetchRowsRequest(): G9DtFetchRowsRequest {
  return { clientId, subscription: subscription(), tableId: 3, keys: ['k1', 'k2'], columns: ['Code'], frameVersion: 1 };
}
