import org.jetbrains.kotlin.gradle.dsl.JvmTarget

plugins {
  id("com.android.application")
  id("org.jetbrains.kotlin.android")
}

val lynx = "4.1.0"
val primjs = "4.1.1"

android {
  namespace = "com.g9tm.syncdatahost"
  compileSdk = 36

  defaultConfig {
    applicationId = "com.g9tm.syncdatahost"
    minSdk = 26
    targetSdk = 36
    versionCode = 1
    versionName = "1.2.0"
    buildConfigField("String", "LYNX_VERSION", "\"$lynx\"")
    buildConfigField("String", "PRIMJS_VERSION", "\"$primjs\"")
  }

  buildFeatures { buildConfig = true }

  compileOptions {
    sourceCompatibility = JavaVersion.VERSION_17
    targetCompatibility = JavaVersion.VERSION_17
  }

  // The bundle (`npm run build` in apps/lynx-android-host → dist/main.lynx.bundle) is packaged as an asset.
  sourceSets["main"].assets.srcDir(layout.buildDirectory.dir("generated/g9-assets"))
}

kotlin { compilerOptions { jvmTarget.set(JvmTarget.JVM_17) } }

val copyBundle by tasks.registering(Copy::class) {
  from("../../dist") { include("main.lynx.bundle") }
  into(layout.buildDirectory.dir("generated/g9-assets"))
  doLast {
    if (!layout.buildDirectory.file("generated/g9-assets/main.lynx.bundle").get().asFile.exists()) {
      throw GradleException("apps/lynx-android-host/dist/main.lynx.bundle is missing: run `npm run build` there first.")
    }
  }
}

tasks.named("preBuild") { dependsOn(copyBundle) }

dependencies {
  implementation("org.lynxsdk.lynx:lynx:$lynx")
  implementation("org.lynxsdk.lynx:lynx-jssdk:$lynx")
  implementation("org.lynxsdk.lynx:lynx-trace:$lynx")
  implementation("org.lynxsdk.lynx:primjs:$primjs")
  implementation("org.lynxsdk.lynx:service-api:$lynx")
  // console.* → logcat
  implementation("org.lynxsdk.lynx:lynx-service-log:$lynx")
  // Lynx's `fetch` (the scenario's POST /sample/pots). It uses OkHttp without declaring it.
  implementation("org.lynxsdk.lynx:lynx-service-http:$lynx")
  implementation("com.squareup.okhttp3:okhttp:4.12.0")
  // The generated com.lynx.tasm.library.LynxAutolinkGenerated is @androidx.annotation.Keep; nothing above puts androidx
  // on the app's compile classpath (G9LynxControls' android-host gets it through androidx.activity).
  implementation("androidx.annotation:annotation:1.9.1")
}
