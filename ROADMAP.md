# KISSIK — Feature roadmap

Everything KISSIK aims to do, in build order. Tick `[x]` only with
evidence (a test, or verified in the live app). KISSIK is an independent
project: all code, art, scenes, looks and names are our own — see
[CONTRIBUTING.md](CONTRIBUTING.md).

Legend: **P1** core studio · **P2** effects/scenes · **P3** audio/record ·
**P4** streaming/automation · **P5** phone + virtual devices ·
⚠ = blocked or not possible as stated (reason given).

## 0. UI shell — "Darkroom Glass" design (approved 2026-11-02)
Live video fills the window; controls float over it on glass panels (warm
accent #FFD36B). Fluent icons, our own scene art, backgrounds and names.
Shell v1 (three-pane) done 2026-11-02, restyled to Darkroom Glass the same day.
Darkroom Glass verified live 2026-11-02 (window pictures via the in-app "Save a picture of this window", VeoCam connected).
- [x] Full-bleed video with vignette; glass panels (in-app acrylic, 22 px corners, 1 px light edge); gold accent on sliders, toggles, tab pills and selection
- [x] Top capsule: serif wordmark, Snapshot, Record (turns red with a running timer), Stream (disabled until P4), profile chip ("Meetings 720p 30fps"), 🙏 Donate, view options (show/hide panels, save window picture, gallery)
- [x] Left sheet: Camera / Audio / Layers; Source row (status dot, device, Connect, live mode text), AF/MF, Background picture tiles (Off/Blur/Replace/Remove, checkerboard) + strength, edge quality, Spotlight, Retouch, Adjustments, Look strip of live filter previews + "+ LUT", LUT, Camera hardware
- [x] Bottom-left audio card (mic name; level bars wait for P3), right Framing panel (minimap, zoom/pan, Manual/Auto/Smart, Mirror/Rotate/Reset), bottom filmstrip of scene thumbnails with Ctrl N badges and a "+" new-scene tile, HUD pills (REC, resolution/fps, effects ms), status toast
- [ ] Minimap shows the live camera image behind the crop box
- [x] Panels must not cover the video: preview fits between the panels (whole frame), and grows when panels are hidden
- [ ] Commands and caption buttons share one row (custom drag region is a 300 px strip today)
- [x] Profiles menu: Meetings 720p30, Meetings HD 1080p30, Streaming 1080p60, 1440p30, 4K30, Vertical 1280p, Vertical HD 1920p (all unlocked; camera + recording follow it)
- [x] Gallery window, tabs **Scenes / Backgrounds / Filters**: collections (All, Favourites, categories), create/import/add, per-item "…" menu
- [x] Filters gallery shows live thumbnails of the current camera through each filter
- [ ] More looks, our own designs and names: muted background, punchy colour, golden warmth, neutral/no-correction, two-tone, retro film, film print, green tint, teal-and-orange, chroma-key helper, colour-vision simulations (deuteranomaly, protanomaly, tritanomaly), exposure false-colour checker
- [ ] Light + dark theme following Windows (in progress 2026-11-02)

## 0b. Screen light (user request 2026-11-02) — own names and looks
Built 2026-11-02: capsule **Light** button opens a glass panel; light windows are
borderless, always-on-top, non-activating, shaped with a window region (clicks
pass through outside the light) and excluded from screen capture.
- [x] Screen as a key light, per display (light window seen live at 2560×1440)
- [x] Looks with descriptions: Daylight, Golden hour, Soft white, Cool studio, Candlelight, Neon magenta, Teal glow (own presets, generated previews)
- [x] Size %, colour temperature 2200–9000 K on a warm→cool track, custom colour picker
- [x] Shapes: side bars, top bar, frame, ring (geometry unit-tested)
- [x] Light brightness; monitor brightness over DDC/CI (read live; restored when the light turns off; laptop panels not supported yet)
- [x] Auto-tune: centre-of-frame luma drives light brightness with a dead-band and step limit (unit-tested; not yet watched live)
- [x] Lights on/off; display checkboxes; never starts lit
- [x] Ring light controls: 5 types (Classic, Soft, Halo, Double, LED), direction (all around, left, right, top, bottom, key left/right, custom arc + rotation), diameter, thickness, softness, position (geometry unit-tested; Classic seen live)
- [x] Drawn inside KISSIK's window by default; whole-screen mode is opt-in
- [x] Max brightness: pure white, big thick ring, monitor at 100% via DDC/CI (restored on off), window maximised
- [x] App logo (gold ring light around a lens): docs/logo SVG + PNG, all package icons, AppIcon.ico
- [ ] HDR boost beyond SDR white on HDR displays
- [ ] Laptop panel brightness (WMI) and face-mask-based auto-tune

## 0c. Premium and never-seen-before features (2026-11-02, from docs/PROBLEMS-AND-IDEAS.md)
Build order:
1. [x] Windows 11 system virtual camera (listed in every app, several apps at once) + friendly slate instead of black
2. [x] ★ Who has my camera? (Meeting camera panel lists apps holding the camera/mic; a failed Connect names the app). Auto-connect on launch and a prompt when a meeting uses KISSIK without a camera.
3. [x] ★ Meeting check (Meeting camera → Check my setup: camera, fps, framing, face light / backlight, mic level, meeting camera; worst first with the fix)
4. [x] ★ Privacy shutter hotkey + away slate (mutes mic) — Visible / Blur me / Be right back in the top bar, Ctrl+Shift+H; preview stays normal
5. [x] Phone as camera: QR pairing + HTTPS/WebSocket browser stream (verified on a real phone), fixed port 47820 + one-click firewall rule, lens flip, quality, battery in HUD, phone microphone as a mixer track. Still to do: USB (adb) path
6. [x] ★ Smart mirror (View options → mirror my preview only) and per-app profiles (Preset row → link icon: when Zoom/Teams/… opens a camera, switch to that preset)
7. [x] ★ Eye-line prompter under the webcam (View options → Prompter: top-centre strip, wpm speed, size, mirror, edit; hidden from shares)
8. [x] ★ Privacy guard (shutter menu: blur when a second face stays in shot 0.8 s; back after 3 s alone)
9. [~] ★ Live captions burnt into the video (built on Windows speech; needs "Online speech recognition" on. TODO: fully offline engine)
10. [x] ★ Smooth-first mode + latency meter (effects ms in HUD; auto steps down: lighter edges → pause steady/face tracking → half-size processing when not recording; steps back up with headroom)
11. [~] Auto-framing done; crash-safe recordings done (1-minute pieces joined on Stop, leftovers rescued on launch); phone battery shown in the HUD; phone temperature still to do
12. [ ] iOS app (funded by donations), then the native Android app

## 0d. From phone-webcam app reviews (2026-11-02, ~40 Google Play reviews of a competing app)
What users expect, in order: it connects every time; setup in under 2 minutes (QR + same Wi-Fi); honest pricing
(every resolution free, no watermark, no ads). Most 1-star reviews are "worked, then broke after an update".
1. [~] Auto-reconnect: done for Wi-Fi (same link + certificate across restarts, phone page retries from 250 ms, PC switches to the phone when it returns). Still to do: USB ↔ Wi-Fi fallback
2. [~] USB: one button; explains no adb / no phone (charge-only cable, USB debugging steps) / "Allow USB debugging" prompt / offline. Per-brand tips and iPhone USB still to do
3. [x] "It's not connecting" explains why from what actually reached the PC (never arrived → firewall / Wi-Fi / client isolation / hotspot; old link; certificate refused; camera blocked); other networks' links shown
4. [x] Promise: every resolution free, no watermark, no ads, every computer — README + welcome; plain "Just my name" scene
5. [ ] Never take over the system default mic; virtual mic stays opt-in; the hardware mute key always works
6. [ ] Quit really quits (no hidden background process); clear on/off state
7. [ ] Phone battery/heat governor: lower bitrate when hot, dark phone screen while streaming
8. [x] Latency meter + smooth-first mode
9. [x] One short first-run welcome (3 steps + free promise, once); command bar buttons have words
10. [x] Phone troubleshooter + "Copy diagnostics" (View options; no personal data)
More from reviews of the desktop + iPhone apps (2026-11-02; see docs/research/competitor-reviews.md):
11. [ ] Free on every computer: no per-device licence
12. [ ] Honest money if store builds are ever sold: one-time price, no auto-renew, one-click refunds
13. [ ] Clean uninstall: remove the meeting camera, virtual mic and drivers; a "Remove the KISSIK camera from all apps" button; verify meeting apps no longer list it
14. [ ] Compatibility test matrix per release (meeting apps, Chromium/Firefox/Safari, streaming apps), results published
15. [ ] Opt-in crash reporting
16. [ ] Main app needs no admin rights; admin only for optional virtual devices; short IT/deployment guide
17. [~] Audio-drop watchdog: engine stall or Windows audio error → restart within ~3 s and warn, recording keeps going (done). Still to do: per-mic and phone-audio silence detection
18. [x] Explain resolution limits: camera ("supports up to 1080p, scaled up") and phone ("sending 720p; pick 1080p on the phone")
19. [ ] Public status / known-issues page linked from the troubleshooter
From desktop studio reviews (Windows + Mac):
20. [ ] One meeting camera for every app (meeting, streaming, browser), no extra driver; watchdog shows the slate and recovers if the camera drops mid-stream
21. [ ] Never touch the real webcam's driver: built-in camera still works after install and uninstall (test on laptops)
22. [ ] App and camera/mic plug-ins ship and update as one version; detect and repair a mismatch; uninstall removes the plug-in
23. [ ] Survive OS updates: current system camera APIs only; test on OS previews before release
24. [ ] 2-hour soak test (auto-framing + effects + recording): memory flat; unplugging a camera never crashes
25. [ ] (Mac) treat the OS's own phone-camera device as a normal source and add our effects on top
Requests: keep streaming with the phone screen off / page in background; PC button for the phone torch; external mic plugged
into the phone as a source; front/back switch always visible on the PC. Keep: QR pairing in minutes, low PC load, control
everything from the PC, switching between paired phones/PCs.

## 1. Camera sources and connections
- [x] P1 Built-in laptop cameras (any UVC device via MediaCapture)
- [x] P1 External USB webcams (verified: 4K VeoCam)
- [x] P1 Select between connected devices (picker + hot-plug watcher)
- [ ] P1 Recently connected device list
- [x] P5 Phone as webcam — browser page over HTTPS/WebSocket (QR pairing, verified on a real phone); native Android app later
- [x] P5 Phone over Wi-Fi and USB (Android: adb reverse to a localhost-only listener, page opens on the phone, no certificate warning)
- [ ] P5 Control phone camera settings from desktop (lens, focus, exposure, flash, stabilisation)
- [ ] P2 DSLR/mirrorless/action cameras — via their UVC webcam mode or an HDMI capture card (both appear as normal sources)
- [ ] P2 HDMI capture-card sources (UVC; same path)
- ⚠ Apple Continuity Camera — macOS-only API; needs a Mac build
- [ ] P2 Multiple cameras in one scene (overhead, side, face, PiP)
- [x] P5 Virtual camera output (C++ media source + Windows 11 `MFCreateVirtualCamera`; installed, harness passes; not yet confirmed inside Zoom/Teams by a user)

## 2. Camera and image controls
- [x] P1 Hardware controls from device ranges (focus, zoom, exposure, WB, brightness, contrast, hue, pan/tilt, backlight)
- [~] P1 Auto/manual focus (AF/MF) done; exposure and white balance follow the device's own controls panel
- [ ] P1 ISO/gain, shutter, tint, anti-flicker (50/60 Hz) where the device exposes them
- [x] P1 Software tone/colour: brightness, contrast, gamma, hue, saturation, vibrance, temperature, tint, sharpness — live on preview + snapshots (tests; live B&W snapshot 240/240 grey samples)
- [ ] P1 Low-light compensation (software gain + denoise)
- [x] P1 Output resolution, frame rate, aspect ratio honoured by capture and recording (2026-11-02: FormatChooser picks the camera mode; output cover-fit to profile; live VeoCam Meetings → 1280×720@30 NV12, Vertical HD → 2592×1944 crop to 1080×1920)
- [x] P1 4K and 60 fps where the device offers them (profiles exist and select 4K/60 modes; 4K live recording not yet measured)

## 3. Framing and transformations
- [x] P1 Digital zoom, pan, crop (tests; live UI)
- [x] P1 Horizontal mirror, 90° rotation (tests; live UI)
- [x] P2 Automatic face framing (Windows face detector) with smoothing, dead zone and hold — Auto/Smart
- [x] P2 Auto-framing keeps face size constant (zoom follows); Auto and Smart modes with a dead zone and 2 s hold
- [~] P2 Smart Zoom: Smart mode zooms up to 3×; picking a higher camera mode for sharper crops still to do
- [x] P2 Software video stabilisation ("Steady picture": block-match shift, 5 % crop, relaxes to follow real moves)
- [ ] P1 Saved profiles (resolution + fps + aspect) and custom profiles

## 4. Backgrounds, appearance, effects
- [x] P2 Person segmentation (2026-11-02): MediaPipe Selfie (Fast) + MODNet (High), Windows ML/DirectML on GPU, temporal smoothing, stray-blob removal (chairs), auto-levels for dark rooms. 720p gate: Fast 28–34 fps, High 33–71 fps. Live VeoCam 1080p: 19–49 ms/frame on GPU.
- [x] P2 Portrait blur ↔ privacy blur (one strength slider), halo-free masked blur
- [x] P2 Background replace (8 self-generated backgrounds + user import, cover-fit), remove (transparent)
- [ ] P2 Mask: hair-level refinement (guided filter) and second-person support tuning
- [ ] P2 Live preview shows transparency as a checkerboard in Remove mode
- [x] P2 Chroma key (green screen) with similarity/smoothness/spill; green or blue, brightness-normalised so shadows key
- [x] P2 Spotlight (dims room, lifts person; % slider)
- [x] P2 Retouch (edge-aware skin smoothing on the person only; slider; light-to-dark skin tones tested)
- [x] P1 Built-in colour filters (B&W, Warm, Cool, Bleach, Cinematic, Vivid, Fade)
- [x] P1 Import `.cube` LUTs (validated, copied to `%APPDATA%\OpenKamera\LUTs`), strength slider — organise/preview comes with the Filters gallery
- [ ] P2 AR effects: our own open hand-gesture emoji overlay and face effects (open ONNX hand/face models)
- [ ] P2 Background and filter galleries

## 5. Scenes, layouts, overlays
- [x] P2 Ready-made templates (7 self-made: Blank, Lower Third, Name Card, Framed, Bubble Cam, Presentation, Corner Logo)
- [x] P2 Custom scene creation, `.okscene` save/load/import/export (zip-slip safe), duplicate, rename, reset/delete
- [~] P2 Layers: text, image/logo, rectangle/circle, placed camera (rect/rounded/circle, outline) — still to do: second camera, screen share, video file, web page (WebView2)
- [x] P2 Scene background (solid or gradient) independent of camera background; removed backgrounds show it
- [~] P2 Layer move (drag, snaps to edges/centre), resize (handle, circles stay round), order, show/hide, properties, undo/redo (Ctrl+Z/Y), Delete key — still to do: alignment guides, groups
- [x] P2 Scenes are in recordings and snapshots (overlay captured from the same XAML as the preview; verified at 4K live, 2026-11-02)
- [ ] P2 Foreground/background layering (elements between you and your background — needs segmentation)
- [ ] P2 Masks (shape/layer), gradient fills, auto-layout stacks
- [~] P2 Scene switching (rail, Ctrl+1…9, category + favourites filter) — animated transitions still to do
- [x] P2 Gallery window: Scenes / Backgrounds / Filters tabs, collections (All, Favourites, categories), live filter previews, create/import/add/duplicate/export/delete
- [ ] P2 Import OBS scene collections (JSON)

## 6. Audio
- [x] P3 Capture mics (built-in, USB, Bluetooth) and the phone mic
- [~] P3 System audio (WASAPI loopback: done, Audio tab → System sound, with echo-loop guard) and per-app capture (Windows 11 process loopback; ⚠ not on Windows 10)
- [~] P3 Mixer: per-source volume, mute, meters done; input delay still to do
- [~] P3 Noise suppression via Windows voice processing (Communications mode); RNNoise still to do
- [x] P3 Master volume, limiter, 4-band EQ
- [~] P2 Meetings hear the mix via a virtual cable (Send to meetings, VB-CABLE auto-picked); our own driver needs a signing certificate

## 7. Recording
- [x] P1 Record and start/stop from the UI (MP4)
- [~] P3 Record what the preview shows (2026-11-02: framing, background effects, filters, LUT, adjustments → H.264 MP4 at profile size; live 6 s recordings at 29.5–29.6 fps) — still missing: scene overlays (need the scene renderer) and audio (P3 audio batch)
- [x] P1 FPS counter in the HUD (processed frames per second) + camera mode + REC timer
- [ ] P1 Choose save location; use output profiles
- [ ] P3 3-2-1 countdown, pause/resume
- [ ] P3 Recording gallery: browse, trim, share
- [ ] P3 Dual-format (landscape + portrait) recording with separate canvases
- [ ] P3 Isolated per-source recordings

## 8. Livestreaming
- [ ] P4 RTMP/RTMPS output (custom destinations)
- [ ] P4 Presets: YouTube, Twitch, Kick, Facebook, Instagram (stream key entry; ⚠ eligibility is the platform's)
- [ ] P4 Multistreaming to several destinations at once
- [ ] P4 Destination manager; bitrate control with warnings
- [ ] P4 Stream panels (chat, info), dockable/detachable
- [ ] P4 Web overlays for alerts/widgets; chat-message layers, highlighted messages
- [ ] P4 Stream history/statistics; landscape + portrait simultaneous streaming

## 9. Controls and automation
- [~] P1 Keyboard shortcuts done (Ctrl+1…9, Ctrl+R, Ctrl+Shift+S, Ctrl+Shift+H); pause/resume feed still to do
- [ ] P4 Tray flyout with global shortcut
- [ ] P4 Elgato Stream Deck plugin (scenes, camera, mute, stream)
- [ ] P4 MCP server so AI agents (e.g. Claude) can switch scenes and adjust the camera

## 10. Compatibility
- [ ] P5 Works in Zoom, Meet, Teams, Slack, Discord, OBS, Streamlabs, browsers — follows from the virtual camera + mic

## 11. Organisation
- [x] P1 Settings saved per device
- [x] P1 Settings presets (save/recall a whole look: enhance, filter, LUT, background, mirror)
- [ ] P2 Unified scene/background/filter galleries
- [~] P1 Copy diagnostics done (View options); in-app feedback link still to do
- ⚠ Licensing tiers, activation limits, enterprise management, paid SDK — not applicable: KISSIK is free and open source, everything unlocked (4K included)
- [ ] P5 Mac build — decide later (WinUI is Windows-only)
