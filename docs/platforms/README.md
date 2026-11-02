# KISSIK on other platforms

KISSIK ships first on Windows 11 (WinUI 3, .NET 10, C++ virtual camera).
This folder holds the plans for taking it to more places:

| Doc | Target | Shape of the port |
| --- | --- | --- |
| [macos.md](macos.md) | macOS 14+ (Apple silicon first) | Native Swift/SwiftUI app + CoreMediaIO camera extension + audio plug-in |
| [ios.md](ios.md) | iOS 17+ iPhone | Companion app: the iPhone becomes a camera (and mic) for the desktop app. **This is what donations fund.** |
| [linux.md](linux.md) | Ubuntu 22.04 / 24.04, Fedora 40+ | Avalonia (.NET) app reusing `OpenKamera.Core` + v4l2loopback / PipeWire virtual devices |

These docs are kept in step with [`ROADMAP.md`](../../ROADMAP.md). When a Windows feature
lands, update the parity matrix below and the "feature mapping" table in each platform doc.

_Last synced with ROADMAP.md: 2026-11-02 (second pass: auto-framing, steady picture, chroma key, look presets added)._

**Done on Windows so far:** Darkroom Glass UI, any UVC webcam with hardware controls, profiles 720p–4K and vertical,
tone/colour, looks and `.cube` LUTs, look presets, zoom/pan/mirror/rotate, auto-framing, steady picture,
person segmentation with blur/replace/remove, spotlight, retouch, chroma key, scenes with editor and gallery,
screen light with ring light, snapshot and H.264 recording of what the preview shows.
**In progress on Windows:** virtual camera (C++ media source), audio mixer, "Who has my camera?".

---

## 1. What is shared and what is not

### 1.1 Things every platform shares (formats and behaviour, not necessarily code)

These are the "contract" of OpenKamera. Every port must read and write them byte-for-byte the same way,
so a user can move between machines and so the phone app can talk to any desktop.

| Contract | Defined in | Notes |
| --- | --- | --- |
| Scene file `.okscene` (zip: `scene.json` + `assets/`) | `src/OpenKamera.Core/Scenes.cs` (`SceneDocument`, `SceneLayer`, `SceneLibrary`) | Coordinates are fractions of the canvas (0–1); font size, stroke and corner radius are fractions of canvas **height**. Property names are case-insensitive on read. Import must stay zip-slip safe. |
| Segmentation models | `src/OpenKamera.Core/Models/selfie_segmentation_fp16.onnx` (Fast), `modnet_fp16.onnx` (High) | Same ONNX files on every desktop. On Apple platforms they may be converted to Core ML (see macos.md). |
| Looks / filters | `ImageFilters.cs` (tone/colour maths), `Lut3D.cs` (`.cube` parser) | Port the maths to a GPU shader per platform; keep CPU C# as the reference for tests. |
| Screen light maths | `ScreenLight.cs` (shapes, ring geometry, Kelvin → RGB, auto-tune dead-band) | Pure maths, fully unit-tested — reuse or port and run the same test vectors. |
| Output profiles | `FormatChooser.cs`, `OutputFit.cs` | "Meetings 720p30", "Meetings HD 1080p30", "Streaming 1080p60", 1440p30, 4K30, Vertical 1280p, Vertical HD 1920p. |
| Phone link protocol | _to be written_ — see ios.md §5 | One protocol for USB and Wi-Fi, used by iOS now and Android later, understood by Windows, macOS and Linux. |
| Settings | `Settings.cs` (JSON, per device) | Same keys everywhere; the storage folder is per-OS. |

### 1.2 OpenKamera.Core, file by file

`OpenKamera.Core` targets `net10.0-windows10.0.26100.0`. About two-thirds of it is plain C# and runs
unchanged on macOS and Linux once it is moved into a project that targets plain `net10.0`.

