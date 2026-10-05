// JVM-only build of the Android core (../android/src/main/java/com/g9tm/syncdatalynx/core): the Brotli half of the
// module contract runs on the host JVM, without the Android SDK. `npm run test:android-jvm` runs it.
pluginManagement {
  repositories {
    gradlePluginPortal()
    mavenCentral()
  }
}

dependencyResolutionManagement { repositories { mavenCentral() } }

rootProject.name = "g9-syncdata-lynx-jvm"
