# KISSIK for macOS — porting and build guide

Status: **plan, no code yet.** Read [README.md](README.md) first for the shared contracts and the
parity matrix.

## 1. Targets and toolchain

| Item | Choice | Version |
| --- | --- | --- |
| Minimum macOS | 14 Sonoma (camera extensions are stable from 13; 14 gives better Vision person segmentation and SwiftUI) | 14.0 |
| Hardware | Apple silicon first; Intel builds best-effort (universal binary) | M1+ |
| Xcode | 16.x or newer | 16.4+ |
| Language | Swift 6 (strict concurrency on), small Objective-C/C++ only where needed (audio plug-in) | 6.0+ |
| UI | SwiftUI + AppKit where needed (borderless windows, menu-bar extra) | — |
| Capture | AVFoundation (`AVCaptureSession`, `AVCaptureVideoDataOutput`) | — |
| Effects | Core Image with custom Metal kernels; Metal compute for blur/LUT | Metal 3 |
| Segmentation | Option A: Vision `VNGeneratePersonSegmentationRequest` (`.balanced` / `.accurate`). Option B: our ONNX models converted to Core ML. Option C: ONNX Runtime with the Core ML execution provider | onnxruntime 1.20+ (`onnxruntime-objc` pod or SwiftPM) |
| Virtual camera | CoreMediaIO **Camera Extension** (system extension) | macOS 13+ API |
| Virtual mic | Audio Server plug-in (`AudioServerPlugIn.h`, HAL plug-in in `/Library/Audio/Plug-Ins/HAL`) | — |
| Recording | `AVAssetWriter` (H.264 / HEVC, AAC) via VideoToolbox | — |
| Noise suppression | RNNoise (BSD) built as a static C library | 0.2 |
| Packages | Swift Package Manager only | — |
| Distribution | Developer ID–signed, notarised DMG + Homebrew cask; Mac App Store later (see §9) | — |

An Apple Developer Program membership (paid, yearly) is **required**: camera extensions and HAL
plug-ins will not load unsigned on a normal machine, and notarisation needs it.

## 2. Repository layout

```
apple/
  OpenKamera.xcworkspace
  Packages/
    OpenKameraKit/              # Swift package shared by macOS + iOS
      Sources/
        OKCore/                 # ports of the portable C# maths
          FrameTransform.swift  # ← FrameTransform.cs
          OutputFit.swift       # ← OutputFit.cs
          FormatChooser.swift   # ← FormatChooser.cs
          ImageFilters.swift    # ← ImageFilters.cs (CPU reference)
          Lut3D.swift           # ← Lut3D.cs (.cube parser)
          ScreenLight.swift     # ← ScreenLight.cs
          Scenes.swift          # ← Scenes.cs (.okscene Codable + zip)
          Settings.swift        # ← Settings.cs
        OKPhoneLink/            # phone link protocol (shared with iOS sender)
        OKMetal/                # .metal kernels + Swift wrappers
      Tests/
        OKCoreTests/            # same test vectors as tests/OpenKamera.Core.Tests
  macOS/
    OpenKamera/                 # the app target (SwiftUI)
      App/  Capture/  Effects/  Scenes/  Audio/  Recording/  Light/  PhoneReceiver/
    OpenKameraCameraExtension/  # CMIO camera extension target (system extension)
    OpenKameraAudioPlugIn/      # HAL audio server plug-in (C/C++)
    Installer/                  # DMG layout, notarise.sh, cask template
  shared-test-vectors/          # JSON fixtures exported from the C# tests
```

`OpenKamera.Core/Models/*.onnx` are copied in at build time by a run-script phase, not duplicated in git.

## 3. Feature mapping

