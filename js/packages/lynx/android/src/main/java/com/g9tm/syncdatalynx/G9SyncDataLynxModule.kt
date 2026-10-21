package com.g9tm.syncdatalynx

import com.g9tm.syncdatalynx.core.G9BrotliCore
import com.g9tm.syncdatalynx.core.G9Envelope
import com.g9tm.syncdatalynx.core.G9SnapshotFileCore
import com.g9tm.syncdatalynx.generated.G9SyncDataLynxModuleSpec
import com.lynx.jsbridge.LynxMethod
import com.lynx.jsbridge.LynxNativeModule
import com.lynx.react.bridge.Callback
import com.lynx.tasm.behavior.LynxContext
import java.util.concurrent.ExecutorService
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicInteger

/**
 * `NativeModules.G9SyncDataLynxModule` (types/g9-syncdata-lynx-module.d.ts): Brotli decompression of the G9SyncData
 * frames for @g9tm/syncdata-lynx, and the snapshot-file sink (ADR-0072: a downloaded segment is decompressed, checked
 * against its SHA-256 and written into the staged file here, never in JavaScript). Decoding runs on a small
 * process-wide pool (two daemon threads, shared by every LynxView), file work on one daemon thread, never on the Lynx JS
 * thread; the reply goes through Lynx's callback from that thread, like @g9tm/signalr-supernetcore-lynx's file
 * operations. Brotli argument errors reply at once.
 */
@LynxNativeModule(name = "G9SyncDataLynxModule")
class G9SyncDataLynxModule(context: LynxContext) : G9SyncDataLynxModuleSpec(context) {
  /** Where staged snapshot files belong: app-private, outside Auto Backup, reclaimable by the OS. */
  private val cacheDirectory: String? by lazy { runCatching { context.cacheDir?.absolutePath }.getOrNull() }

  companion object {
    const val VERSION = "1.5.4"

    private val decoders: ExecutorService by lazy {
      val count = AtomicInteger()
      Executors.newFixedThreadPool(2) { runnable ->
        Thread(runnable, "g9-syncdata-brotli-${count.incrementAndGet()}").apply { isDaemon = true }
      }
    }

    /**
     * Snapshot-file work (ADR-0072) on its own thread: a segment's decode + SHA-256 + write never waits behind a frame
     * decode, and the writes of one file land in the order the sink sent them.
     */
    private val files: ExecutorService by lazy {
      Executors.newSingleThreadExecutor { runnable -> Thread(runnable, "g9-syncdata-files").apply { isDaemon = true } }
    }
  }

  @LynxMethod
  override fun capabilities(callback: Callback?) =
    G9Bridge.reply(
      callback,
      G9Envelope.ok(
        mapOf(
          "version" to VERSION,
          "platform" to "android",
          "brotli" to true,
          "snapshotFiles" to true,
          "cacheDirectory" to cacheDirectory,
        ),
      ),
    )

  @LynxMethod
  override fun brotliDecompress(data: ByteArray?, expectedLength: Double, callback: Callback?) {
    val bytes = data
    val invalid = G9BrotliCore.validate(bytes, expectedLength)
    if (invalid != null) return G9Bridge.reply(callback, invalid)
    decoders.execute {
      val envelope =
        try {
          G9BrotliCore.decompress(bytes, expectedLength)
        } catch (error: Throwable) {
          // A pool thread must never die with an uncaught throwable (that ends the app): reply instead.
          G9Envelope.fail(G9BrotliCore.CODE_INVALID, "Brotli decoding failed: ${error.message ?: error.javaClass.simpleName}")
        }
      G9Bridge.reply(callback, envelope)
    }
  }

  // ---------------------------------------------------------------- snapshot files (ADR-0072)

  @LynxMethod
  override fun snapshotFileCreate(path: String?, size: Double, callback: Callback?) = offThread(callback) { G9SnapshotFileCore.create(path, size) }

  @LynxMethod
  override fun snapshotFileWriteSegment(path: String?, offset: Double, data: ByteArray?, length: Double, sha256: String?, sync: Boolean, callback: Callback?) =
    offThread(callback) { G9SnapshotFileCore.writeSegment(path, offset, data, length, sha256, sync) }

  @LynxMethod
  override fun snapshotFileSize(path: String?, callback: Callback?) = offThread(callback) { G9SnapshotFileCore.size(path) }

  @LynxMethod
  override fun snapshotFileHead(path: String?, length: Double, callback: Callback?) = offThread(callback) { G9SnapshotFileCore.head(path, length) }

  @LynxMethod
  override fun snapshotFileRemove(path: String?, callback: Callback?) = offThread(callback) { G9SnapshotFileCore.remove(path) }

  @LynxMethod
  override fun snapshotFileList(directory: String?, callback: Callback?) = offThread(callback) { G9SnapshotFileCore.list(directory) }

  /** File work runs on the file thread, never on the Lynx JS thread; that thread never dies with a throwable. */
  private fun offThread(callback: Callback?, work: () -> Map<String, Any?>) {
    files.execute {
      val envelope =
        try {
          work()
        } catch (error: Throwable) {
          G9Envelope.fail(G9SnapshotFileCore.CODE_IO, "${error.javaClass.simpleName}: ${error.message}")
        }
      G9Bridge.reply(callback, envelope)
    }
  }
}
