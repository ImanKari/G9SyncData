package com.g9tm.syncdatalynx.core

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test
import java.security.MessageDigest
import java.util.concurrent.Callable
import java.util.concurrent.Executors
import java.util.zip.CRC32

/**
 * The Brotli half of the module contract against the shared test vectors (../test/fixtures/brotli, written by
 * scripts/make-brotli-fixtures.mjs: quality 4, window 22, like the .NET server). The iOS core's GNUstep suite and the
 * Lynxtron bridge's vitest suite decode the same files and derive the same broken streams.
 */
class G9BrotliCoreTest {
  private class Fixture(val name: String, val length: Int, val sha256: String, val crc32: String) {
    val stream: ByteArray by lazy { resource(name) }
  }

  companion object {
    private fun resource(name: String): ByteArray =
      G9BrotliCoreTest::class.java.getResourceAsStream("/brotli/$name")?.use { it.readBytes() }
        ?: throw AssertionError("missing test vector brotli/$name (resources from ../test/fixtures)")

    private val fixtures: List<Fixture> by lazy {
      String(resource("manifest.txt"), Charsets.UTF_8)
        .lines()
        .map { it.trim() }
        .filter { it.isNotEmpty() && !it.startsWith("#") }
        .map { line ->
          val parts = line.split(Regex("\\s+"))
          Fixture(parts[0], parts[1].toInt(), parts[2], parts[3])
        }
    }

    private fun fixture(name: String): Fixture = fixtures.first { it.name == name }

    private fun hex(bytes: ByteArray): String = bytes.joinToString("") { "%02x".format(it) }
  }

  private fun value(reply: Map<String, Any?>): ByteArray {
    assertEquals(reply.toString(), true, reply["ok"])
    return reply["value"] as ByteArray
  }

  /** The error code; also asserts that a failure never carries a (partial) value. */
  private fun code(reply: Map<String, Any?>): Any? {
    assertEquals(reply.toString(), false, reply["ok"])
    assertNull(reply["value"])
    return (reply["error"] as Map<*, *>)["code"]
  }

  private fun decompress(data: ByteArray?, expectedLength: Number): Map<String, Any?> =
    G9BrotliCore.decompress(data, expectedLength.toDouble())

  @Test
  fun everyVectorDecodesToExactlyItsOriginal() {
    assertEquals(4, fixtures.size)
    for (f in fixtures) {
      val out = value(decompress(f.stream, f.length))
      assertEquals(f.name, f.length, out.size)
      assertEquals(f.name, f.sha256, hex(MessageDigest.getInstance("SHA-256").digest(out)))
      assertEquals(f.name, f.crc32, "%08x".format(CRC32().apply { update(out) }.value))
    }
    assertArrayEquals(byteArrayOf(0x2a), value(decompress(fixture("one-byte.br").stream, 1)))
    assertArrayEquals(ByteArray(0), value(decompress(fixture("empty.br").stream, 0)))
  }

  @Test
  fun anOutputLengthOtherThanExpectedIsInvalid() {
    val mixed = fixture("mixed-300k.br")
    assertEquals("G9SYNC-BROTLI-INVALID", code(decompress(mixed.stream, mixed.length - 1))) // longer than expected
    assertEquals("G9SYNC-BROTLI-INVALID", code(decompress(mixed.stream, mixed.length + 1))) // shorter than expected
    assertEquals("G9SYNC-BROTLI-INVALID", code(decompress(fixture("one-byte.br").stream, 0)))
    assertEquals("G9SYNC-BROTLI-INVALID", code(decompress(fixture("empty.br").stream, 1)))
  }

  @Test
  fun corruptStreamsAreInvalid() {
    val mixed = fixture("mixed-300k.br")
    val flippedHead = mixed.stream.copyOf().also { it[2] = (it[2].toInt() xor 0xff).toByte() }
    assertEquals("G9SYNC-BROTLI-INVALID", code(decompress(flippedHead, mixed.length)))
    assertEquals("G9SYNC-BROTLI-INVALID", code(decompress("G9SyncData: this is not a Brotli stream.".toByteArray(Charsets.ISO_8859_1), 40)))
    assertEquals("G9SYNC-BROTLI-INVALID", code(decompress(ByteArray(16) { 0xff.toByte() }, 0)))
    assertEquals("G9SYNC-BROTLI-INVALID", code(decompress(ByteArray(0), 0))) // no stream at all, not even the header
  }

  @Test
  fun truncatedStreamsAreInvalid() {
    val mixed = fixture("mixed-300k.br")
    assertEquals("G9SYNC-BROTLI-INVALID", code(decompress(mixed.stream.copyOf(mixed.stream.size / 2), mixed.length)))
    assertEquals("G9SYNC-BROTLI-INVALID", code(decompress(mixed.stream.copyOf(mixed.stream.size - 1), mixed.length)))
  }

  @Test
  fun bytesAfterTheEndOfTheStreamAreInvalid() {
    val one = fixture("one-byte.br").stream
    assertEquals("G9SYNC-BROTLI-INVALID", code(decompress(one + byteArrayOf(0, 1), 1)))
    // Inside the decoder's read buffer (its own check) and beyond it (input the decoder never read).
    val mixed = fixture("mixed-300k.br")
    for (extra in listOf(1, 100, 5000, 70_000)) {
      assertEquals("$extra", "G9SYNC-BROTLI-INVALID", code(decompress(mixed.stream + ByteArray(extra), mixed.length)))
    }
  }

  @Test
  fun argumentsAreChecked() {
    val one = fixture("one-byte.br").stream
    assertEquals("G9SYNC-BROTLI-ARG", code(decompress(null, 1)))
    for (bad in listOf(-1.0, 1.5, Double.NaN, Double.POSITIVE_INFINITY, Double.NEGATIVE_INFINITY, 603_979_777.0)) {
      assertEquals("$bad", "G9SYNC-BROTLI-ARG", code(G9BrotliCore.decompress(one, bad)))
    }
    // The bounds themselves are accepted (validated only: 576 MiB is not allocated here).
    assertNull(G9BrotliCore.validate(one, 0.0))
    assertNull(G9BrotliCore.validate(one, -0.0))
    assertNull(G9BrotliCore.validate(one, 603_979_776.0))
    assertEquals(603_979_776, G9BrotliCore.MAX_EXPECTED_LENGTH)
  }

  @Test
  fun concurrentDecodesShareNothing() {
    val mixed = fixture("mixed-300k.br")
    val text = fixture("text-32k.br")
    val pool = Executors.newFixedThreadPool(4)
    try {
      val jobs = (0 until 8).map { i ->
        val f = if (i % 2 == 0) mixed else text
        pool.submit(Callable { f to decompress(f.stream, f.length) })
      }
      for (job in jobs) {
        val (f, reply) = job.get()
        assertEquals(f.sha256, hex(MessageDigest.getInstance("SHA-256").digest(value(reply))))
      }
    } finally {
      pool.shutdownNow()
    }
  }
}
