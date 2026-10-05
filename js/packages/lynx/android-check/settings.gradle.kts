// Stand-in for the Android Gradle compile of @g9tm/syncdata-lynx where Google Maven (and with it AGP and the Android
// SDK) is out of reach: compiles the module sources (Kotlin + the generated Java spec) with kapt and the real
// lynx-processor against the published Lynx 4.1.0 classes and the Android framework classes of Robolectric's
// android-all (Maven Central), and checks the Autolink provider. `npm run check:android-module` runs it.
pluginManagement {
  repositories {
    gradlePluginPortal()
    mavenCentral()
  }
}

dependencyResolutionManagement { repositories { mavenCentral() } }

rootProject.name = "g9-syncdata-lynx-android-check"
include(":annotation-stubs")
