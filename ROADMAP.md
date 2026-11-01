# OpenKamera — Feature roadmap

Everything OpenKamera aims to do, in build order. Tick `[x]` only with
evidence (a test, or verified in the live app). OpenKamera is an independent
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
- [ ] Commands and caption buttons share one row (custom drag region is a 300 px strip today)
- [x] Profiles menu: Meetings 720p30, Meetings HD 1080p30, Streaming 1080p60, 1440p30, 4K30, Vertical 1280p, Vertical HD 1920p (all unlocked; camera + recording follow it)
- [x] Gallery window, tabs **Scenes / Backgrounds / Filters**: collections (All, Favourites, categories), create/import/add, per-item "…" menu
- [x] Filters gallery shows live thumbnails of the current camera through each filter
- [ ] More looks, our own designs and names: muted background, punchy colour, golden warmth, neutral/no-correction, two-tone, retro film, film print, green tint, teal-and-orange, chroma-key helper, colour-vision simulations (deuteranomaly, protanomaly, tritanomaly), exposure false-colour checker
- [ ] Light + dark theme following Windows

## 0b. Screen light (user request 2026-11-02) — own names and looks
- [ ] Turn the screen into a key light: full-screen always-on-top light window(s) around the edges
- [ ] Looks with descriptions (e.g. Daylight, Golden hour, Neon, Cool studio) — own presets, own thumbnails
- [ ] Size %, colour temperature in Kelvin (warm→cool gradient slider) + custom colour
- [ ] Shapes: side bars, top bar, frame, circle (ring)
- [ ] Light brightness (SDR → HDR boost on HDR displays) and screen brightness control
- [ ] Auto-tune: measure face exposure from the camera and adjust brightness/temperature automatically
- [ ] Lights on/off master switch; per-display choice

## 1. Camera sources and connections
- [x] P1 Built-in laptop cameras (any UVC device via MediaCapture)
- [x] P1 External USB webcams (verified: 4K VeoCam)
- [x] P1 Select between connected devices (picker + hot-plug watcher)
- [ ] P1 Recently connected device list
- [ ] P5 Phone as webcam — own open-source phone side: browser page first (WebRTC), native Android app later
- [ ] P5 Phone over USB (adb port-forward) and Wi-Fi
- [ ] P5 Control phone camera settings from desktop (lens, focus, exposure, flash, stabilisation)
- [ ] P2 DSLR/mirrorless/action cameras — via their UVC webcam mode or an HDMI capture card (both appear as normal sources)
- [ ] P2 HDMI capture-card sources (UVC; same path)
- ⚠ Apple Continuity Camera — macOS-only API; needs a Mac build
- [ ] P2 Multiple cameras in one scene (overhead, side, face, PiP)
- [ ] P5 Virtual camera output for Zoom/Meet/Teams/Discord/OBS (C++ IMFMediaSource; Windows 11 `MFCreateVirtualCamera` needs no driver)

## 2. Camera and image controls
- [x] P1 Hardware controls from device ranges (focus, zoom, exposure, WB, brightness, contrast, hue, pan/tilt, backlight)
- [ ] P1 Auto/manual toggles for focus, exposure, white balance
- [ ] P1 ISO/gain, shutter, tint, anti-flicker (50/60 Hz) where the device exposes them
- [x] P1 Software tone/colour: brightness, contrast, gamma, hue, saturation, vibrance, temperature, tint, sharpness — live on preview + snapshots (tests; live B&W snapshot 240/240 grey samples)
- [ ] P1 Low-light compensation (software gain + denoise)
- [x] P1 Output resolution, frame rate, aspect ratio honoured by capture and recording (2026-11-02: FormatChooser picks the camera mode; output cover-fit to profile; live VeoCam Meetings → 1280×720@30 NV12, Vertical HD → 2592×1944 crop to 1080×1920)
- [x] P1 4K and 60 fps where the device offers them (profiles exist and select 4K/60 modes; 4K live recording not yet measured)

## 3. Framing and transformations
- [x] P1 Digital zoom, pan, crop (tests; live UI)
- [x] P1 Horizontal mirror, 90° rotation (tests; live UI)
- [ ] P2 Automatic face framing (face detector) with smoothing
- [ ] P2 Auto-framing keeps face size constant (zoom follows)
- [ ] P2 Smart Zoom: crop from the highest source resolution, output at profile size
- [ ] P2 Software video stabilisation
- [ ] P1 Saved profiles (resolution + fps + aspect) and custom profiles

## 4. Backgrounds, appearance, effects
- [x] P2 Person segmentation (2026-11-02): MediaPipe Selfie (Fast) + MODNet (High), Windows ML/DirectML on GPU, temporal smoothing, stray-blob removal (chairs), auto-levels for dark rooms. 720p gate: Fast 28–34 fps, High 33–71 fps. Live VeoCam 1080p: 19–49 ms/frame on GPU.
- [x] P2 Portrait blur ↔ privacy blur (one strength slider), halo-free masked blur
- [x] P2 Background replace (8 self-generated backgrounds + user import, cover-fit), remove (transparent)
- [ ] P2 Mask: hair-level refinement (guided filter) and second-person support tuning
- [ ] P2 Live preview shows transparency as a checkerboard in Remove mode
- [ ] P2 Chroma key (green screen) with similarity/smoothness/spill
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
- [ ] P3 Capture mics (built-in, USB, Bluetooth), phone mic
- [ ] P3 System audio (WASAPI loopback) and per-app capture (Windows 11 process loopback; ⚠ not on Windows 10)
- [ ] P3 Mixer: per-source volume, mute, meters, input delay
- [ ] P3 Noise suppression (RNNoise, BSD)
- [ ] P3 Master volume, limiter, 4-band EQ
- [ ] P5 Virtual microphone output

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
- [ ] P1 Keyboard shortcuts; pause/resume feed
- [ ] P4 Tray flyout with global shortcut
- [ ] P4 Elgato Stream Deck plugin (scenes, camera, mute, stream)
- [ ] P4 MCP server so AI agents (e.g. Claude) can switch scenes and adjust the camera

## 10. Compatibility
- [ ] P5 Works in Zoom, Meet, Teams, Slack, Discord, OBS, Streamlabs, browsers — follows from the virtual camera + mic

## 11. Organisation
- [x] P1 Settings saved per device
- [ ] P1 Settings presets
- [ ] P2 Unified scene/background/filter galleries
- [ ] P1 Diagnostic log export, in-app feedback link (GitHub issues)
- ⚠ Licensing tiers, activation limits, enterprise management, paid SDK — not applicable: OpenKamera is free and open source, everything unlocked (4K included)
- [ ] P5 Mac build — decide later (WinUI is Windows-only)
