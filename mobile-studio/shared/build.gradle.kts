plugins {
    id("org.jetbrains.kotlin.multiplatform")
    id("com.android.library")
    id("org.jetbrains.kotlin.plugin.compose")
    id("org.jetbrains.kotlin.plugin.serialization")
    id("org.jetbrains.compose")
}

kotlin {
    androidTarget { compilerOptions { jvmTarget.set(org.jetbrains.kotlin.gradle.dsl.JvmTarget.JVM_17) } }
    listOf(iosX64(), iosArm64(), iosSimulatorArm64()).forEach {
        it.binaries.framework { baseName = "Shared"; isStatic = true }
    }

    sourceSets {
        commonMain.dependencies {
            implementation(compose.runtime)
            implementation(compose.foundation)
            implementation(compose.material3)
            implementation(compose.materialIconsExtended)
            implementation(compose.ui)
            implementation(compose.components.resources)
            implementation("org.jetbrains.kotlinx:kotlinx-coroutines-core:1.10.2")
            implementation("org.jetbrains.kotlinx:kotlinx-serialization-json:1.9.0")
        }
        commonTest.dependencies {
            implementation(kotlin("test"))
        }
        androidMain.dependencies {
            val camerax = "1.4.2"
            implementation("androidx.activity:activity-compose:1.10.1")
            implementation("androidx.camera:camera-camera2:$camerax")
            implementation("androidx.camera:camera-lifecycle:$camerax")
            implementation("androidx.camera:camera-view:$camerax")
            implementation("androidx.camera:camera-video:$camerax")
            implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.10.2")
            implementation("com.google.mlkit:segmentation-selfie:16.0.0-beta6")
            implementation("com.google.mlkit:face-detection:16.1.7")
            implementation("com.google.mlkit:barcode-scanning:17.3.0")
            implementation("com.google.mlkit:text-recognition:16.0.1")
            implementation("com.google.android.gms:play-services-mlkit-document-scanner:16.0.0-beta1")
            implementation("com.squareup.okhttp3:okhttp:4.12.0")
            implementation("com.github.pedroSG94.RootEncoder:rtmp:2.6.1")
        }
    }
}

android {
    namespace = "app.kissik.studio.shared"
    compileSdk = 36
    defaultConfig { minSdk = 26 }
    compileOptions { sourceCompatibility = JavaVersion.VERSION_17; targetCompatibility = JavaVersion.VERSION_17 }
}

compose.resources {
    packageOfResClass = "app.kissik.studio.resources"
    generateResClass = always
}