| File | Lines | Portable? | Why / what replaces it |
| --- | ---: | --- | --- |
| `BackgroundEffects.cs` | 214 | ✅ yes | Pure pixel maths (masked blur, spotlight, retouch). CPU path; GPU version per platform for speed. |
| `BackgroundPipeline.cs` | 45 | ✅ yes | Orchestration only. |
| `Donation.cs` | 48 | ✅ yes | |
| `FormatChooser.cs` | 41 | ✅ yes | Works on a plain list of modes. |
| `FpsCounter.cs` | 34 | ✅ yes | |
| `FrameTransform.cs` | 51 | ✅ yes | Zoom/pan/crop/mirror/rotate maths. |
| `ImageFilters.cs` | 165 | ✅ yes | Reference implementation for shaders. |
| `Lut3D.cs` | 82 | ✅ yes | `.cube` parser + trilinear lookup. |
| `Models.cs` | 55 | ✅ yes | Model metadata. |
| `OutputFit.cs` | 42 | ✅ yes | Cover/contain fit maths. |
| `SceneCompositor.cs` | 152 | ✅ yes | Layer maths; text drawing is done by the UI layer per platform. |
| `Scenes.cs` | 313 | ✅ yes | JSON + zip, only `System.*`. Check the library root folder per OS. |
| `ScreenLight.cs` | 260 | ✅ yes | Pure maths. |
| `Settings.cs` | 69 | ✅ mostly | Uses the per-user app-data folder; map to `~/Library/Application Support/OpenKamera` (macOS) and `$XDG_CONFIG_HOME/openkamera` (Linux). |
| `PersonSegmenter.cs` | 206 | ⚠ partly | Uses ONNX Runtime API, which is cross-platform, but the package is `Microsoft.Windows.AI.MachineLearning` (Windows ML / DirectML). Swap to the `Microsoft.ML.OnnxRuntime` package (CPU, CoreML EP on macOS) or `Microsoft.ML.OnnxRuntime.Gpu` (CUDA on Linux) behind a small `IExecutionProviderFactory`. |
| `ChromaKey.cs` | 53 | ✅ yes | Green/blue key with similarity, smoothness, spill; brightness-normalised. Port to a shader for speed. |
| `Stabilizer.cs` | 84 | ✅ yes | "Steady picture": block-match shift, 5 % crop, relaxes to follow real moves. |
| `LookPresets.cs` | 24 | ✅ yes | Saved looks (enhance, filter, LUT, background, mirror). |
| `AutoFramer.cs` | 102 | ⚠ partly | `AutoFramer` class is pure maths (dead zone, 2 s hold, steady face size) — portable. `FaceTracker` in the same file uses `Windows.Media.FaceAnalysis`; replace with Vision (Apple) or an ONNX face detector (Linux). Split the file. |
| `DeviceUsage.cs` | 84 | ❌ no | "Who has my camera?" reads the Windows privacy-indicator usage record. macOS: no public API (show only our own clients). Linux: scan `/proc/*/fd` for `/dev/video*`. |
| `VirtualCameraChannel.cs` | 92 | ❌ no | Named shared memory `Global\OpenKameraVirtualCamera` (magic `OKVC`, 256-byte header, BGRA up to 1920×1080). Named maps are Windows-only in .NET. Keep the **header layout**, replace the transport: a POSIX shm file / memfd on Linux, an IOSurface + XPC on macOS. |
| `CameraCatalog.cs` | 23 | ❌ no | `Windows.Devices.Enumeration`. Replace per platform. |
| `CameraController.cs` | 61 | ❌ no | `Windows.Media.Devices` (focus, exposure, WB…). Replace per platform. |
| `CaptureSession.cs` | 123 | ❌ no | `MediaCapture` / `MediaFrameReader`. Replace per platform. |
| `FrameProcessor.cs` | 50 | ❌ no (thin) | Works on `SoftwareBitmap`. Rewrite over a plain `Span<byte>` BGRA frame; logic is small. |
| `BackgroundLibrary.cs` | 185 | ⚠ partly | Image decode uses `Windows.Graphics.Imaging`. Swap to SkiaSharp (Linux/Avalonia) or ImageIO (macOS). Library logic is portable. |
| `CompositeRecorder.cs` | 196 | ❌ no | `MediaTranscoder` H.264/AAC. Replace with AVAssetWriter (macOS) or GStreamer (Linux). |
| `AudioEngine.cs` | 256 | ❌ no | `Windows.Media.Audio` graph. Replace with AVAudioEngine (macOS) or PipeWire (Linux). The mixer maths (gain, meters, limiter) should be lifted into a portable `AudioMath.cs` first. |

**Recommended refactor before any port starts** (done on Windows, does not change behaviour):

