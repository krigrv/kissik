# KISSIK for iPhone — the phone-as-webcam companion app

Status: **plan, no code yet.** This is the app that donations fund. It turns an iPhone into a
high-quality camera (and microphone) for the KISSIK desktop app on Windows, macOS and Linux.
The desktop app does all effects; the phone's job is to capture the best possible picture and get it
to the desktop with low, stable latency.

## 1. Targets and toolchain

| Item | Choice |
| --- | --- |
| Minimum iOS | 17.0 (iPhone XS and newer); tested on the two newest iOS majors |
| Language / UI | Swift 6, SwiftUI; AVFoundation for capture; VideoToolbox for encode; Network.framework for transport |
| Shared code | `apple/Packages/OpenKameraKit` (`OKPhoneLink`, settings models) — see macos.md §2 |
| Third-party | None required. Optional: WebRTC (BSD) only if browser-receiver mode is built (§5.4) |
| Bundle id | `org.openkamera.ios` |

## 2. Repository layout

```
apple/
  iOS/
    OpenKameraPhone/
      App/              # SwiftUI app, onboarding, donate screen
      Capture/          # CaptureController (AVCaptureSession), LensCatalog, ManualControls
      Encode/           # VideoEncoder (VTCompressionSession), AudioEncoder (AAC / Opus)
      Link/             # USBListener, WiFiListener (Bonjour), Pairing (QR), Session
      Health/           # ThermalGovernor, BatteryMonitor
      UI/               # Viewfinder, control sheets, status HUD
    OpenKameraPhoneTests/
    OpenKameraPhoneUITests/
  Packages/OpenKameraKit/Sources/OKPhoneLink/   # protocol shared with desktop receivers
```

## 3. Capture

- `AVCaptureSession` with `sessionPreset = .inputPriority`; choose an `AVCaptureDevice.Format` matching the
  desktop profile (720p30, 1080p30, 1080p60, 4K30; vertical profiles rotate on the desktop, not the phone).
- **All lenses:** list with `AVCaptureDevice.DiscoverySession(deviceTypes: [.builtInUltraWideCamera,
  .builtInWideAngleCamera, .builtInTelephotoCamera, .builtInTripleCamera, .builtInDualWideCamera,
  .builtInTrueDepthCamera], mediaType: .video, position: .unspecified)`. Offer the virtual multi-lens device
  for smooth zoom (`virtualDeviceSwitchOverVideoZoomFactors`) and the single lenses for fixed looks.
- **Manual controls** (each guarded by `isXSupported`, all inside `lockForConfiguration`):
  - Focus: auto / continuous / locked; `setFocusModeLocked(lensPosition:)`; tap-to-focus via `focusPointOfInterest`.
  - Exposure: auto / custom `setExposureModeCustom(duration:iso:)`; exposure bias; anti-flicker by picking
    shutter multiples of 1/50 or 1/60 s.
  - White balance: auto / locked / Kelvin+tint via `deviceWhiteBalanceGains(for: AVCaptureDevice.WhiteBalanceTemperatureAndTintValues)`.
  - Zoom: `ramp(toVideoZoomFactor:withRate:)`; torch level `setTorchModeOn(level:)`.
  - Stabilisation: `AVCaptureConnection.preferredVideoStabilizationMode` (`.off` by default — it adds latency; offer `.standard`).
  - HDR video / low-light boost where supported; the system's own auto-centring off by default — the desktop already
    has auto-framing and steady picture (done on Windows), so the phone should send a clean, wide, unstabilised picture.
- Controls are driven **from the desktop** too (ROADMAP §1 "Control phone camera settings from desktop"):
  every control is a message in the protocol (§5.3) so the desktop UI can show the same sliders.
- Mic: `AVCaptureAudioDataOutput` (built-in or connected mic), 48 kHz mono/stereo.

## 4. Encoding

| Setting | USB | Wi-Fi |
| --- | --- | --- |
| Codec | HEVC (H.265) Main, fallback H.264 High (receiver says what it can decode) | same |
| Bitrate 1080p30 | 20 Mbit/s CBR-ish | 6–10 Mbit/s, adaptive |
| Bitrate 4K30 | 50 Mbit/s | not offered by default |
| Keyframe | every 2 s + on request | every 1 s + on request |
| B-frames | **off** (`kVTCompressionPropertyKey_AllowFrameReordering = false`) | off |
| Real-time | `kVTCompressionPropertyKey_RealTime = true`, `PrioritizeEncodingSpeedOverQuality = true` | same |
| Low-latency mode | `kVTVideoEncoderSpecification_EnableLowLatencyRateControl = true` (H.264) | same |
| Audio | AAC-LC 128 kbit/s or Opus 64 kbit/s (Opus preferred on Wi-Fi) | same |

Windows receiver decodes HEVC with Media Foundation (needs the HEVC extension; fall back to H.264 if missing),
macOS with VideoToolbox, Linux with VA-API/NVDEC via GStreamer or FFmpeg (`libavcodec`).