| KISSIK feature | Windows today | macOS implementation |
| --- | --- | --- |
| Camera list + hot-plug | `CameraCatalog.cs` | `AVCaptureDevice.DiscoverySession(deviceTypes: [.builtInWideAngleCamera, .external, .continuityCamera, .deskViewCamera], …)`; observe `AVCaptureDeviceWasConnected/Disconnected` |
| Formats / profiles | `FormatChooser.cs` | Same rules over `device.formats` (`CMVideoFormatDescriptionGetDimensions`, `videoSupportedFrameRateRanges`) |
| Hardware controls | `CameraController.cs` | `AVCaptureDevice` focus/exposure/WB/zoom where `isXSupported`. External USB cameras often expose nothing through AVFoundation; for UVC controls send class requests with IOKit `IOUSBDeviceInterface` (phase 5, optional). Show controls only when supported. |
| Software adjustments | `ImageFilters.cs` | One Metal compute kernel doing brightness/contrast/gamma/hue/saturation/vibrance/temperature/tint; sharpness via `CIUnsharpMask`. Unit-test against C# vectors. |
| Looks + `.cube` LUTs | `Lut3D.cs` | Parse `.cube` in Swift → `CIColorCubeWithColorSpace` (or a Metal 3D texture). LUT folder: `~/Library/Application Support/OpenKamera/LUTs`. |
| Framing (zoom/pan/mirror/rotate) | `FrameTransform.cs` | Same maths → `CIImage.transformed(by:)` + crop; mirror flag separate for preview vs output (smart mirror) |
| Auto framing (Auto / Smart) | `AutoFramer.cs` (done) | `VNDetectFaceRectanglesRequest` on a small copy every 3rd frame → Swift port of the `AutoFramer` maths (face-height target, framing point above centre, dead zone, 2 s hold, follow rate). Same test vectors. |
| Steady picture | `Stabilizer.cs` (done) | Port block-match shift to a Metal compute kernel on a downscaled luma plane; 5 % crop margin, same relax constant. Alternative: `VNTranslationalImageRegistrationRequest` (simpler, slower). |
| Chroma key | `ChromaKey.cs` (done) | One Metal kernel: brightness-normalised chroma distance → alpha with similarity/smoothness, spill suppression on the key channel. |
| Look presets | `LookPresets.cs` (done) | Same JSON in `~/Library/Application Support/OpenKamera/Looks`. |
| Who has my camera? | `DeviceUsage.cs` (in progress) | No public API lists which app uses the camera. Show our own extension's clients count; otherwise point to the system camera indicator. |
| Segmentation Fast / High | `PersonSegmenter.cs` + ONNX | **Default:** Vision person segmentation (`.balanced` ≈ Fast, `.accurate` ≈ High), runs on Neural Engine, no model shipping. **Parity mode:** convert `selfie_segmentation_fp16.onnx` and `modnet_fp16.onnx` with `coremltools` 8 (`ct.convert` from ONNX via an intermediate PyTorch export, or run ONNX Runtime + CoreML EP directly). Keep temporal smoothing + blob removal from `BackgroundEffects.cs` ported to Metal. |
| Blur / replace / remove, spotlight, retouch | `BackgroundEffects.cs` | Metal: mask-weighted Gaussian (`MPSImageGaussianBlur`) + composite; spotlight = mask-weighted exposure; retouch = bilateral filter inside mask. Remove = alpha output (virtual camera gets premultiplied BGRA over scene background). |
| Scenes | `Scenes.cs`, `SceneCompositor.cs` | `Codable` structs identical to `SceneDocument`/`SceneLayer` (see §6). Draw layers with Core Graphics/Core Text into a `CVPixelBuffer`, cached until the scene changes; composite over camera in Metal. |
| Screen light | `ScreenLight.cs`, `ScreenLightWindow.cs` | Borderless `NSPanel` (`.nonactivatingPanel`), `level = .screenSaver`, `ignoresMouseEvents = true`, `sharingType = .none` (excluded from screen capture), one per `NSScreen`. Shapes drawn with `CAShapeLayer`. Brightness: built-in display via `DisplayServices` is private — do **not** ship private APIs; use EDR headroom (`maximumExtendedDynamicRangeColorComponentValue`) for "boost" and DDC/CI over `IOAVService` for external monitors (document as experimental). |
| Audio mixer | `AudioEngine.cs` (in progress) | `AVAudioEngine`: one `AVAudioInputNode` per device is not allowed, so use one engine per input device (or a Core Audio aggregate device) → mixer → limiter (`AVAudioUnitEffect` with `kAudioUnitSubType_PeakLimiter`) → EQ (`AVAudioUnitEQ`, 4 bands) → tap for meters. Noise suppression: RNNoise in an `AUAudioUnit` v3. System audio: `ScreenCaptureKit` audio-only stream (`SCStreamConfiguration.capturesAudio`), per-app via content filter. |
| Virtual mic | planned | Audio Server plug-in reading from a shared ring buffer (§5). |
| Virtual camera | C++ media source | CoreMediaIO Camera Extension (§4). |
| Recording | `CompositeRecorder.cs` | `AVAssetWriter` with `AVAssetWriterInputPixelBufferAdaptor`; H.264 or HEVC (`AVVideoCodecType.hevc`), AAC 48 kHz; write fragmented MP4 (`movieFragmentInterval = 2 s`) so a crash keeps the file. Default folder `~/Movies/OpenKamera`. |
| Phone as camera | planned | Receiver for the phone link protocol (ios.md §5): USB via `usbmuxd` socket `/var/run/usbmuxd`, Wi-Fi via Bonjour (`NWBrowser` for `_openkamera._tcp`). Decode with `VTDecompressionSession`. The macOS-only system feature that turns an iPhone into a camera already appears as an `AVCaptureDevice` and needs no work beyond listing it. |
| Snapshot | `MainPage` | `CIContext.writeHEIFRepresentation` / PNG |
| Donate | `Donation.cs` | Same JSON; open link with `NSWorkspace` |

