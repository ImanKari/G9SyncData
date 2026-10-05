import org.jetbrains.kotlin.gradle.dsl.JvmTarget

plugins {
  kotlin("jvm") version "2.3.20"
  kotlin("kapt") version "2.3.20"
}

val lynx = "4.1.0"
val moduleSrc = "../android/src/main/java"

java {
  sourceCompatibility = JavaVersion.VERSION_17
  targetCompatibility = JavaVersion.VERSION_17
}

kotlin { compilerOptions { jvmTarget.set(JvmTarget.JVM_17) } }

// The Lynx AARs: only their classes.jar matters for compiling.
val aars by configurations.creating { isTransitive = false }

val lynxDir = layout.buildDirectory.dir("lynx")
val extractLynx by tasks.registering {
  inputs.files(aars)
  outputs.dir(lynxDir)
  doLast {
    for (aar in aars.files) {
      project.copy {
        from(zipTree(aar)) { include("classes.jar") }
        into(lynxDir)
        rename { "${aar.nameWithoutExtension}.jar" }
      }
    }
  }
}

sourceSets {
  main {
    java.srcDirs(moduleSrc)
    kotlin.srcDirs(moduleSrc)
  }
}

dependencies {
  aars("org.lynxsdk.lynx:lynx:$lynx@aar")
  aars("org.lynxsdk.lynx:service-api:$lynx@aar")
  compileOnly(files(lynxDir.map { it.asFileTree.matching { include("*.jar") } }).builtBy(extractLynx))
  compileOnly("org.robolectric:android-all:14-robolectric-10818077")
  implementation("org.brotli:dec:0.1.2")
  // Not transitive: its androidx.annotation dependency is on Google Maven only (the stubs stand in).
  kapt("org.lynxsdk.lynx:lynx-processor:$lynx") { isTransitive = false }
  kapt("com.squareup:javapoet:1.11.1")
  kapt(project(":annotation-stubs"))
  compileOnly(project(":annotation-stubs"))

  // src/test: every @LynxMethod signature built by Lynx's own LynxMethodWrapper (what the bridge does at runtime).
  testImplementation(files(lynxDir.map { it.asFileTree.matching { include("*.jar") } }).builtBy(extractLynx))
  testImplementation("org.robolectric:android-all:14-robolectric-10818077")
  testImplementation(project(":annotation-stubs"))
  testImplementation("junit:junit:4.13.2")
}

kapt {
  // What org.lynxsdk.lynx.library-build passes to the library project (LynxLibraryBuildPlugin).
  arguments { arg("lynx.library.packageName", "com.g9tm.syncdatalynx") }
}

tasks.named("compileKotlin") { dependsOn(extractLynx) }
tasks.named("compileTestKotlin") { dependsOn(extractLynx) }
tasks.named<Test>("test") { testLogging { events("passed", "failed"); showStandardStreams = true; exceptionFormat = org.gradle.api.tasks.testing.logging.TestExceptionFormat.FULL } }

/** Fails unless kapt generated the Autolink provider the host's LynxAutolinkGenerated loads by name. */
val verifyProvider by tasks.registering {
  dependsOn("compileJava")
  doLast {
    val generated = layout.buildDirectory.dir("generated/source/kapt/main").get().asFile
    val provider = generated.walkTopDown().firstOrNull { it.name == "LynxLibraryProviderImpl.java" }
      ?: throw GradleException("lynx-processor generated no LynxLibraryProviderImpl under $generated")
    val text = provider.readText()
    if (!text.contains("G9SyncDataLynxModule")) throw GradleException("LynxLibraryProviderImpl does not register G9SyncDataLynxModule:\n$text")
    println("autolink provider: ${provider.relativeTo(generated)}\n$text")
  }
}

tasks.named("build") { dependsOn(verifyProvider) }
