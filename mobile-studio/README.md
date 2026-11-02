# KISSIK Camera Studio (Android + iOS)

A standalone camera studio for phones, written in Kotlin Multiplatform with Compose Multiplatform UI. Feature status is
tracked in [CHECKLIST.md](CHECKLIST.md).

## Layout

| Path | What |
|---|---|
| `shared/src/commonMain` | Everything shared: the picture pipeline (`image/`, `effects/`), framing, scenes (desktop JSON + 100-scene pack in `composeResources/files`), recording logic, RTMP model, captions, teleprompter, phone protocol, crash reports, settings and the whole Compose UI (`ui/`). `platform/Platform.kt` is the interface each phone implements. |
| `shared/src/commonTest` | Unit tests for the shared logic (run on the JVM). |
| `shared/src/androidMain` | Android services: CameraX + Camera2 interop, ML Kit (selfie segmentation, faces, barcodes, text, document scanner), MediaCodec/MediaMuxer, RTMP (RootEncoder `rtmp`, Apache-2.0), SpeechRecognizer, OkHttp WebSocket. |
| `shared/src/iosMain` | iOS services: AVFoundation, Vision, VisionKit, Speech, AVAssetWriter, Photos, NSURLSession WebSockets. `MainViewController()` hosts the Compose UI. |
| `androidApp/` | Android app shell (activity, manifest, adaptive icon). |
| `iosApp/` | iOS app shell: Swift entry point (`iOSApp.swift`, `ContentView.swift`), `Info.plist`, AppIcon, and an XcodeGen spec (`project.yml`). |

## Android (Windows, macOS or Linux)

Needs JDK 17 and the Android SDK (compile SDK 36). Create `local.properties` with `sdk.dir=...` if `ANDROID_HOME` isn't set.

```bash
cd mobile-studio
./gradlew :androidApp:assembleDebug
```

```bash
adb install -r androidApp/build/outputs/apk/debug/androidApp-debug.apk
```

Shared tests:

```bash
./gradlew :shared:testDebugUnitTest
```

On the KISSIK dev PC Gradle needs `JAVA_TOOL_OPTIONS=-Djdk.net.unixdomain.tmpdir=C:\Users\Public\ktmp`; otherwise it fails with "Unable to establish loopback connection".

## iOS (on a Mac)

Needs Xcode 16+, JDK 17 and [XcodeGen](https://github.com/yonaskolb/XcodeGen) (MIT).

```bash
brew install xcodegen
```

```bash
cd mobile-studio/iosApp && xcodegen
```

```bash
open iosApp.xcodeproj
```

Pick your team under Signing and run on an iPhone (the camera doesn't work in the Simulator). The "Compile Kotlin framework" build phase runs `./gradlew :shared:embedAndSignAppleFrameworkForXcode`, which builds the `Shared` framework that Swift imports. The iOS Kotlin code has not been compiled yet (it can't be built on Windows), so expect a round of small interop fixes on the first build.

Swift hooks:
- **Live streaming**: iOS has no built-in RTMP client. Implement `IosLive.Bridge` in Swift (for example with HaishinKit, BSD-3-Clause) and set `IosLive.companion.bridge`.
- **PC webcam over Wi-Fi**: the PC uses a self-made certificate. Give `IosWebcam` a URLSession whose delegate pins the certificate on first use, as Android does.

## Privacy

All picture and sound processing runs on the phone. There's no account and no analytics. Crash reports are opt-in and scrubbed with the desktop's rules.
