/// <reference types="node" />
// @g9tm/syncdata/node — Node helpers: the node:sqlite reference driver and Brotli through node:zlib (the same
// quality 4 / window 22 the .NET writer uses).
import { brotliCompressSync, brotliDecompressSync, constants } from 'node:zlib';
import { setDefaultFrameDecompressor, type G9FrameDecompressor } from '../protocol/rowBatch/reader.js';
import type { G9FrameCompressor } from '../protocol/rowBatch/writer.js';

export { nodeSqliteDriver, G9NodeSqliteError } from './sqliteDriver.js';

/** A frame decompressor over node:zlib. */
export function nodeBrotliDecompressor(): G9FrameDecompressor {
  return {
    decompress(compressed: Uint8Array, expectedLength: number): Uint8Array {
      const out = brotliDecompressSync(compressed, { maxOutputLength: Math.max(1, expectedLength + 1) });
      if (out.length !== expectedLength) throw new Error(`Brotli body decoded to ${out.length} bytes, ${expectedLength} expected.`);
      return new Uint8Array(out.buffer, out.byteOffset, out.length);
    },
  };
}

/** A push-frame compressor over node:zlib. */
export function nodeBrotliCompressor(): G9FrameCompressor {
  return {
    compress(body: Uint8Array, quality: number, window: number): Uint8Array {
      const out = brotliCompressSync(body, {
        params: { [constants.BROTLI_PARAM_QUALITY]: quality, [constants.BROTLI_PARAM_LGWIN]: window, [constants.BROTLI_PARAM_SIZE_HINT]: body.length },
      });
      return new Uint8Array(out.buffer, out.byteOffset, out.length);
    },
  };
}

/** Makes node:zlib the process-wide frame decompressor (setDefaultFrameDecompressor). */
export function useNodeBrotli(): G9FrameDecompressor {
  const decompressor = nodeBrotliDecompressor();
  setDefaultFrameDecompressor(decompressor);
  return decompressor;
}
