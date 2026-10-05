package com.g9tm.syncdatahost

import android.content.Context
import com.lynx.tasm.provider.AbsTemplateProvider
import java.io.IOException
import java.util.concurrent.Executors

/** Loads bundles packaged under assets/ (main.lynx.bundle) off the main thread. */
class AssetTemplateProvider(context: Context) : AbsTemplateProvider() {
  private val assets = context.applicationContext.assets
  private val io = Executors.newSingleThreadExecutor()

  override fun loadTemplate(uri: String, callback: Callback) {
    io.execute {
      try {
        callback.onSuccess(assets.open(uri).use { it.readBytes() })
      } catch (error: IOException) {
        callback.onFailed("Cannot load $uri: ${error.message}")
      }
    }
  }
}
