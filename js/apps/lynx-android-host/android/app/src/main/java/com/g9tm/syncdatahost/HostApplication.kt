package com.g9tm.syncdatahost

import android.app.Application
import android.util.Log
import com.lynx.service.http.LynxHttpService
import com.lynx.service.log.LynxLogService
import com.lynx.tasm.LynxEnv
import com.lynx.tasm.library.LynxAutolinkGenerated
import com.lynx.tasm.service.LynxServiceCenter

/**
 * Lynx is initialised once per process: the log service (console.* → logcat), the HTTP service (Lynx's `fetch`),
 * `LynxEnv`, then the autolinked libraries' global registrations — G9SyncDataLynxModule, G9SignalRLynxModule,
 * G9SqliteModule and G9HostModule.
 */
class HostApplication : Application() {
  override fun onCreate() {
    super.onCreate()
    LynxServiceCenter.inst().registerService(LynxLogService)
    LynxServiceCenter.inst().registerService(LynxHttpService)
    LynxEnv.inst().init(this, null, null, null)
    LynxAutolinkGenerated.setupGlobal(this)
    Log.i(TAG, "Lynx ${LynxEnv.inst().lynxVersion} (artifact ${BuildConfig.LYNX_VERSION}), PrimJS ${BuildConfig.PRIMJS_VERSION}")
  }

  companion object {
    const val TAG = "G9SYNC-DEVICE"
  }
}
