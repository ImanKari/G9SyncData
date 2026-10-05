// The native module of @g9tm/syncdata-lynx. `npm run codegen` turns this declaration into the Android/iOS specs
// (android/…/generated, ios/src/generated) and the JS accessor (generated/). Every reply is an envelope
// `{ ok: true, value } | { ok: false, error: { code, message } }` so error fields survive the bridge.
//
// Error codes (public API — add, never rename or repurpose):
//   G9SYNC-BROTLI-ARG          expectedLength is not an integer in [0, 603979776] (576 MiB), or data is not an ArrayBuffer
//   G9SYNC-BROTLI-INVALID      corrupt or truncated stream, bytes after its end, or an output length other than
//                              expectedLength (more or fewer); also a native failure while decoding (e.g. out of memory),
//                              with the cause in the message. Never a partial buffer.
//   G9SYNC-BROTLI-UNAVAILABLE  no Brotli decoder on this OS version (iOS < 15)

/** @lynxmodule */
export declare class G9SyncDataLynxModule {
  /** `{ version: '1.2.1', platform: 'android' | 'ios', brotli }` — `brotli` is false on iOS < 15. */
  capabilities(callback: (reply: Record<string, unknown>) => void): void;
  /**
   * Decodes the raw Brotli stream `data` (no framing; what .NET's `BrotliEncoder` writes) OFF the JS thread. The value
   * is an ArrayBuffer of EXACTLY `expectedLength` bytes; decoding stops as soon as the stream would produce more.
   */
  brotliDecompress(data: ArrayBuffer, expectedLength: number, callback: (reply: Record<string, unknown>) => void): void;
}
