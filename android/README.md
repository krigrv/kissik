# KISSIK Camera for Android

A small native app that turns an Android phone into a camera and microphone for KISSIK on Windows.
Free, open source, no account, no ads, no watermark. Video and sound go only to the PC you paired.

It speaks the protocol of the phone web page KISSIK already serves, plus the optional pro controls and H.264 below.

## What it does

- **Pair once:** scan the QR code KISSIK shows (or paste its link). The PC's self-made certificate is pinned on the
  first connect and checked on every connect after that; nothing else is trusted.
- **Wi-Fi or USB:** if `adb reverse tcp:47821 tcp:47821` is in place (KISSIK's *Connect by USB* does this), the app
  uses `ws://localhost:47821` with no TLS; otherwise `wss://<pc>:47820`.
- **Reconnects by itself** within about 3 seconds after a drop, keeping the lens, quality and mic choice.
- **Screen off is fine:** streaming runs in a foreground service with a clear notification and a Stop button.
- **Full resolution:** streams at the phone camera's maximum, up to 4K or whatever the phone supports.
- Front/back switch, torch, zoom, quality, mic on/off (mic is opened only while on, and released straight away).
- **Pro camera controls** (the *Pro* button, or from KISSIK on the PC): every lens the phone exposes (ultra-wide,
  main, telephoto, front), brightness (EV), manual ISO and shutter speed, manual focus or tap-to-focus, white balance,
  frame rate, stabilization and HDR. Controls the open lens doesn't support are hidden.
- **Hardware H.264** when KISSIK asks for it (`codec:h264`): the camera feeds the phone's video chip directly, so 4K
  stays smooth and cool. JPEG stays the default so older KISSIK versions keep working.
- **Battery & heat saver** (on by default): drops to 480p when the phone runs hot or battery is under 20% and not charging.
- One short onboarding: three quick pages (what it is, how pairing works, privacy + permissions), then the scanner.

## Protocol (as served by `src/OpenKamera.Core/PhoneServer.cs`)

### Phone → PC

| Kind | Message |
|---|---|
| binary | JPEG frame (default), upright |
| binary | `'V'` · flags byte (bit 0 = keyframe) · uint64 LE timestamp in µs · H.264 Annex-B. Only after `codec:h264`. Keyframes carry SPS + PPS in front, so decoding can start at any keyframe. Frames are as the sensor sees them: rotate clockwise by `rot` |
| binary | `'A'` · uint32 LE sample rate · mono float32 LE samples (20 ms at 48 kHz) |
| text | status JSON on connect, on any change, and every 15 s (below) |

Status JSON fields. A control the lens lacks is `null` or missing, so the PC can hide it.

| Field | Meaning |
|---|---|
| `device`, `battery` (0..1), `charging`, `facing` | as before |
| `w`, `h` | picture size as shown (after `rot`) |
| `rot` | clockwise degrees the PC must rotate H.264 frames (0 for JPEG) |
| `maxW`, `maxH`, `maxFps` | the lens's largest size and highest frame rate |
| `torch` | bool, or null without a light |
| `zoom`, `zoomMin`, `zoomMax` | zoom factor and range (min below 1 = can widen) |
| `lenses` | `[{id, facing, label}]`, e.g. Ultra-wide / Main / Telephoto / Front |
| `lens` | chosen lens id, or null when following front/back |
| `ev`, `evMin`, `evMax`, `evStep` | exposure compensation index, range and step in EV |
| `manual` | whether manual ISO/shutter work; then `isoMin`, `isoMax`, `shutterMin`, `shutterMax` (ns) |
| `iso`, `shutter` | manual values, or null for automatic |
| `focusManual`, `focus` | manual focus available; `focus` 0 (far) … 1 (nearest), null = auto |
| `wbModes`, `wb` | available white balance modes and the current one |
| `fpsList`, `fps` | frame rates the lens offers; `fps` 0 = automatic |
| `stab`, `hdr` | bool, or null when the lens has none |
| `codecs`, `codec` | `["jpeg","h264"]` and the one in use |

### PC → phone (text)

| Command | Effect |
|---|---|
| `front`, `back` | switch camera (clears a chosen `lens`) |
| `lens:<id>` | use a specific lens from `lenses` |
| `q:<short side>` | capture size, e.g. `q:2160`; the closest the lens supports |
| `torch:on` / `torch:off`, `zoom:<factor>` | as before; zoom clamped to the lens |
| `ev:<index>` | exposure compensation (index × `evStep` = EV) |
| `iso:<n>` / `iso:auto`, `shutter:<ns>` / `shutter:auto`, `exposure:auto` | manual exposure (when `manual`) |
| `focus:<0..1>` / `focus:auto`, `focus:tap:<x>,<y>` | manual focus, or focus + meter at a point (0..1 of the picture as shown) |
| `wb:<mode>` | one of `wbModes` |
| `fps:<n>` / `fps:auto` | frame rate from `fpsList` |
| `stab:on|off`, `hdr:on|off` | stabilization, HDR scene mode |
| `codec:jpeg` / `codec:h264` | video format; JPEG is the default |
| `key` | send an H.264 keyframe now |

## Build

Needs JDK 17 and the Android SDK (compile SDK 36).

```bash
cd android
./gradlew assembleDebug
```

The APK lands in `app/build/outputs/apk/debug/app-debug.apk`. Install with `adb install -r` that file.

## Try it on the emulator

The emulator reaches the PC at `10.0.2.2`. With KISSIK open on its Phone page:

```bash
adb shell am start -n app.kissik.camera/.MainActivity --es link "https://10.0.2.2:47820/?t=<token>"
```

The token is the `t=` value in KISSIK's pairing link. For the USB path instead, run `adb reverse tcp:47821 tcp:47821`.

## Code map

- `Pairing.kt` – parses the QR link, stores the pairing, certificate pinning, USB detection.
- `StreamService.kt` – camera (CameraX), mic, WebSocket, reconnects, status, saver.
- `MainActivity.kt` – screens: onboarding, home (viewfinder + control dock), scanner (ZXing), settings.
- `Ui.kt` – colours and the status chip.

## What was checked

On the Android emulator, against KISSIK's own `PhoneServer` code: JPEG frames (about 29 fps), status every 15 s and on lens
change, mic audio at 48 kHz, the `front`/`back`/`q:` commands, streaming with the screen off, reconnecting after the
PC side restarts, switching to another PC while streaming, and the USB path through `adb reverse`. Not yet tried on a
real phone.
