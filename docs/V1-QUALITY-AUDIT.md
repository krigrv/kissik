# KISSIK v1: QA checklist and "best in the world" audit

Date: 2026-11-02. Two parts: (1) what was tested and what broke, (2) for each feature, what we do now,
what the best known approach is, and what we change so it is the best we can make it.

Licence rule for every upgrade: only permissive licences (MIT, BSD, Apache-2.0). GPL code or models would
force all of KISSIK to stay GPL and rule out a closed Premium tier later.

## Part 1: QA pass

Method: a UI Automation crawler listed all 91 controls, pressed every safe one in the running app, closed
whatever it opened, and checked the app stayed alive, responsive (all under 170 ms) and crash-free.
Menus were walked item by item. Destructive or outward actions (Record, Go live, Delete, Donate,
file pickers) are tested by hand, below.

| Area | Result |
|---|---|
| Top bar: Snapshot, Meeting camera, Visible (privacy), Light, Output profile, menu | Pass |
| Camera tab: background tiles, edge quality, green screen, spotlight, retouch, adjustments, looks, LUT, presets, camera hardware | Pass |
| Framing: manual/auto/smart, mirror, rotate, reset, steady picture | Pass |
| Scenes: all cards select, search, new scene | Pass |
| Menu: captions on/off, vertical copy, updates, window picture, View submenu | Pass after fixes |
| Phone: flip, connect/reconnect | Pass (flip tested on a real phone) |

Bugs found and fixed (commit c60737c):
1. Scene cards were read aloud as "OpenKamera.Core.SceneDocument": now their name and category.
2. The adjustments expander had no accessible name.
3. "Check for updates" showed a raw 404 when no release exists: now "You have the latest version".
4. Live captions never said "off", and a quick on/off could leave them running with the switch off.
5. Profiles above the phone's reported best were blocked; browsers can under-report, so they are now labelled.
6. Main actions moved to the centre of the top bar (user request).

Still to test by hand before v1 (needs a person at the PC):
- [ ] Record → pause → resume → stop → trim; vertical copy appears
- [ ] Go live to a test RTMP key; stop cleanly
- [ ] Real phone over USB; unplug/replug; screen locks
- [ ] Second webcam picture-in-picture; unplug the main camera mid-call
- [ ] Global hotkeys (Ctrl+Alt+H/M/R/1-9) from another app; tray menu
- [ ] Meeting camera seen in Teams, Zoom and Meet at 720p and 1080p
- [ ] Narrator walk-through of every screen; keyboard-only use; 200% text scale; high contrast
- [ ] 2-hour recording soak: memory flat, audio in sync

## Part 2: feature by feature: are we the best?

### Background removal / blur / replace
- **Now:** MediaPipe selfie (fast) and MODNet matting (high) on the GPU via DirectML, temporal smoothing,
  stray-blob removal, blur at quarter size.
- **Best known:** Robust Video Matting (recurrent, best hair and motion) is GPL-3.0, so ruled out.
  The top results come from combining a matting model, a guided filter at full resolution (crisp hair
  without halo), light wrap (room colour spills onto the edge like a real shot) and motion-aware
  temporal smoothing (steady when still, responsive when moving).
- **Upgrade:** add a fast guided filter for edges, light wrap, motion-aware smoothing and
  a "Studio" quality that runs MODNet at full 512 on capable GPUs. Background blur becomes lens-like
  (bokeh, depth falloff) instead of a flat box blur. Measure against hand-cut test frames (edge error).

