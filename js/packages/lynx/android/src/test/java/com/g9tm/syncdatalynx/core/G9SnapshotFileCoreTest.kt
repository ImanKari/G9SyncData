package com.g9tm.syncdatalynx.core

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Test
import java.io.File
import java.nio.file.Files
import java.security.MessageDigest

/**
 * The snapshot-file half of the module contract (ADR-0072) against the shared Brotli vectors: a segment is written only
 * when it decompresses to exactly its length AND matches its SHA-256; a bad one leaves the file as it was.
 */
class G9SnapshotFileCoreTest {
  private fun resource(name: String): ByteArray =
    G9SnapshotFileCoreTest::class.java.getResourceAsStream("/brotli/$name")?.use { it.readBytes() }
      ?: throw AssertionError("missing test vector brotli/$name")

  /** The first vector of the shared manifest: its stream, decoded length and SHA-256. */
  private val vector: Triple<ByteArray, Int, String> by lazy {
    val line = String(resource("manifest.txt"), Charsets.UTF_8).lines().map { it.trim() }.first { it.isNotEmpty() && !it.startsWith("#") && it.split(Regex("\\s+"))[1].toInt() > 0 }
    val parts = line.split(Regex("\\s+"))
    Triple(resource(parts[0]), parts[1].toInt(), parts[2])
  }

  private fun code(reply: Map<String, Any?>): Any? = (reply["error"] as Map<*, *>?)?.get("code")

  @Test
  fun a_segment_is_decompressed_checked_and_written_at_its_offset() {
    val (stream, length, sha) = vector
    val directory = Files.createTempDirectory("g9-snapshot").toFile()
    val path = File(directory, "sub/file.sqlite").absolutePath
    val offset = 4096
    assertEquals(true, G9SnapshotFileCore.create(path, (offset + length + 100).toDouble())["ok"])
    val reply = G9SnapshotFileCore.writeSegment(path, offset.toDouble(), stream, length.toDouble(), sha, sync = true)
    assertEquals(reply.toString(), true, reply["ok"])

    val bytes = File(path).readBytes()
    assertEquals(offset + length + 100, bytes.size)
    val written = bytes.copyOfRange(offset, offset + length)
    assertEquals(sha, MessageDigest.getInstance("SHA-256").digest(written).joinToString("") { "%02x".format(it) })
    assertEquals((offset + length + 100).toDouble(), G9SnapshotFileCore.size(path)["value"])
    assertArrayEquals(bytes.copyOfRange(0, 16), G9SnapshotFileCore.head(path, 16.0)["value"] as ByteArray)
    assertEquals(listOf(File(directory, "sub/file.sqlite").absolutePath), G9SnapshotFileCore.list(File(directory, "sub").absolutePath)["value"])
    assertEquals(true, G9SnapshotFileCore.remove(path)["ok"])
    assertEquals(null, G9SnapshotFileCore.size(path)["value"])
    directory.deleteRecursively()
  }

  @Test
  fun a_segment_that_fails_its_hash_or_length_writes_nothing() {
    val (stream, length, sha) = vector
    val directory = Files.createTempDirectory("g9-snapshot").toFile()
    val path = File(directory, "file.sqlite").absolutePath
    G9SnapshotFileCore.create(path, length.toDouble())
    val before = File(path).readBytes()

    assertEquals(G9SnapshotFileCore.CODE_INVALID, code(G9SnapshotFileCore.writeSegment(path, 0.0, stream, length.toDouble(), "0".repeat(64), sync = false)))
    assertEquals(G9SnapshotFileCore.CODE_INVALID, code(G9SnapshotFileCore.writeSegment(path, 0.0, stream, (length + 1).toDouble(), sha, sync = false)))
    assertArrayEquals(before, File(path).readBytes())

    assertEquals(G9SnapshotFileCore.CODE_ARG, code(G9SnapshotFileCore.writeSegment(path, -1.0, stream, length.toDouble(), sha, sync = false)))
    assertEquals(G9SnapshotFileCore.CODE_ARG, code(G9SnapshotFileCore.writeSegment(path, 0.0, stream, length.toDouble(), "ABC", sync = false)))
    assertEquals(G9SnapshotFileCore.CODE_ARG, code(G9SnapshotFileCore.writeSegment("", 0.0, stream, length.toDouble(), sha, sync = false)))
    directory.deleteRecursively()
  }
}