## 4. Virtual camera — CoreMediaIO Camera Extension

### 4.1 How it fits
```
OpenKamera.app (frames after effects)
   └─ sink stream ──► OpenKameraCameraExtension (system extension, runs as _cmiodalassistants)
                          └─ source stream ──► any app that lists cameras
```
The extension exposes **one device** with two streams: a *source* stream (what apps see) and a *sink*
stream (what the KISSIK app pushes into using `CMIOStreamCopyBufferQueue` on the sink). This is the
pattern Apple's sample "Creating a camera extension with Core Media I/O" uses. No shared memory or XPC
is needed for frames; the sink stream carries `CMSampleBuffer`s backed by IOSurfaces.

### 4.2 Steps
1. Xcode → File → New → Target → **Camera Extension**. Bundle id `org.openkamera.mac.camera`.
2. Implement `CMIOExtensionProviderSource`, `CMIOExtensionDeviceSource`, two `CMIOExtensionStreamSource`s
   (`.source`, `.sink`). Advertise formats: 1280×720, 1920×1080 at 30 fps, `kCVPixelFormatType_32BGRA`
   (and `420v` for efficiency).
3. When no app is pushing, show the **slate** (same art as Windows: logo + "KISSIK is paused") — never black.
4. App side: activate with `OSSystemExtensionRequest.activationRequest(forExtensionWithIdentifier:queue:)`.
   The user must approve once in System Settings → General → Login Items & Extensions → Camera Extensions.
   Show a guided sheet with a screenshot-free text explanation.
5. Find the device with `CMIOObjectGetPropertyData(kCMIOHardwarePropertyDevices)`, match by device UID,
   get the sink stream, start it, and enqueue `CMSampleBuffer`s each frame.

### 4.3 Entitlements and signing
| Target | Entitlements |
| --- | --- |
| App | `com.apple.developer.system-extension.install` = YES; `com.apple.security.device.camera`; `com.apple.security.device.audio-input`; App Group `$(TeamID).org.openkamera.shared`; hardened runtime |
| Camera extension | App Sandbox = YES; same App Group; `com.apple.security.device.camera` not needed |
| Info.plist (app) | `NSCameraUsageDescription`, `NSMicrophoneUsageDescription`, `NSSystemExtensionUsageDescription` |
| Info.plist (ext) | `CMIOExtension` → `CMIOExtensionMachServiceName` = `$(TeamID).org.openkamera.mac.camera` |

The app **must live in `/Applications`** for activation to succeed. During development use
`systemextensionsctl developer on` (requires SIP partly disabled on a dedicated test Mac) or simply
copy the signed build to `/Applications`. Reset with `systemextensionsctl reset`.

## 5. Virtual microphone — Audio Server plug-in

