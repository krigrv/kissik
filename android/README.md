# KISSIK Camera for Android

A small native app that turns an Android phone into a camera and microphone for KISSIK on Windows.
Free, open source, no account, no ads, no watermark. Video and sound go only to the PC you paired.

It speaks the same protocol as the phone web page KISSIK already serves, so nothing changes on the PC side.

## What it does

- **Pair once:** scan the QR code KISSIK shows (or paste its link). The PC's self-made certificate is pinned on the
  first connect and checked on every connect after that; nothing else is trusted.
- **Wi-Fi or USB:** if `adb reverse tcp:47821 tcp:47821` is in place (KISSIK's *Connect by USB* does this), the app
  uses `ws://localhost:47821` with no TLS; otherwise `wss://<pc>:47820`.
- **Reconnects by itself** within about 3 seconds after a drop, keeping the lens, quality and mic choice.
- **Screen off is fine:** streaming runs in a foreground service with a clear notification and a Stop button.
- Front/back switch, torch, 480p/720p/1080p, mic on/off (mic is opened only while on, and released straight away).
- **Battery & heat saver** (on by default): drops to 480p when the phone runs hot or battery is under 20% and not charging.
- One short onboarding: three quick pages (what it is, how pairing works, privacy + permissions), then the scanner.

## Protocol (as served by `src/OpenKamera.Core/PhoneServer.cs`)

| Direction | Message |
|---|---|
| phone → PC, binary | JPEG frame |
| phone → PC, binary | `'A'` + uint32 LE sample rate + mono float32 LE samples (20 ms at 48 kHz) |
| phone → PC, text | `{"device","w","h","battery","charging","facing","torch","zoomMax","zoom"}` on connect, on lens/torch/zoom change, and every 15 s. `torch` is null when the lens has no light |
| PC → phone, text | `front`, `back`, `q:480` / `q:720` / `q:1080`, `torch:on` / `torch:off`, `zoom:<factor>` (clamped to the lens) |

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
