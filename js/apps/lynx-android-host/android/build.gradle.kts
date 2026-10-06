// Versions for the app and the autolinked libraries, as in G9LynxControls' apps/android-host: AGP 8.13, Kotlin 2.3.20
// (kapt for the libraries' lynx-processor). The Lynx build plugin adds every autolinked library to the app and generates
// com.lynx.tasm.library.LynxAutolinkGenerated.
import org.jetbrains.kotlin.gradle.dsl.JvmTarget
import org.jetbrains.kotlin.gradle.dsl.KotlinAndroidProjectExtension
import org.jetbrains.kotlin.gradle.tasks.KotlinJvmCompile

plugins {
  id("com.android.application") version "8.13.0" apply false
  id("com.android.library") version "8.13.0" apply false
  id("org.jetbrains.kotlin.android") version "2.3.20" apply false
  id("org.jetbrains.kotlin.kapt") version "2.3.20" apply false
  // No version: the settings plugin (same jar, lynx-library-plugin 4.1.0) already put it on the classpath, and a
  // version here fails with "already on the classpath with an unknown version".
  id("org.lynxsdk.lynx.library-build")
}

// The build plugin scans node_modules again (same six levels up as the settings plugin) and generates the provider
// registry from its own list: drop the js/node_modules copies here too, or every provider is registered twice.
val ownNodeModules: File = file("../node_modules").canonicalFile
@Suppress("UNCHECKED_CAST")
val autolinked = extensions.extraProperties["lynxAutolinkLibraries"] as MutableList<Any>
autolinked.removeIf { library ->
  val androidDir = (library.javaClass.getMethod("getAndroidDir").invoke(library) as File).canonicalFile
  !androidDir.path.startsWith(ownNodeModules.path + File.separator)
}

// No library needs patching any more: @g9tm/syncdata-lynx 1.2.0, @g9tm/signalr-supernetcore-lynx 2.10.1 and
// G9LynxControls' @g9tm/lynxcontrols-native declare the parameter types Lynx's Android bridge accepts (Callback, ReadableMap,
// ReadableArray, byte[] — codegen 0.6.0 wrote Object, and one Object parameter left a module without methods in JS,
// G9SyncData LES-0050), and they supply androidx.annotation for kapt themselves. Each repository guards both with
// scripts/android-bridge-types.mjs and LynxBridgeSignatureTest, so this host builds every library as packed.

// Every library pins `kotlin { jvmToolchain(17) }`. A machine with only JDK 21 (Android Studio's jbr, no toolchain
// download) cannot provide a 17 toolchain, so the libraries compile on the running JDK with the bytecode level they
// declare (Java and Kotlin target 17). G9_KEEP_LIBRARY_TOOLCHAINS=1 keeps their own toolchain (a JDK 17 installed).
if (System.getenv("G9_KEEP_LIBRARY_TOOLCHAINS") != "1") {
  val running = JavaVersion.current().majorVersion.toInt()
  subprojects {
    if (path != ":app") {
      afterEvaluate {
        extensions.findByType(KotlinAndroidProjectExtension::class.java)?.jvmToolchain(running)
        tasks.withType(KotlinJvmCompile::class.java).configureEach { compilerOptions.jvmTarget.set(JvmTarget.JVM_17) }
      }
    }
  }
}
