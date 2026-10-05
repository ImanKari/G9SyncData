// @g9tm/syncdata-lynx — Lynx support for the G9SyncData TypeScript client: the Brotli native module (Android, iOS,
// Lynxtron) and the sync transport over the G9 native WebSocket. The engine and the store are @g9tm/syncdata's; the
// SQLite driver is the app's (G9LynxControls' native driver satisfies the store's G9SqliteDriver contract).

/** The package version; equal to @g9tm/syncdata's and to the .NET packages' (one release, every side). */
export const VERSION = '1.2.1';

export * from './native.js';
export { createLynxSyncTransport, useLynxBrotli, type G9LynxSyncTransportOptions } from './transport.js';
