# KISSIK Camera Studio (Android + iOS)

A standalone camera studio for phones, written in Kotlin Multiplatform with Compose Multiplatform UI. Feature status is
tracked in [CHECKLIST.md](CHECKLIST.md).

## Layout

| Path | What |
|---|---|
| `shared/src/commonMain` | Everything shared: the picture pipeline (`image/`, `effects/`), framing, scenes (desktop JSON + 100-scene pack in `composeResources/files`), recording logic, RTMP model, captions, teleprompter, phone protocol, crash reports, settings and the whole Compose UI (`ui/`). `platform/Platform.kt` is the interface each phone implements. |
| `shared/src/commonTest` | Unit tests for the shared logic (run on the JVM). |
| `shared/src/androidMain` | Android services: CameraX + Camera2 interop, MediaPipe (person + faces, default) or ML Kit, ML Kit (barcodes, text, document scanner), MediaCodec/MediaMuxer, RTMP (RootEncoder `rtmp`, Apache-2.0), SpeechRecognizer, OkHttp WebSocket. |
| `shared/src/iosMain` | iOS services: AVFoundation, Vision, VisionKit, Speech, AVAssetWriter, Photos, NSURLSession WebSockets. `MainViewController()` hosts the Compose UI. |
| `whisper/` | Android library wrapping whisper.cpp (MIT) for Accurate captions. Gradle downloads the pinned release source; CMake builds it with the NDK. `WhisperTest` is an on-device test. |
| `androidApp/` | Android app shell (activity, manifest, adaptive icon, R8 rules, per-ABI splits). |
| `iosApp/` | iOS app shell: Swift entry point (`iOSApp.swift`, `ContentView.swift`), `Info.plist`, AppIcon, and an XcodeGen spec (`project.yml`). |

## Android (Windows, macOS or Linux)

Needs JDK 17 and the Android SDK (compile SDK 36). Create `local.properties` with `sdk.dir=...` if `ANDROID_HOME` isn't set.

```bash
cd mobile-studio
./gradlew :androidApp:assembleDebug
```

```bash
adb install -r androidApp/build/outputs/apk/debug/androidApp-arm64-v8a-debug.apk
```

Release APKs, one per ABI (signed with the debug key until a real signing config is added):

```bash
./gradlew :androidApp:assembleRelease
```

Shared tests:

```bash
./gradlew :shared:testDebugUnitTest
```

whisper on a device (push `ggml-base.bin` and whisper.cpp's `samples/jfk.wav` to `/data/local/tmp` first):

```bash
./gradlew :whisper:connectedDebugAndroidTest
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

Swift side (`iosApp/iosApp/Bridges.swift`, installed at launch):
- **Live streaming**: `RtmpPublisher` implements the Kotlin `RtmpBridge` with HaishinKit (BSD-3-Clause, added as a Swift package in `project.yml`).
- **PC webcam H.264**: `H264Encoder` implements `H264Bridge` with VideoToolbox and outputs Annex-B with SPS/PPS on keyframes.
- Certificate pinning for the PC link is in Kotlin (`PinningDelegate`).

## Privacy

All picture and sound processing runs on the phone. There's no account and no analytics. Crash reports are opt-in and scrubbed with the desktop's rules.
