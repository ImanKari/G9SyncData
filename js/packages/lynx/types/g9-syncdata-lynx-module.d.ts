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
//   G9SYNC-FILE-ARG            a snapshot-file call with an empty path, a negative or fractional size/offset/length,
//                              a length above 64 MiB or a SHA-256 that is not 64 hex characters
//   G9SYNC-FILE-INVALID        a snapshot-file segment that does not decompress to `length` bytes or fails its SHA-256;
//                              nothing was written
//   G9SYNC-FILE-IO             the file system refused (missing directory, no space, permissions), message has the cause
//   G9SYNC-FILE-UNAVAILABLE    (JS side only) no module with the snapshot-file methods: none registered, or one built
//                              before snapshot files
//
// The snapshot-file methods (ADR-0072) are the device's G9SnapshotFileSink: a downloaded segment goes from the transport
// straight to its place in the staged file, decompressed, checked and written off the JS thread - a site's 20-40 MB
// never crosses back into JavaScript, and SQLite reads the finished file itself (ATTACH).

/** @lynxmodule */
export declare class G9SyncDataLynxModule {
  /**
   * `{ version: '1.2.2', platform: 'android' | 'ios', brotli, snapshotFiles, cacheDirectory }` — `brotli` is false on
   * iOS < 15; `cacheDirectory` is the app's cache directory (Android `Context.cacheDir`, iOS Caches), where staged
   * snapshot files belong (app-private, outside backups), or null.
   */
  capabilities(callback: (reply: Record<string, unknown>) => void): void;
  /**
   * Decodes the raw Brotli stream `data` (no framing; what .NET's `BrotliEncoder` writes) OFF the JS thread. The value
   * is an ArrayBuffer of EXACTLY `expectedLength` bytes; decoding stops as soon as the stream would produce more.
   */
  brotliDecompress(data: ArrayBuffer, expectedLength: number, callback: (reply: Record<string, unknown>) => void): void;
  /** Creates `path` (its directory too) - or truncates it - as `size` zero bytes. Value: null. */
  snapshotFileCreate(path: string, size: number, callback: (reply: Record<string, unknown>) => void): void;
  /**
   * Decompresses the Brotli segment `data`, checks that it is `length` bytes whose SHA-256 is `sha256` (lower-case hex),
   * and writes it at `offset` in `path`; with `sync`, the file is flushed to disk before the reply. Value: null.
   */
  snapshotFileWriteSegment(
    path: string,
    offset: number,
    data: ArrayBuffer,
    length: number,
    sha256: string,
    sync: boolean,
    callback: (reply: Record<string, unknown>) => void,
  ): void;
  /** Value: the file's size in bytes, or null when it does not exist. */
  snapshotFileSize(path: string, callback: (reply: Record<string, unknown>) => void): void;
  /** Value: an ArrayBuffer of the file's first `length` bytes (fewer when the file is shorter). */
  snapshotFileHead(path: string, length: number, callback: (reply: Record<string, unknown>) => void): void;
  /** Deletes the file; a missing file is not an error. Value: null. */
  snapshotFileRemove(path: string, callback: (reply: Record<string, unknown>) => void): void;
  /** Value: the full paths of the files in `directory` (empty when it does not exist). */
  snapshotFileList(directory: string, callback: (reply: Record<string, unknown>) => void): void;
}
