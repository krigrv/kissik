<p align="center"><img src="docs/logo/openkamera-logo-512.png" width="128" alt="KISSIK logo"></p>

# KISSIK

A free, open-source camera studio for Windows. Make any webcam look its best,
build scenes with your name, logo and slides, and record it all — no account,
no watermark, nothing locked behind a paywall.

> **Our promise:** every resolution and frame rate is free (up to 4K 60), on every
> computer you own. No watermark, no account, no subscription, and never an ad in
> your video. KISSIK may show one small card marked "Sponsored" in its own window;
> it's chosen on your PC (nothing about you is sent) and you can turn it off in
> Settings and help. If you'd like to help, there's a 🙏 Support button — it's
> optional, always.

> **Independent project.** KISSIK is not affiliated with, endorsed by or
> connected to any other camera or webcam software company. All product names
> mentioned anywhere in this project belong to their owners and are used only
> to describe compatibility. See [TRADEMARKS.md](TRADEMARKS.md).

## What it does today

- **Any UVC webcam** — live preview, hot-plug, and the camera's real hardware
  controls (focus, zoom, exposure, white balance, pan/tilt…) with ranges read
  from the device.
- **Background** — blur (portrait to privacy), replace, remove, plus Spotlight
  and Retouch, using on-device AI on your GPU (ONNX Runtime + DirectML).
- **Looks** — brightness, contrast, gamma, saturation, vibrance, hue,
  temperature, tint, sharpness, built-in filters and your own `.cube` LUTs.
- **Framing** — zoom, pan, mirror, rotate.
- **Output profiles** — 720p to 4K, 30/60 fps, and vertical 9:16; the camera
  switches to the matching mode.
- **Scenes** — text, logos, shapes, a placed or circular camera, gradients;
  drag-and-drop editor with undo; share scenes as `.okscene` files.
- **Snapshot and recording** — exactly what you see, as PNG and H.264 MP4, with
  sound; recordings survive a crash.
- **Meeting camera** — a Windows 11 camera for Zoom, Teams, Meet, Discord and OBS.
- **Phone as a camera** — the KISSIK Camera app for Android (in `android/`):
  scan a QR code, up to 4K on back lenses, about 100 ms camera-to-screen at
  60 fps, pro controls (lens, exposure, ISO, shutter, focus, torch, zoom), the
  phone's mic, and its screen dims while it streams.
- **Audio** — mic mixer with meters, EQ, limiter, system sound, watchdog.
- **Screen light, prompter, captions, privacy shutter and guard, auto-framing,
  green screen, presets per app, light and dark mode.**

Also: replay buffer (save the last 30 seconds), scene transitions, video
backgrounds, accurate captions on this PC, your own output profiles, your
details (name, pronouns, logo, your own fields) filled into scenes, start with
Windows, and Ctrl+Alt shortcuts that work from any app. What's next is in
[ROADMAP.md](ROADMAP.md).

## Requirements

- Windows 10 1809 or later (Windows 11 recommended), x64 or ARM64
- [.NET 10 SDK](https://dotnet.microsoft.com/download) and the Windows App SDK 2.6 runtime
- A webcam (any UVC camera works; a GPU is recommended for background effects)

## Build and run

```powershell
dotnet build OpenKamera.slnx -c Release
dotnet test OpenKamera.slnx -c Release
dotnet run -c Release --project src\OpenKamera.App\OpenKamera.App.csproj
```

The app is a packaged WinUI 3 app: start it with `dotnet run` (which registers
a development identity) rather than launching the `.exe` directly. On first
Connect, allow camera access in **Settings → Privacy & security → Camera**.

More detail: [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md).

## Contributing

Contributions are welcome — bug reports, ideas, code, translations, scene
templates. Please read [CONTRIBUTING.md](CONTRIBUTING.md) first; it explains
how to send a pull request and the rules that keep this project clean and
independent. Everyone taking part follows the
[Code of Conduct](CODE_OF_CONDUCT.md).

## Support the project

KISSIK is free. If it helps you, you can donate:

- **PayPal:** https://paypal.me/krishnagaurav
- **UPI (India):** `krigrv@upi` — or scan [docs/donate-upi-qr.png](docs/donate-upi-qr.png)
  with Google Pay, PhonePe, Paytm or BHIM

The in-app **🙏 Donate** button reads these from
[src/OpenKamera.App/donate.json](src/OpenKamera.App/donate.json). Forks should
put their own details there (empty fields hide that option).

## Licence

Copyright © 2026 Krishna Gaurav and KISSIK contributors.

KISSIK is free software under the **GNU General Public License v3.0** — you
may use, study, share and modify it, and versions you distribute must stay open
under the same licence. See [LICENSE](LICENSE). Bundled models and libraries
keep their own licences, listed in [THIRD_PARTY.md](THIRD_PARTY.md).
