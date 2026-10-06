// Device test host of @g9tm/syncdata + @g9tm/syncdata-lynx (run: ../scripts/device-test.mjs). Lynx Autolink links the three native
// libraries from this app's node_modules: @g9tm/syncdata-lynx (G9SyncDataLynxModule, Brotli),
// @g9tm/signalr-supernetcore-lynx (G9SignalRLynxModule, WebSocket) and @g9tm/lynxcontrols-native (G9SqliteModule, G9HostModule).
pluginManagement {
  repositories {
    google()
    mavenCentral()
    gradlePluginPortal()
  }
}

plugins { id("org.lynxsdk.lynx.library-settings") version "4.1.0" }

dependencyResolutionManagement {
  repositories {
    google()
    mavenCentral()
  }
}

rootProject.name = "g9syncdata-lynx-android-host"

include(":app")

// The Autolink scanner walks SIX directories up from here and collects every node_modules it meets, without
// de-duplicating by package: from js/apps/lynx-android-host/android it also reaches js/node_modules (the npm workspace,
// whose @g9tm/syncdata-lynx is a link to packages/lynx and whose @g9tm/signalr-supernetcore-lynx is the vendor
// tarball). The last copy found would win the project directory, so the device test would build the workspace sources,
// not the packed packages. Keep only the libraries installed in THIS app's node_modules.
val ownNodeModules: File = file("../node_modules").canonicalFile
fun autolinkProperty(library: Any, name: String): Any? = library.javaClass.getMethod(name).invoke(library)

@Suppress("UNCHECKED_CAST")
val autolinked = extensions.extraProperties["lynxAutolinkLibraries"] as MutableList<Any>
autolinked.removeIf { library ->
  val androidDir = (autolinkProperty(library, "getAndroidDir") as File).canonicalFile
  !androidDir.path.startsWith(ownNodeModules.path + File.separator)
}
for (library in autolinked) {
  project(autolinkProperty(library, "getProjectPath") as String).projectDir = autolinkProperty(library, "getAndroidDir") as File
}
