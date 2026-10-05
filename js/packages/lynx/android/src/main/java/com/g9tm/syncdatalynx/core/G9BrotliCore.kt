package com.g9tm.syncdatalynx.core

import org.brotli.dec.BrotliInputStream
import java.io.ByteArrayInputStream

/**
 * The Brotli half of the module contract (types/g9-syncdata-lynx-module.d.ts): a raw Brotli stream (what the .NET
 * server's `BrotliEncoder` writes, quality 4 / window 22) in, EXACTLY `expectedLength` bytes out — or an error
 * envelope, never a partial buffer. Output is bounded: the buffer is `expectedLength` bytes and decoding stops at the
 * first byte past it. Platform-free (org.brotli:dec, Google's pure-Java decoder), tested on the JVM.
 */
object G9BrotliCore {
  /** 576 MiB: the largest output the contract accepts. */
  const val MAX_EXPECTED_LENGTH = 603_979_776

  const val CODE_ARG = "G9SYNC-BROTLI-ARG"
  const val CODE_INVALID = "G9SYNC-BROTLI-INVALID"
  const val CODE_UNAVAILABLE = "G9SYNC-BROTLI-UNAVAILABLE"

  /** `null` when the arguments are usable, else the `G9SYNC-BROTLI-ARG` envelope. */
  fun validate(data: ByteArray?, expectedLength: Double): Map<String, Any?>? {
    if (data == null) return G9Envelope.fail(CODE_ARG, "data must be an ArrayBuffer.")
    if (expectedLength.isNaN() ||
      expectedLength < 0 ||
      expectedLength > MAX_EXPECTED_LENGTH ||
      expectedLength != Math.floor(expectedLength)
    ) {
      return G9Envelope.fail(CODE_ARG, "expectedLength must be an integer in [0, $MAX_EXPECTED_LENGTH]; got $expectedLength.")
    }
    return null
  }

  /** `{ ok: true, value: ByteArray(expectedLength) }`, or `G9SYNC-BROTLI-ARG` / `G9SYNC-BROTLI-INVALID`. */
  fun decompress(data: ByteArray?, expectedLength: Double): Map<String, Any?> {
    validate(data, expectedLength)?.let { return it }
    val expected = expectedLength.toInt()
    return try {
      decode(data!!, expected)
    } catch (error: OutOfMemoryError) {
      invalid("Out of memory while decoding $expected bytes.")
    }
  }

  private fun decode(data: ByteArray, expected: Int): Map<String, Any?> {
    val output = ByteArray(expected)
    val source = ByteArrayInputStream(data)
    try {
      BrotliInputStream(source).use { input ->
        var filled = 0
        while (filled < expected) {
          val read = input.read(output, filled, expected - filled)
          if (read <= 0) break
          filled += read
        }
        if (filled < expected) return invalid("The Brotli stream ends after $filled bytes; $expected were expected.")
        // One byte more tells "exactly expected" from "longer" (and runs the decoder's end-of-stream checks: missing
        // final block, unused bytes in its read buffer). Nothing past it is ever decoded.
        if (input.read(ByteArray(1), 0, 1) > 0) return invalid("The Brotli stream holds more than the $expected bytes expected.")
        // The decoder reads its input in 4 KiB chunks and checks only what it has read: input it never asked for
        // after the final block is trailing data too.
        val unread = source.available()
        if (unread > 0) return invalid("$unread bytes follow the end of the Brotli stream.")
      }
    } catch (error: Exception) {
      return invalid("The Brotli stream is invalid: ${describe(error)}")
    }
    return G9Envelope.ok(output)
  }

  private fun invalid(message: String): Map<String, Any?> = G9Envelope.fail(CODE_INVALID, message)

  /** The decoder wraps its own `BrotliRuntimeException` in an IOException; the cause says what was wrong. */
  private fun describe(error: Throwable): String {
    val cause = error.cause
    val own = error.message ?: error.javaClass.simpleName
    return if (cause != null && cause !== error) "$own (${cause.message ?: cause.javaClass.simpleName})" else own
  }
}