### Noise removal (mic)
- **Now (done):** per-mic noise removal, picked in each mic card: "Light" (RNNoise, the default) or "Studio voice"
  (DeepFilterNet 3 low-latency), plus 80 Hz low cut, de-esser and auto level toward −16 LUFS (BS.1770 K-weighted)
  with a −1 dBFS peak guard, all at 48 kHz before the mix. Changes apply live, without restarting audio. Each mic
  card shows how many dB are being removed. Measured on the dev PC (16 threads), per mic: Light ≈ 1 % of one
  core and no extra delay; Studio voice ≈ 6 % of one core and 10 ms (plus the graph's 10 ms quantum). On spoken
  test audio, noise in pauses drops 21 dB (Light) / 22 dB (Studio voice) with the voice level kept within 1 dB.
  Studio voice errors pass audio through instead of closing the app. x64 and x86; ARM64 needs the ARM64 build tools.
- **Before:** none (mixer, gain, mute, watchdog only).
- **Best known (open):** DeepFilterNet (MIT/Apache) beats RNNoise (BSD) clearly; RNNoise uses
  far less CPU.
- **Upgrade:** DeepFilterNet as "Studio voice", RNNoise as the light option, both on-device. Add a
  low-cut filter, a de-esser and auto-gain so every voice lands at broadcast loudness.

### Live captions
- **Now:** Vosk, offline, near-instant, weaker with accents and noise.
- **Best known:** Whisper-family models (MIT) are far more accurate but slower (about 3 s with streaming).
- **Upgrade:** two modes. "Instant" (Vosk) and "Accurate" (whisper.cpp on the GPU), with Vosk drafting
  words live and Whisper correcting them a moment later. Add translation captions and saved transcripts.

### Auto-framing and smart zoom
- **Now:** face tracking with smoothing.
- **Best:** framing that obeys camera rules: headroom, rule of thirds, slow ease-in, no hunting when
  you lean, and groups (two people get framed together).
- **Upgrade:** dead-zone plus critically damped motion, group framing, "presenter" mode that follows
  you across a whiteboard.

### Phone as camera
- **Now:** Wi-Fi (browser page or native app) and USB, flip, torch, zoom, battery.
- **Best:** full manual camera (focus, exposure, white balance, lens choice: ultra-wide/tele),
  HEVC/H.264 hardware streaming instead of JPEG frames (sharper at a fraction of the bandwidth), and sub-100 ms
  latency over USB.
- **Upgrade:** hardware video stream in the native app, manual controls, and lens picker. The Android session owns this work.

### Picture quality (looks, retouch, light)
- **Now:** looks, LUTs, retouch, spotlight, screen light, exposure warning.
- **Best:** automatic exposure and white-balance correction from the face, skin-tone-safe retouch,
  low-light denoise.
- **Upgrade:** "Auto best picture" button: one press measures face brightness, colour and noise and fixes all three.

### Recording, streaming, scenes
- **Now:** segmented crash-safe recording, pause, trim, vertical copy, RTMP multi-destination, 105 scenes, 43 backgrounds.
- **Best:** hardware encoders (NVENC/QSV/AMF) at 4K60, separate audio tracks, instant replay buffer,
  animated scene transitions, video backgrounds.
- **Upgrade:** pick the hardware encoder automatically, add a replay buffer, transitions and video backgrounds.

### Accessibility and universal design
- **Now:** Windows controls, light/dark/high contrast, named controls, keyboard shortcuts, colour-blind preview.
- **Best:** every action reachable by keyboard and voice, screen-reader announcements for every state change,
  large-target mode, reduced motion, captions and sounds for every alert.
- **Upgrade:** full Narrator audit, "Large controls" view option, spoken status for record/live/privacy,
  and a first-run choice of "Simple" (5 big buttons) or "Studio" (everything).

### Performance
- **Now:** effects on the CPU after segmentation; about 11 fps at 4K with blur.
- **Best:** the whole frame path on the GPU (Direct3D 11 compute), zero copies to the CPU.
- **Upgrade:** GPU frame path. Biggest single gain; required for 4K60.

## Order of work
1. Hand tests above (blocker for v1).
2. Background edges (guided filter, light wrap, motion-aware smoothing) and lens blur.
3. Noise removal (DeepFilterNet / RNNoise).
4. Accessibility audit and Simple mode.
5. GPU frame path.
6. Captions accurate mode, framing rules, phone hardware stream, auto best picture, recording upgrades.
