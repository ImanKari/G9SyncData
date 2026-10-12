/// <reference types="node" />
// The Node implementation of G9SnapshotFileSink (ADR-0072): Brotli through node:zlib, SHA-256 through node:crypto, positional
// writes through node:fs. The Lynx package implements the same contract natively; the store never sees the bytes of a file.
import { createHash } from 'node:crypto';
import { closeSync, fsyncSync, mkdirSync, openSync, readdirSync, readSync, rmSync, statSync, writeSync, ftruncateSync } from 'node:fs';
import { join } from 'node:path';
import { brotliDecompressSync } from 'node:zlib';
import type { G9SnapshotFileSink } from '../sqlite/options.js';

/** A snapshot-file sink writing into `directory` (created when missing). */
export function nodeSnapshotFileSink(directory: string): G9SnapshotFileSink {
  return {
    directory,

    async create(path: string, size: number): Promise<void> {
      mkdirSync(directory, { recursive: true });
      const fd = openSync(path, 'w');
      try {
        ftruncateSync(fd, size);
      } finally {
        closeSync(fd);
      }
    },

    async writeSegment(path: string, offset: number, compressed: Uint8Array, length: number, sha256: string, sync: boolean): Promise<void> {
      const raw = brotliDecompressSync(compressed, { maxOutputLength: Math.max(1, length + 1) });
      if (raw.length !== length) throw new Error(`The segment decompressed to ${raw.length} bytes, ${length} expected.`);
      const hash = createHash('sha256').update(raw).digest('hex');
      if (hash !== sha256) throw new Error('The segment fails its SHA-256.');
      const fd = openSync(path, 'r+');
      try {
        let written = 0;
        while (written < raw.length) written += writeSync(fd, raw, written, raw.length - written, offset + written);
        if (sync) fsyncSync(fd);
      } finally {
        closeSync(fd);
      }
    },

    async size(path: string): Promise<number | null> {
      try {
        return statSync(path).size;
      } catch {
        return null;
      }
    },

    async head(path: string, length: number): Promise<Uint8Array> {
      const out = new Uint8Array(length);
      const fd = openSync(path, 'r');
      try {
        let read = 0;
        while (read < length) {
          const n = readSync(fd, out, read, length - read, read);
          if (n === 0) break;
          read += n;
        }
        return read === length ? out : out.subarray(0, read);
      } finally {
        closeSync(fd);
      }
    },

    async remove(path: string): Promise<void> {
      rmSync(path, { force: true });
    },

    async list(target: string): Promise<string[]> {
      try {
        return readdirSync(target).map((name) => join(target, name));
      } catch {
        return [];
      }
    },
  };
}
