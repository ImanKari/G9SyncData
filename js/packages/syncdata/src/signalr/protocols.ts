// The two SignalR hub protocols of the sync hub, with the wire forms the .NET server reads and writes:
//  - JSON: SignalR's JSON protocol, except that int64 values stay exact both ways (reconciliation digests are full 64-bit
//    numbers; JSON.parse would round them) and byte arrays travel as base64, as System.Text.Json writes them.
//  - MessagePack: SignalR's MessagePack protocol (@microsoft/signalr-protocol-msgpack) with GUIDs as Nerdbank's extension
//    type 2 and int64 values exact (its decoder would round them; there is no BigInt on Lynx).
import { MessageType, TransferFormat, type HubMessage, type IHubProtocol, type ILogger } from '@microsoft/signalr';
import { MessagePackHubProtocol } from '@microsoft/signalr-protocol-msgpack';
import { ExtensionCodec } from '@msgpack/msgpack';
import { readGuidBytes, writeGuidBytes } from '../abstractions/guid.js';
import { G9Long, parseJson, replaceLongSentinels } from '../abstractions/long.js';
import { bytesToBase64 } from '../internal/base64.js';
import { G9WireGuid, GUID_EXTENSION_TYPE } from '../protocol/wire.js';

const RECORD_SEPARATOR = '\u001e';

/** SignalR's JSON hub protocol with exact int64 values (G9Long) and base64 byte arrays. */
export class G9SyncJsonHubProtocol implements IHubProtocol {
  readonly name = 'json';
  readonly version = 2;
  readonly transferFormat = TransferFormat.Text;

  parseMessages(input: string | ArrayBuffer, _logger: ILogger | null): HubMessage[] {
    if (typeof input !== 'string') throw new Error('Invalid input for JSON hub protocol. Expected a string.');
    if (!input) return [];
    if (input.charAt(input.length - 1) !== RECORD_SEPARATOR) throw new Error('Message is incomplete.');
    const messages = input.split(RECORD_SEPARATOR);
    messages.pop();
    const out: HubMessage[] = [];
    for (const text of messages) {
      const message = parseJson(text) as HubMessage;
      if (typeof message !== 'object' || message === null || typeof (message as { type?: unknown }).type !== 'number') throw new Error('Invalid payload.');
      out.push(message);
    }
    return out;
  }

  writeMessage(message: HubMessage): string {
    const text = JSON.stringify(message, (_key, value: unknown) => {
      if (value instanceof Uint8Array) return bytesToBase64(value);
      if (value instanceof G9WireGuid) return value.value;
      return value;
    });
    return replaceLongSentinels(text) + RECORD_SEPARATOR;
  }
}

/** The extension codec: GUIDs as Nerdbank's extension type 2 (16 big-endian bytes). */
function syncExtensionCodec(): ExtensionCodec {
  const codec = new ExtensionCodec();
  codec.register({
    type: GUID_EXTENSION_TYPE,
    encode: (value: unknown) => {
      if (!(value instanceof G9WireGuid)) return null;
      const bytes = new Uint8Array(16);
      writeGuidBytes(value.value, bytes, 0);
      return bytes;
    },
    decode: (data: Uint8Array) => new G9WireGuid(readGuidBytes(data, 0)),
  });
  return codec;
}

interface EncoderInternals {
  doEncode(object: unknown, depth: number): void;
  ensureBufferSizeToWrite(size: number): void;
  writeU8(value: number): void;
  writeI32(value: number): void;
  writeU32(value: number): void;
}

interface DecoderInternals {
  view: DataView;
  pos: number;
  readU64(): unknown;
  readI64(): unknown;
}

/**
 * SignalR's MessagePack hub protocol for the sync hub. Its encoder and decoder are @msgpack/msgpack instances the
 * protocol keeps privately; this sets their int64 paths so G9Long values are written exactly (int 64) and integers beyond
 * ±(2^53 − 1) are read as G9Long. A protocol build whose internals moved fails here, at creation, never silently.
 */
export function createSyncMessagePackProtocol(): IHubProtocol {
  const protocol = new MessagePackHubProtocol({ extensionCodec: syncExtensionCodec() });
  const internals = protocol as unknown as { _encoder?: EncoderInternals; _decoder?: DecoderInternals };
  const encoder = internals._encoder;
  const decoder = internals._decoder;
  if (!encoder || typeof encoder.doEncode !== 'function' || !decoder || typeof decoder.readI64 !== 'function') {
    throw new Error('@microsoft/signalr-protocol-msgpack changed its internals: the sync protocol cannot keep int64 values exact.');
  }
  const doEncode = encoder.doEncode;
  encoder.doEncode = function (this: EncoderInternals, object: unknown, depth: number): void {
    if (object instanceof G9Long) {
      this.ensureBufferSizeToWrite(9);
      this.writeU8(0xd3);
      this.writeI32(object.hi);
      this.writeU32(object.lo);
      return;
    }
    doEncode.call(this, object, depth);
  };
  decoder.readU64 = function (this: DecoderInternals): unknown {
    const hi = this.view.getUint32(this.pos);
    const lo = this.view.getUint32(this.pos + 4);
    this.pos += 8;
    // An unsigned value above int64's range does not occur on this wire; it is read as its bits.
    const value = new G9Long(hi, lo);
    return hi < 0x200000 ? value.toNumber() : value;
  };
  decoder.readI64 = function (this: DecoderInternals): unknown {
    const hi = this.view.getInt32(this.pos);
    const lo = this.view.getUint32(this.pos + 4);
    this.pos += 8;
    const value = new G9Long(hi, lo);
    return value.isSafe() ? value.toNumber() : value;
  };
  return protocol;
}

/** Message types the transport looks at (re-exported so callers need not import @microsoft/signalr). */
export { MessageType };
