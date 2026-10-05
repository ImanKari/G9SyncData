// Loads js/packages/syncdata/test/golden/dotnet-vectors.json, written by the .NET test
// tests/G9SyncData.Tests/Protocol/JsGoldenVectorsTests.cs (G9_UPDATE_GOLDEN=1 regenerates it).
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

export const goldenDirectory = dirname(fileURLToPath(import.meta.url));

// eslint-disable-next-line @typescript-eslint/no-explicit-any
export type Json = any;

export const vectors: Json = JSON.parse(readFileSync(join(goldenDirectory, 'dotnet-vectors.json'), 'utf8'));

export function hexToBytes(hex: string): Uint8Array {
  const out = new Uint8Array(hex.length / 2);
  for (let i = 0; i < out.length; i++) out[i] = parseInt(hex.substr(i * 2, 2), 16);
  return out;
}

export function bytesToHex(bytes: Uint8Array): string {
  let out = '';
  for (const b of bytes) out += (b < 16 ? '0' : '') + b.toString(16);
  return out;
}

/** A double from the 16 hex digits of its IEEE bits (as .NET wrote them). */
export function doubleFromBits(bits: string): number {
  const view = new DataView(new ArrayBuffer(8));
  view.setUint32(0, parseInt(bits.slice(0, 8), 16));
  view.setUint32(4, parseInt(bits.slice(8), 16));
  return view.getFloat64(0);
}

export function doubleToBits(value: number): string {
  const view = new DataView(new ArrayBuffer(8));
  view.setFloat64(0, value);
  return view.getUint32(0).toString(16).padStart(8, '0') + view.getUint32(4).toString(16).padStart(8, '0');
}
