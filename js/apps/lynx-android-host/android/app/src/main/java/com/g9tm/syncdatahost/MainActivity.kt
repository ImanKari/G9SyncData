package com.g9tm.syncdatahost

import android.app.Activity
import android.os.Build
import android.os.Bundle
import android.util.Log
import com.lynx.tasm.LynxEnv
import com.lynx.tasm.LynxView
import com.lynx.tasm.LynxViewBuilder
import com.lynx.tasm.library.LynxAutolinkGenerated

/**
 * One LynxView running the device test bundle. The run is configured by intent extras, handed to the page as global
 * props (`lynx.__globalProps`):
 *
 *   adb shell am start -S -n com.g9tm.syncdatahost/.MainActivity \
 *     --es server http://10.0.2.2:5210 --es protocol messagepack --es runId r1 [--es files on]
 *
 * `files on` lets the devices take their first downloads as snapshot files (ADR-0072) through the module's sink. The
 * test's SQLite files (`files/g9sync-*.db*`) and staged snapshot files (`cache/g9sync-files`) of a previous run are
 * deleted first, so every run starts empty.
 */
class MainActivity : Activity() {
  private var lynxView: LynxView? = null

  override fun onCreate(savedInstanceState: Bundle?) {
    super.onCreate(savedInstanceState)
    val server = intent.getStringExtra("server") ?: "http://10.0.2.2:5210"
    val protocol = intent.getStringExtra("protocol") ?: "messagepack"
    val runId = intent.getStringExtra("runId") ?: System.currentTimeMillis().toString(36)
    val files = intent.getStringExtra("files") ?: "off"
    // mode farm: a real farm's first download from the host test's server (src/farmTest.ts); hub = its hub URL.
    val mode = intent.getStringExtra("mode") ?: "interop"
    val hub = intent.getStringExtra("hub") ?: ""

    val stale = filesDir.listFiles { file -> file.name.startsWith("g9sync-") }.orEmpty()
    for (file in stale) file.delete()
    java.io.File(cacheDir, "g9sync-files").deleteRecursively()
    Log.i(HostApplication.TAG, "start server=$server protocol=$protocol files=$files runId=$runId (deleted ${stale.size} old test files)")

    val builder = LynxViewBuilder().setTemplateProvider(AssetTemplateProvider(this))
    LynxAutolinkGenerated.setup(builder)
    val view = builder.build(this)
    lynxView = view
    view.setGlobalProps(
      mapOf<String, Any>(
        "g9Server" to server,
        "g9Protocol" to protocol,
        "g9RunId" to runId,
        "g9Files" to files,
        "g9Mode" to mode,
        "g9Hub" to hub,
        "g9LynxVersion" to "${LynxEnv.inst().lynxVersion} (org.lynxsdk.lynx:lynx:${BuildConfig.LYNX_VERSION})",
        "g9PrimjsVersion" to BuildConfig.PRIMJS_VERSION,
        "g9Device" to "${Build.MANUFACTURER} ${Build.MODEL}, Android ${Build.VERSION.RELEASE} (API ${Build.VERSION.SDK_INT}), ${Build.SUPPORTED_ABIS.joinToString("/")}",
      ),
    )
    setContentView(view)
    view.renderTemplateUrl("main.lynx.bundle", "")
  }

  override fun onResume() {
    super.onResume()
    lynxView?.onEnterForeground()
  }

  override fun onPause() {
    lynxView?.onEnterBackground()
    super.onPause()
  }

  override fun onDestroy() {
    lynxView?.destroy()
    lynxView = null
    super.onDestroy()
  }
}
