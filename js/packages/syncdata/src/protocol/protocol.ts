// Twins of G9SyncData.Protocol/G9SyncProtocol.cs and G9SyncHubContract.cs (G9SyncHubErrors).
import { G9SyncException } from '../abstractions/errors.js';

/** Protocol constants shared by every transport (`G9SyncProtocol`). */
export const G9SyncProtocol = {
  /** Wire protocol version. The server refuses a Hello from a different major version. */
  Version: 1,
  /** The newest G9RowBatch body layout this build reads and writes (1 row-wise, 2 column-grouped; ADR-0040). */
  MaxFrameVersion: 2,
  /** Default SignalR hub route. */
  DefaultHubRoute: '/g9sync',
  /** Hub method names (client → server). */
  Methods: {
    Hello: 'Hello',
    Push: 'Push',
    PlanPull: 'PlanPull',
    Pull: 'Pull',
    Reconcile: 'Reconcile',
    FetchRows: 'FetchRows',
  },
  /** Listener method names (server → client). */
  Listeners: {
    OnChangesAvailable: 'OnChangesAvailable',
  },
} as const;

/** The frame layout to answer a request with: the newest both sides know, and 1 for a sender that did not say. */
export function negotiateFrameVersion(requested: number): number {
  return Math.min(Math.max(requested, 1), G9SyncProtocol.MaxFrameVersion);
}

/** The SignalR group of connections that negotiated a table (`G9SyncProtocol.TableGroup`). */
export function tableGroup(logicalTable: string): string {
  if (typeof logicalTable !== 'string' || logicalTable.trim() === '') throw new TypeError('A table name is required.');
  return 'g9sync:' + logicalTable.toLowerCase();
}

const PREFIX = '[G9S|';

/**
 * Carries a G9SyncException through SignalR (`G9SyncHubErrors`): a HubException message is the only error text SignalR
 * forwards, so the code travels inside it as `[G9S|{code}|{retryable 0/1}] {message}`.
 */
export const G9SyncHubErrors = {
  format(exception: G9SyncException): string {
    return PREFIX + exception.code + '|' + (exception.retryable ? '1' : '0') + '] ' + exception.message;
  },

  /** Finds the marker anywhere in `text` (the client wraps it in its own words); null when there is none. */
  tryParse(text: string | null | undefined): G9SyncException | null {
    if (!text) return null;
    const start = text.indexOf(PREFIX);
    if (start < 0) return null;
    const end = text.indexOf(']', start);
    if (end < 0) return null;
    const header = text.slice(start + PREFIX.length, end);
    const bar = header.lastIndexOf('|');
    if (bar <= 0) return null;
    const code = header.slice(0, bar);
    const retryable = header.slice(bar + 1) === '1';
    return new G9SyncException(code, text.slice(end + 1).trim(), { retryable });
  },
};