## 5. Transport and the phone link protocol (v1)

### 5.1 Paths
| Path | How | Typical glass-to-glass latency target |
| --- | --- | --- |
| **USB** (preferred) | Phone listens on TCP port 51820 on its loopback; desktop connects through the Apple USB multiplexing daemon (`usbmuxd` on macOS/Linux, the Apple Mobile Device service on Windows) — the same "connect to a TCP port on the device over USB" technique the open-source peertalk library uses. Linux: `libimobiledevice` 1.3+ / `libusbmuxd` 2.0+. | ≤ 80 ms at 1080p30 |
| **Wi-Fi** | Phone advertises Bonjour `_openkamera._tcp` (`NWListener` with `NWListener.Service`). Video over TCP by default; UDP + simple FEC as an option for congested networks. | ≤ 150 ms at 1080p30 on 5 GHz |
| **Browser receiver** (later) | WebRTC peer connection, for desktops without KISSIK installed | ≤ 200 ms |

Auto-switch: when both paths are up, prefer USB; if USB drops, continue on Wi-Fi within 1 s with a forced keyframe.

### 5.2 Pairing
1. Desktop shows a QR code (QRCoder is already a Core dependency) containing
   `okam://pair?v=1&host=<name>&id=<desktopId>&k=<32-byte base64url secret>&ip=<ipv4>,<ipv6>&port=51820`.
2. Phone scans with `DataScannerViewController` or `AVCaptureMetadataOutput`.
3. Both sides derive a session key (HKDF-SHA256 over the secret) and authenticate with a challenge.
   Traffic is TLS 1.3 with a pre-shared key (`NWProtocolTLS.Options` + `sec_protocol_options_add_pre_shared_key`);
   no plaintext video on the LAN.
4. Paired desktops are remembered in the Keychain; later connections need no QR. USB connections still require
   a pairing record so another computer cannot silently take the camera.

### 5.3 Wire format (both paths)
Length-prefixed frames over the TLS stream:
```
u32 length | u8 type | u8 flags | u16 reserved | u64 ptsMicros | payload
type 0x01 HELLO    (JSON: protocol version, device model, lenses, formats, codecs, battery, thermal)
type 0x02 CONFIG   (JSON: chosen format, codec, bitrate)
type 0x10 VIDEO    (Annex-B HEVC/H.264 access unit; flag bit0 = keyframe; parameter sets on keyframes)
type 0x11 AUDIO    (AAC ADTS or Opus packet)
type 0x20 CONTROL  (JSON: {"focus":{"mode":"locked","lens":0.42}}, …)
type 0x21 STATE    (JSON: current values + supported ranges, sent on change)
type 0x30 STATS    (JSON: encode ms, queue depth, bitrate, dropped frames, thermal, battery)
type 0x31 KEYFRAME_REQUEST
type 0x7F BYE
```
The spec will live in `docs/phone-link.md` with golden captures so Windows, macOS, Linux and Android
implement the same thing.

### 5.4 Latency rules
- Never queue more than 2 encoded frames on the phone; drop oldest non-key frames when the socket is slow.
- Receiver jitter buffer: 1 frame on USB, adaptive 1–4 frames on Wi-Fi.
- Show a latency meter (ROADMAP "Smooth-first mode + latency meter") using `ptsMicros` against a clock offset
  agreed in HELLO (NTP-style ping exchange).

## 6. Battery, thermal and background behaviour

- Observe `ProcessInfo.processInfo.thermalState` (+ `thermalStateDidChangeNotification`):
  | State | Action |
  | --- | --- |
  | nominal / fair | full profile |
  | serious | drop to 30 fps, lower bitrate 30 %, turn off HDR video, tell desktop (STATS) |
  | critical | 720p30, torch off, show "Phone is hot" on both screens |
- Battery: `UIDevice.isBatteryMonitoringEnabled = true`; send level/state in STATS; desktop shows it
  (ROADMAP "phone temperature/battery"). Recommend charging over USB.
- `UIApplication.shared.isIdleTimerDisabled = true` while streaming; dim the viewfinder after 30 s
  (an "eco screen" that shows only a status dot) to save power and heat.
- **Background:** iOS stops camera capture when the app leaves the foreground (multitasking camera access is
  only for special entitlements on iPad). The app must stay in front. On backgrounding: send BYE-pause, the
  desktop shows its slate, resume on return. Audio-only background mode is not worth the review risk in v1.

## 6b. Lessons from user reviews of phone-webcam apps (2026-11)

From about 40 recent public store reviews of an existing phone-as-webcam app. Most 1-star reviews were
"it worked, then broke after an update". Users ranked: connects every time > set up in 2 minutes > honest pricing.

