import org.jetbrains.kotlin.gradle.dsl.JvmTarget

plugins {
  kotlin("jvm") version "2.3.20"
}

java {
  sourceCompatibility = JavaVersion.VERSION_17
  targetCompatibility = JavaVersion.VERSION_17
}

kotlin { compilerOptions { jvmTarget.set(JvmTarget.JVM_17) } }

sourceSets {
  main { kotlin.srcDirs("../android/src/main/java/com/g9tm/syncdatalynx/core") }
  test {
    kotlin.srcDirs("../android/src/test/java")
    // The Brotli test vectors shared with the iOS and Lynxtron suites.
    resources.srcDirs("../test/fixtures")
  }
}

dependencies {
  implementation("org.brotli:dec:0.1.2")
  testImplementation("junit:junit:4.13.2")
}

tasks.test {
  useJUnit()
  testLogging {
    events("passed", "failed", "skipped")
    exceptionFormat = org.gradle.api.tasks.testing.logging.TestExceptionFormat.FULL
  }
}