1. Split `OpenKamera.Core` into `OpenKamera.Core` (`net10.0`, the ✅ files) and
   `OpenKamera.Core.Windows` (`net10.0-windows…`, the ❌ files).
2. Introduce small interfaces in the portable project: `ICameraSource`, `ICameraControls`,
   `IFrameSink` (virtual camera), `IRecorder`, `IAudioGraph`, `IImageCodec`, `IExecutionProviderFactory`.
3. Move `FrameProcessor` to operate on a `Frame` struct (`width`, `height`, `stride`, `Memory<byte>` BGRA, timestamp).
4. Run `tests/OpenKamera.Core.Tests` on Linux in CI (GitHub Actions `ubuntu-24.04`) for the portable project.

### 1.3 Recommended architecture per platform

| Platform | UI | Core logic | Capture | Effects | Virtual camera | Virtual mic | Recording |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Windows (today) | WinUI 3 | OpenKamera.Core (C#) | MediaCapture | Windows ML / DirectML | C++ `IMFMediaSource` via `MFCreateVirtualCamera` | planned | MediaTranscoder |
| macOS | SwiftUI + Metal | Swift port of the portable maths (tests share vectors with C#) | AVFoundation | Core Image + Metal, Vision or Core ML | CoreMediaIO Camera Extension | Audio Server plug-in | AVAssetWriter |
| iOS | SwiftUI | Shares a Swift package with macOS | AVFoundation | (none on phone at first) | n/a — sends video to the desktop | sends audio to the desktop | n/a |
| Linux | Avalonia 11 (.NET) | OpenKamera.Core (portable part) as-is | PipeWire camera portal / V4L2 | ONNX Runtime (CUDA / ROCm / OpenVINO / CPU) + SkiaSharp / OpenGL | v4l2loopback (and later PipeWire virtual node) | PipeWire null sink + remap source | GStreamer |

Why Swift on macOS instead of .NET: camera extensions, audio plug-ins, Metal and Vision are first-class only
from Swift/Objective-C; the system extension must be a native bundle anyway; and the iOS app shares the code.
Why .NET on Linux: there is no single native toolkit everyone uses, the portable Core already works there,
and Avalonia gives one UI codebase for X11 and Wayland.

---

## 2. Phased plan (all platforms)

| Phase | Goal | Platforms | Exit criteria |
| --- | --- | --- | --- |
| 0 | Core split + interfaces (section 1.2) | Windows | Windows app unchanged; portable tests green on Linux CI |
| 1 | Phone link protocol v1 written down (ios.md §5) and a test receiver in Windows app | Windows | Spec in `docs/phone-link.md`, golden test captures |
| 2 | iOS companion app MVP: Wi-Fi + USB to Windows | iOS, Windows | TestFlight build streams 1080p30 under 120 ms glass-to-glass over USB |
| 3 | Linux app MVP: preview, blur/replace, looks, framing, v4l2loopback output | Ubuntu, Fedora | Flatpak on a test remote; works in a browser meeting and in a streaming-studio app |
| 4 | macOS app MVP: preview, effects, camera extension | macOS | Notarised DMG; camera shows up in browsers and meeting apps |
| 5 | Audio mixer + virtual mic, recording, scenes everywhere | macOS, Linux | Parity rows below go green |
| 6 | iOS app on the App Store; phone receivers on macOS and Linux | all | iPhone works as a camera on all three desktops |
| 7 | Android companion app (same protocol) | Android | — |

---

## 3. Feature-parity matrix

Legend: ✅ done and verified · 🟡 partly done · ⬜ planned · ⚠ limited by the platform · — not applicable.
Windows column reflects `ROADMAP.md` as of the sync date above. Other columns are the **plan**
(what is achievable and how hard), since no other port has code yet.

| Feature | Windows | macOS (plan) | Linux (plan) | iOS companion |
| --- | --- | --- | --- | --- |
| Built-in / USB webcams, picker, hot-plug | ✅ | ⬜ easy (AVCaptureDevice.DiscoverySession) | ⬜ easy (portal / V4L2 + udev) | — |
| Hardware controls (focus, exposure, WB, zoom…) | ✅ (auto/manual toggles ⬜) | ⚠ limited: built-in cams expose few controls; UVC controls need IOKit/USB | ⬜ V4L2 controls are rich | ✅ planned, full manual control of iPhone lenses |
| Software tone/colour adjustments | ✅ | ⬜ Core Image / Metal | ⬜ shader or CPU | — |
| Profiles (720p30 … 4K30, vertical) | ✅ | ⬜ | ⬜ | ⬜ phone picks matching mode |
| Zoom / pan / crop / mirror / rotate | ✅ | ⬜ | ⬜ (FrameTransform.cs reused) | ⬜ optical lens switch |
| Auto framing (Auto / Smart, steady face size) | ✅ | ⬜ Vision face rects + `AutoFramer` maths | ⬜ ONNX face detector + `AutoFramer.cs` | ⬜ can run on phone (Vision) |
| Steady picture (software stabilisation) | ✅ | ⬜ port `Stabilizer.cs` to Metal | ⬜ `Stabilizer.cs` reused | ⬜ phone stabilisation as an option |
| Chroma key (green/blue) | ✅ | ⬜ Metal kernel from `ChromaKey.cs` | ⬜ `ChromaKey.cs` reused | — |
| Look presets (save/recall a look) | ✅ | ⬜ same JSON | ⬜ `LookPresets.cs` reused | — |
| Who has my camera? | ⬜ (`DeviceUsage.cs` in progress) | ⚠ no public API | ⬜ `/proc` scan | — |
| Person segmentation (Fast / High) | ✅ | ⬜ Vision person segmentation or Core ML (converted models) | ⬜ ONNX Runtime with same models | ⬜ optional on-phone later |
| Blur / replace / remove background | ✅ | ⬜ | ⬜ | — |
| Spotlight, retouch | ✅ | ⬜ | ⬜ | — |
| Built-in looks + `.cube` LUTs | ✅ | ⬜ CIColorCube | ⬜ Lut3D.cs + GL 3D texture | — |
| Scenes `.okscene` (templates, layers, editor, switching) | ✅ / 🟡 | ⬜ Swift port of format | ⬜ Scenes.cs reused | — |
| Scenes in recordings/snapshots | ✅ | ⬜ | ⬜ | — |
| Screen light (shapes, ring, looks, Kelvin) | ✅ | ⬜ borderless NSWindow, excluded from capture | ⚠ X11 easy; Wayland needs layer-shell (not on GNOME) | ⚠ phone screen as fill light (front camera only) |
| Monitor brightness (DDC/CI) | ✅ | ⚠ external displays only, private-ish API; built-in via IOKit | ⬜ `ddcutil` (needs i2c group) | — |
| Virtual camera | ⬜ (C++ media source in progress) | ⬜ CoreMediaIO Camera Extension | ⬜ v4l2loopback; PipeWire node later | — |
| Audio mixer, meters, noise suppression | ⬜ (`AudioEngine.cs` in progress) | ⬜ AVAudioEngine + RNNoise | ⬜ PipeWire + RNNoise | ⬜ phone mic as a source |
| Virtual microphone | ⬜ | ⬜ Audio Server plug-in | ⬜ PipeWire null sink + remap source | — |
| Recording (H.264/AAC MP4) | ✅ (scenes 🟡) | ⬜ AVAssetWriter, HEVC too | ⬜ GStreamer (VA-API / NVENC / x264) | ⬜ local ProRes/HEVC backup recording later |
| Phone as camera | ⬜ | ⬜ receiver | ⬜ receiver | ⬜ sender (this is the iOS app) |
| Livestreaming (RTMP) | ⬜ | ⬜ | ⬜ | — |
| Hotkeys, tray / menu bar | ⬜ | ⬜ menu-bar extra | ⚠ global hotkeys need the GlobalShortcuts portal on Wayland | — |
| Settings per device | ✅ | ⬜ | ⬜ | ⬜ |

---

## 4. How to keep these docs current

When the Windows session finishes a feature:

1. Copy the new tick from `ROADMAP.md` into the Windows column above.
2. If the feature added a new Core file, add it to the table in §1.2 with its portability.
3. If it changed a shared contract (scene JSON, profiles, settings keys, virtual-camera header), note it
   in each platform doc's "feature mapping" table and in its risks section.
4. Update the "Last synced" date at the top.
5. Update the website `website/index.html` (feature cards and the parity table under `#roadmap`).
   It is a single static file in the app's Darkroom Glass style and can be served with GitHub Pages.