| Must-have | How we meet it |
| --- | --- |
| Reconnect by itself | Auto-reconnect in under 2 s; USB ↔ Wi-Fi fallback (§5.1); remember lens, mic and settings across reconnects |
| Explain failures in plain words | Desktop "Fix my connection" check: router client isolation, different network, firewall, USB trust/pairing missing, cable is charge-only; manual IP and phone-hotspot options |
| Keep streaming when the screen is off | Eco screen (§6) instead of minimising; clear on-screen notice that iOS needs the app in front |
| Torch from the PC | CONTROL message `{"torch":{"on":true,"level":0.6}}`, with a button in the desktop UI |
| External mic on the phone | List every `AVAudioSession` input (wired, USB-C, Bluetooth) as a choice |
| Everything free | All resolutions, 4K and every lens free; no watermark; tips only (§7) |
| Light on battery | Thermal governor (§6), bitrate presets, dark viewfinder while streaming |
| Simple first run | One onboarding screen with a QR scan; buttons with words, not bare icons |
| Test before release | Every release runs through the device lab (§10) on at least 3 iPhones and both Wi-Fi bands before TestFlight |

## 7. App Store and privacy

Info.plist strings (plain, honest):
- `NSCameraUsageDescription`: "KISSIK uses the camera to send video to your computer."
- `NSMicrophoneUsageDescription`: "KISSIK can send your microphone to your computer."
- `NSLocalNetworkUsageDescription`: "KISSIK finds your computer on the local network to send video."
- `NSBonjourServices`: `["_openkamera._tcp"]`

Review notes:
- Explain that the app is a companion for a free desktop app and give a demo video + a link to the desktop download.
  Provide a test mode that streams to a tiny built-in receiver page so the reviewer can see it work without a PC.
- No accounts, no tracking: App Privacy label "Data Not Collected".
- Donations: tips that unlock nothing may use In-App Purchase consumables ("Tip jar"); an external donation link is only
  allowed under the current storefront rules — safest is **IAP tips** in the App Store build, keeping the
  donation page link on the website. Never gate features behind donations.
- Licence: the app is GPL-3.0. App Store terms and GPL have known friction; as the copyright holder the project can
  publish its own build. Make sure every contributor agrees (contributor licence note in CONTRIBUTING.md) and do not
  include third-party GPL code you do not own.

## 8. TestFlight steps

1. Create the app record in App Store Connect (bundle id `org.openkamera.ios`).
2. Xcode → Product → Archive → Distribute → App Store Connect → Upload.
3. Fill "What to Test", export compliance (uses standard encryption → exempt under the usual category, answer the questionnaire).
4. Internal testers (up to 100) get it at once; external testers need a short Beta App Review.
5. Public link for donors/testers; collect feedback in-app with a "Send logs" share sheet (no automatic upload).

## 9. Numbered build plan

| # | Milestone | Done when |
| --- | --- | --- |
| 1 | Protocol spec `docs/phone-link.md` + golden captures | Reviewed; test receiver in Windows app decodes captures |
| 2 | Capture + viewfinder + lens picker | All lenses selectable on a 3-lens iPhone |
| 3 | HEVC/H.264 encoder + Wi-Fi TCP + QR pairing + Bonjour | 1080p30 to Windows over Wi-Fi, < 150 ms |
| 4 | USB path | 1080p30 to Windows over USB, < 80 ms; 4K30 works |
| 5 | Manual controls from phone and desktop | Every control round-trips; state stays in sync |
| 6 | Audio (phone mic) | Desktop lists "iPhone mic" in mixer, A/V sync within 40 ms |
| 7 | Thermal/battery governor + eco screen | 60-min 1080p30 session without "critical" on a recent iPhone |
| 8 | TestFlight beta | 50+ external testers |
| 9 | App Store release | Approved |
| 10 | macOS + Linux receivers | Same phone works on all three desktops |

## 10. Testing

- Unit: protocol encode/decode, pairing crypto (known-answer tests), thermal governor state machine.
- Device lab: oldest supported iPhone, a mid-range model, newest Pro; iOS current and previous.
- Network: 2.4 GHz vs 5 GHz, busy network (packet loss with Network Link Conditioner), router client isolation (must
  show a clear error and suggest USB).
- Latency: film a running millisecond clock on the desktop screen through the phone and screenshot the preview.
- Soak: 2-hour session; memory flat; no thermal shutdown.

## 11. Risks

| Risk | Mitigation |
| --- | --- |
| App Store rejection (GPL, donations, "minimum functionality") | Built-in demo receiver, IAP tip jar, clear review notes |
| USB multiplexing on Windows needs the Apple device service installed | Detect and explain; Wi-Fi always works |
| Linux USB needs `usbmuxd` running and pairing trust | Document in linux.md; fall back to Wi-Fi |
| Client-isolated / corporate Wi-Fi blocks discovery | Manual IP entry, USB, hotspot instructions |
| HEVC not decodable on some Windows PCs | Negotiate in HELLO, fall back to H.264 |
| Phone overheats in a long meeting | Thermal governor, eco screen, suggest a stand away from the sun |