- A HAL plug-in (`OpenKameraAudio.driver`) installed to `/Library/Audio/Plug-Ins/HAL/` (needs admin password once;
  ship a signed `.pkg` component for it). Start from Apple's "NullAudio" sample (`AudioServerPlugIn.h`).
- One output-less device "KISSIK Microphone", 48 kHz stereo float32.
- Frames from the app reach the plug-in through a **shared memory ring buffer** (`shm_open` name
  `/org.openkamera.audio`, 1 s capacity, header with write index + sample rate) — the plug-in runs inside
  `coreaudiod` and cannot use XPC easily. Keep the header style of `VirtualCameraChannel.cs` (magic,
  sequence, timestamp, writer pid) so tooling stays the same.
- Restart audio after install: `sudo killall coreaudiod`.
- Alternative (later, macOS 15+): an AudioDriverKit DEXT; heavier signing (DriverKit entitlements by request).
  Not recommended for v1.

## 6. Scenes — porting `.okscene`

Mirror `Scenes.cs` exactly:

```swift
struct SceneLayer: Codable, Identifiable {
    var id = String(UUID().uuidString.prefix(8)).lowercased()
    var kind = "text"            // text | image | shape | camera
    var name = ""
    var visible = true
    var x = 0.05, y = 0.05, w = 0.4, h = 0.1   // fractions of canvas
    var opacity = 1.0
    var text = ""; var fontSize = 0.06; var bold = false; var align = "left"
    var color = "#FFFFFF"; var fill = ""; var stroke = ""
    var strokeWidth = 0.0; var cornerRadius = 0.0   // fractions of canvas HEIGHT
    var shape = "rect"           // rect | ellipse
    var source = ""              // "assets/logo.png" inside the scene folder
}
struct SceneDocument: Codable {
    var id = "user-" + …; var name = "Untitled"; var category = "Utility"
    var background = "#101418"; var background2 = ""
    var layers: [SceneLayer] = []
}
```
- JSON keys are camelCase on write (as `System.Text.Json` default writes them in `Scenes.cs` — check the
  `JsonSerializerOptions` there before shipping) and **case-insensitive on read**: decode via a custom
  `CodingKey` lookup that lowercases keys.
- Ignore unknown keys; keep defaults for missing ones (forward compatibility with newer Windows builds).
- Zip handling: use Apple's `AppleArchive` is not zip — use the `ZIPFoundation` package (MIT, 0.9.19+).
  Reject entries whose resolved path leaves the scene folder (zip-slip).
- Library folder: `~/Library/Application Support/OpenKamera/Scenes/<id>/scene.json`.
- Templates: port the 7 built-ins from `SceneDocument.Templates`; add a test that serialises each to JSON and
  compares to a fixture exported from C#.

## 7. Numbered build plan

| # | Milestone | Done when |
| --- | --- | --- |
| 1 | Workspace, `OpenKameraKit` package, CI (GitHub Actions `macos-15`, `xcodebuild test`) | Empty app builds and tests run in CI |
| 2 | Port pure maths (FrameTransform, OutputFit, FormatChooser, Lut3D, ImageFilters, ScreenLight) + shared test vectors | All ported tests pass with the C# fixtures |
| 3 | Capture + preview (Metal view, 60 fps), device picker, profiles | Preview of built-in and USB camera, mode matches profile |
| 4 | Adjustments, looks, LUT import, framing | Visual match with Windows on a test image (ΔE < 2) |
| 5 | Segmentation (Vision) + blur/replace/remove + spotlight/retouch | 1080p30 on M1 with < 15 ms effect time |
| 6 | Camera extension + slate | Shows in a browser meeting and a meeting app; survives app quit |
| 7 | Recording (fragmented MP4) + snapshot | 10-min 1080p30 recording, no dropped frames, file playable after force-quit |
| 8 | Scenes: load/save/import/export, templates, editor | `.okscene` from Windows opens identically |
| 9 | Screen light | All shapes/looks; excluded from screen sharing |
| 10 | Audio mixer, RNNoise, system audio, virtual mic plug-in | Mic usable in a meeting app with noise suppression |
| 11 | Phone receiver (USB + Wi-Fi) | iPhone app streams to Mac (see ios.md) |
| 12 | Packaging: DMG, notarisation, Sparkle updates, Homebrew cask | Clean-machine install test passes |

