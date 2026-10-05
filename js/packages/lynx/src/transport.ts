// The sync transport on Lynx's background thread: @g9tm/syncdata's SignalR transport over the G9 native WebSocket of
// @g9tm/signalr-supernetcore-lynx (release builds of Lynx have no WebSocket of their own), and the Brotli decoder of this
// package's native module for the frames the server sends.
import { G9Client } from '@g9tm/signalr-supernetcore-client';
import { lynxClientOptions, type G9LynxClientOptions } from '@g9tm/signalr-supernetcore-lynx';
import { setDefaultFrameDecompressor, type G9FrameDecompressor } from '@g9tm/syncdata';
import { G9SignalRSyncTransport, type G9SignalRSyncTransportOptions } from '@g9tm/syncdata/signalr';
import { lynxBrotliDecompressor, type G9SyncDataNative } from './native.js';

/** Options of {@link createLynxSyncTransport}: the sync transport's, plus the Lynx connection knobs. */
export interface G9LynxSyncTransportOptions extends Omit<G9SignalRSyncTransportOptions, 'createClient'> {
  /**
   * The Lynx side of the connection (`lynxClientOptions` of @g9tm/signalr-supernetcore-lynx): the native module (detected
   * by default), headers sent with the WebSocket upgrade, `skipNegotiation: false` behind a proxy that needs it, and
   * `onTransportChosen` for diagnostics.
   */
  lynx?: Omit<G9LynxClientOptions, 'url' | 'protocol'>;
}

/**
 * The sync transport for a Lynx app: MessagePack over the native WebSocket (binary frames) without a negotiate round
 * trip by default; long polling over `fetch` where no native module is linked.
 */
export function createLynxSyncTransport(options: G9LynxSyncTransportOptions): G9SignalRSyncTransport {
  return new G9SignalRSyncTransport({
    ...options,
    createClient: (clientOptions) => new G9Client(lynxClientOptions({ ...clientOptions, ...(options.client ?? {}), ...(options.lynx ?? {}) })),
  });
}

/**
 * Makes the native Brotli decoder the process-wide frame decompressor (`setDefaultFrameDecompressor`). Call once at
 * start-up, before the first sync. Without the native module the decoder rejects every compressed frame with
 * `G9SYNC-BROTLI-UNAVAILABLE`; use `jsBrotliDecompressor()` of `@g9tm/syncdata/brotli-js` there instead.
 */
export function useLynxBrotli(native?: G9SyncDataNative): G9FrameDecompressor {
  const decompressor = lynxBrotliDecompressor(native);
  setDefaultFrameDecompressor(decompressor);
  return decompressor;
}
