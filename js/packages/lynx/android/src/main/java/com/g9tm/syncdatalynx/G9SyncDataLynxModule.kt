package com.g9tm.syncdatalynx

import com.g9tm.syncdatalynx.core.G9BrotliCore
import com.g9tm.syncdatalynx.core.G9Envelope
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
 * frames for @g9tm/syncdata-lynx. Decoding runs on a small process-wide pool (two daemon threads, shared by every
 * LynxView), never on the Lynx JS thread; the reply goes through Lynx's callback from that thread, like
 * @g9tm/signalr-supernetcore-lynx's file operations. Argument errors reply at once.
 */
@LynxNativeModule(name = "G9SyncDataLynxModule")
class G9SyncDataLynxModule(context: LynxContext) : G9SyncDataLynxModuleSpec(context) {
  companion object {
    const val VERSION = "1.2.1"

    private val decoders: ExecutorService by lazy {
      val count = AtomicInteger()
      Executors.newFixedThreadPool(2) { runnable ->
        Thread(runnable, "g9-syncdata-brotli-${count.incrementAndGet()}").apply { isDaemon = true }
      }
    }
  }

  @LynxMethod
  override fun capabilities(callback: Callback?) =
    G9Bridge.reply(callback, G9Envelope.ok(mapOf("version" to VERSION, "platform" to "android", "brotli" to true)))

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
}