## 8. Step-by-step: build from source

```bash
# 1. Tools
xcode-select --install
brew install swiftlint create-dmg      # optional helpers
# 2. Clone
git clone https://github.com/<owner>/OpenKamera.git && cd OpenKamera/apple
# 3. Open
open OpenKamera.xcworkspace
```
4. In Xcode → Signing & Capabilities, set your Team for **all three** targets (app, camera extension,
   audio plug-in) and change the bundle-id prefix if you are not on the KISSIK team.
5. Build scheme `OpenKamera-macOS` (Release) → Product → Archive → Distribute App → Developer ID → Export.
6. Copy `OpenKamera.app` to `/Applications`, launch, click **Enable virtual camera**, approve in System Settings.
7. Command-line test run: `xcodebuild -workspace OpenKamera.xcworkspace -scheme OKCoreTests -destination 'platform=macOS' test`.

## 9. Packaging and distribution

1. **Sign** with "Developer ID Application"; enable hardened runtime; sign the extension and plug-in inside-out.
2. **Notarise**: `xcrun notarytool submit OpenKamera.dmg --keychain-profile OK_NOTARY --wait`, then
   `xcrun stapler staple OpenKamera.dmg`.
3. **DMG**: `create-dmg --volname KISSIK --app-drop-link 480 160 OpenKamera.dmg build/OpenKamera.app`.
4. The audio plug-in needs root to install → ship it as an optional `.pkg` (signed with "Developer ID Installer"),
   offered from inside the app on first use of "Virtual microphone".
5. **Updates**: Sparkle 2 (MIT) with EdDSA-signed appcast on GitHub Releases.
6. **Homebrew cask** (`openkamera.rb` in a tap first, then homebrew/cask):
   `cask "openkamera" do version "x.y.z"; sha256 "…"; url "https://github.com/<owner>/OpenKamera/releases/download/v#{version}/OpenKamera-#{version}.dmg"; app "OpenKamera.app"; zap trash: "~/Library/Application Support/OpenKamera" end`.
7. **Mac App Store** (later): camera extensions are allowed in the store; HAL plug-ins are not → store build ships
   without virtual mic. Keep a build flag `OK_APPSTORE`.

## 10. Testing

- Unit: `OKCoreTests` with fixtures exported from `tests/OpenKamera.Core.Tests` (add a tiny exporter there later).
- Golden images: render a fixed test frame through every look/effect, compare with Windows output (tolerance).
- Virtual camera: browser test page using `navigator.mediaDevices.getUserMedia` (Safari, a Chromium browser,
  Firefox), desktop meeting apps, a streaming studio app; check device name, 720p/1080p, mirroring, slate on pause,
  two apps at once.
- Performance: Instruments → Metal System Trace + Time Profiler; budget 33 ms per frame at 30 fps, target < 50 % of it.
- Energy: Activity Monitor "Energy Impact" < 20 on M1 at 1080p30 with blur.
- Clean-machine test in a fresh macOS VM (UTM / Virtualization framework) for install, approval and uninstall.

## 11. Risks

| Risk | Impact | Mitigation |
| --- | --- | --- |
| User must approve system extension; confusing UI | Many users stuck | Clear in-app guide, detect state with `OSSystemExtensionRequest` delegate, retry button |
| Some apps cache camera list or use old DAL plug-in path | Device not shown | Camera Extensions are the only supported path since 12.3; tell users to restart the app |
| Extension crash → camera disappears | Bad meeting | Keep the extension tiny (no effects inside), slate fallback, crash reporting |
| HAL plug-in runs in `coreaudiod` | A bug can break all audio | Minimal code, fuzz the ring buffer reader, never block |
| Vision masks differ from our ONNX masks | Visual mismatch | Offer "Parity mode" with converted models; document the difference |
| Brightness control only via private APIs on built-in displays | Feature gap | Use EDR boost instead; never ship private APIs |
| Swift port drifts from C# Core | Divergent behaviour | Shared JSON test vectors run in both CIs |
