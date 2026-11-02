plugins {
    id("com.android.library")
    id("org.jetbrains.kotlin.android")
}

// whisper.cpp (MIT) for the "Accurate" captions engine, built from a pinned release. Gradle fetches the source once
// (CMake's own downloader breaks on paths with spaces).
val whisperVersion = "1.7.6"
val whisperRoot = layout.buildDirectory.dir("whisper-src").get().asFile
val whisperSrc = File(whisperRoot, "whisper.cpp-$whisperVersion")
val fetchWhisper by tasks.registering {
    outputs.dir(whisperRoot)
    doLast {
        if (File(whisperSrc, "CMakeLists.txt").exists()) return@doLast
        whisperRoot.mkdirs()
        val tgz = File(whisperRoot, "whisper.tar.gz")
        uri("https://github.com/ggml-org/whisper.cpp/archive/refs/tags/v$whisperVersion.tar.gz").toURL().openStream().use { i -> tgz.outputStream().use { i.copyTo(it) } }
        copy { from(tarTree(resources.gzip(tgz))); into(whisperRoot) }
        tgz.delete()
    }
}
tasks.matching { it.name.startsWith("configureCMake") || it.name.startsWith("buildCMake") }.configureEach { dependsOn(fetchWhisper) }

android {
    namespace = "app.kissik.whisper"
    compileSdk = 36
    ndkVersion = "26.3.11579264"
    defaultConfig {
        minSdk = 26
        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"
        externalNativeBuild { cmake { arguments += listOf("-DCMAKE_BUILD_TYPE=Release", "-DANDROID_STL=c++_static", "-DWHISPER_SRC=${whisperSrc.invariantSeparatorsPath}") } }
        ndk { abiFilters += listOf("arm64-v8a", "armeabi-v7a", "x86_64") }
    }
    externalNativeBuild { cmake { path = file("src/main/cpp/CMakeLists.txt"); version = "3.22.1" } }
    compileOptions { sourceCompatibility = JavaVersion.VERSION_17; targetCompatibility = JavaVersion.VERSION_17 }
}

kotlin { jvmToolchain(17) }

dependencies {
    androidTestImplementation("androidx.test:runner:1.6.2")
    androidTestImplementation("junit:junit:4.13.2")
}
