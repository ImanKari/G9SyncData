// @g9tm/syncdata/brotli-js — a pure-JavaScript Brotli decoder for frames, for runtimes with no native one (Lynx web,
// a Lynx host without @g9tm/syncdata-lynx's native module, tests). It is Google's own decoder (about 190 KB of source,
// most of it the static dictionary), so import it only where it is needed: the native decoders are far faster.
import type { G9FrameDecompressor } from '../protocol/rowBatch/reader.js';
import { brotliDecode } from './decode.js';

/** Decodes a raw Brotli stream. */
export function brotliDecompressJs(compressed: Uint8Array): Uint8Array {
  const input = new Int8Array(compressed.buffer, compressed.byteOffset, compressed.length);
  const output = brotliDecode(input) as Int8Array;
  return new Uint8Array(output.buffer, output.byteOffset, output.length);
}

/** A frame decompressor over the JavaScript decoder. */
export function jsBrotliDecompressor(): G9FrameDecompressor {
  return {
    decompress(compressed: Uint8Array, expectedLength: number): Uint8Array {
      const out = brotliDecompressJs(compressed);
      if (out.length !== expectedLength) throw new Error(`Brotli body decoded to ${out.length} bytes, ${expectedLength} expected.`);
      return out;
    },
  };
}
