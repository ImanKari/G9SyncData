package com.g9tm.syncdatalynx.core

import java.io.File
import java.io.RandomAccessFile
import java.security.MessageDigest

/**
 * The snapshot-file half of the module contract (types/g9-syncdata-lynx-module.d.ts, ADR-0072): the device's
 * G9SnapshotFileSink. A segment is decompressed with [G9BrotliCore] (exact length or an error, never a partial buffer),
 * checked against its SHA-256 and only then written at its offset; nothing reaches the file from a segment that fails.
 * Platform-free (java.io + java.security), tested on the JVM; every result is an envelope.
 */
object G9SnapshotFileCore {
  const val CODE_ARG = "G9SYNC-FILE-ARG"
  const val CODE_INVALID = "G9SYNC-FILE-INVALID"
  const val CODE_IO = "G9SYNC-FILE-IO"

  /** 64 MiB: the largest segment the contract accepts (the server cuts 1 MiB). */
  const val MAX_SEGMENT = 64 * 1024 * 1024

  private val HEX = Regex("^[0-9a-f]{64}$")

  fun create(path: String?, size: Double): Map<String, Any?> {
    if (path.isNullOrEmpty()) return G9Envelope.fail(CODE_ARG, "path is empty.")
    if (!isCount(size)) return G9Envelope.fail(CODE_ARG, "size must be a non-negative integer; got $size.")
    return io {
      val file = File(path)
      file.absoluteFile.parentFile?.mkdirs()
      RandomAccessFile(file, "rw").use { it.setLength(size.toLong()) }
      G9Envelope.ok(null)
    }
  }

  fun writeSegment(path: String?, offset: Double, data: ByteArray?, length: Double, sha256: String?, sync: Boolean): Map<String, Any?> {
    if (path.isNullOrEmpty()) return G9Envelope.fail(CODE_ARG, "path is empty.")
    if (!isCount(offset)) return G9Envelope.fail(CODE_ARG, "offset must be a non-negative integer; got $offset.")
    if (!isCount(length) || length <= 0 || length > MAX_SEGMENT) return G9Envelope.fail(CODE_ARG, "length must be an integer in [1, $MAX_SEGMENT]; got $length.")
    if (sha256 == null || !HEX.matches(sha256)) return G9Envelope.fail(CODE_ARG, "sha256 must be 64 lower-case hex characters.")
    if (data == null) return G9Envelope.fail(CODE_ARG, "data must be an ArrayBuffer.")

    val decoded = G9BrotliCore.decompress(data, length)
    if (decoded["ok"] != true) {
      val error = decoded["error"] as? Map<*, *>
      return G9Envelope.fail(CODE_INVALID, "The segment does not decompress to $length bytes: ${error?.get("message")}")
    }
    val raw = decoded["value"] as ByteArray
    if (hex(MessageDigest.getInstance("SHA-256").digest(raw)) != sha256) return G9Envelope.fail(CODE_INVALID, "The segment fails its SHA-256.")
    return io {
      RandomAccessFile(File(path), "rw").use { file ->
        file.seek(offset.toLong())
        file.write(raw)
        if (sync) file.fd.sync()
      }
      G9Envelope.ok(null)
    }
  }

  fun size(path: String?): Map<String, Any?> {
    if (path.isNullOrEmpty()) return G9Envelope.fail(CODE_ARG, "path is empty.")
    val file = File(path)
    return G9Envelope.ok(if (file.isFile) file.length().toDouble() else null)
  }

  fun head(path: String?, length: Double): Map<String, Any?> {
    if (path.isNullOrEmpty()) return G9Envelope.fail(CODE_ARG, "path is empty.")
    if (!isCount(length) || length > MAX_SEGMENT) return G9Envelope.fail(CODE_ARG, "length must be an integer in [0, $MAX_SEGMENT]; got $length.")
    return io {
      RandomAccessFile(File(path), "r").use { file ->
        val buffer = ByteArray(minOf(length.toLong(), file.length()).toInt())
        file.readFully(buffer)
        G9Envelope.ok(buffer)
      }
    }
  }

  fun remove(path: String?): Map<String, Any?> {
    if (path.isNullOrEmpty()) return G9Envelope.fail(CODE_ARG, "path is empty.")
    return io {
      val file = File(path)
      if (file.exists() && !file.delete()) throw java.io.IOException("The file could not be deleted.")
      G9Envelope.ok(null)
    }
  }

  fun list(directory: String?): Map<String, Any?> {
    if (directory.isNullOrEmpty()) return G9Envelope.fail(CODE_ARG, "directory is empty.")
    val files = File(directory).listFiles()?.filter { it.isFile }?.map { it.absolutePath }?.sorted() ?: emptyList()
    return G9Envelope.ok(files)
  }

  private fun isCount(value: Double): Boolean = !value.isNaN() && value >= 0 && value == Math.floor(value) && value <= 9.007199254740991E15

  private fun hex(bytes: ByteArray): String {
    val out = StringBuilder(bytes.size * 2)
    for (b in bytes) {
      val v = b.toInt() and 0xff
      out.append("0123456789abcdef"[v ushr 4]).append("0123456789abcdef"[v and 0x0f])
    }
    return out.toString()
  }

  private inline fun io(block: () -> Map<String, Any?>): Map<String, Any?> =
    try {
      block()
    } catch (error: Exception) {
      G9Envelope.fail(CODE_IO, "${error.javaClass.simpleName}: ${error.message}")
    }
}
