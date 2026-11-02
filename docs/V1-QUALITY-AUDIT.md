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
- **Now:** none (mixer, gain, mute, watchdog only).
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

## Part 3: accessibility and universal design checklist

Date: 2026-11-02. Scope: `MainPage.xaml` and its `MainPage.*.cs` partials, `RecordingsWindow`, `PrompterWindow`.
Method: every control in the XAML and in code-built UI was read for its UI Automation Name, HelpText,
size and colours; state changes were traced to what Narrator hears. **Checked in code and build only:**
a live Narrator, 200% text and high-contrast pass on a running app is still open (last section).

### Names and help text
- [x] Every button, toggle, slider, switch and list in the main window has an `AutomationProperties.Name`
      (icon-only buttons included: reset, save/delete/link preset, import/clear LUT, add background).
- [x] Every control with a tooltip now has the same text as `AutomationProperties.HelpText` (78 controls),
      so Narrator reads the hint, not only the name.
- [x] Record button no longer reads "bullet Record": its name is "Record", then "■ Stop" while recording.
- [x] Decorative status dots (source, meeting camera) are hidden from the automation tree.
- [x] Scene cards, adjustments expander: named (fixed in c60737c).
- [x] Recordings window: grid named "Recordings and snapshots"; each card reads file name, kind, date, size.
- [x] Prompter: every toolbar button has Name and HelpText; the script text and editor are named.

### Keyboard
- [x] All main actions are standard WinUI buttons/menus: reachable with Tab, activated with Enter/Space,
      menus with arrow keys. Shortcuts: Ctrl+R record, Ctrl+Shift+R pause, Ctrl+Shift+S snapshot,
      Ctrl+Shift+H hide me (plus global Ctrl+Alt versions).
- [x] Focus order follows reading order: top bar (centre, then right), left panel tabs and content,
      right panel, filmstrip, then Simple-mode buttons when shown.
- [x] Visible focus: the WinUI system focus rectangle is kept on every control (no `UseSystemFocusVisuals="False"` anywhere).
- [x] Scene layers can be moved without a mouse: Ctrl+arrow moves the selected layer 1% of the picture,
      Ctrl+Shift+arrow 10%, Delete removes it. Each is one undo step and Narrator says what happened.
      Ctrl is used because plain arrows already move focus and change sliders and lists.

### Narrator announcements for state changes
- [x] Every status message is now raised as a UIA notification (`RaiseNotificationEvent`, ImportantMostRecent)
      as well as shown, and the status text has `LiveSetting=Polite`. This covers:
  - [x] Record: countdown (Assertive live region), started, cancelled, failed, "Finishing recording…" and the result.
  - [x] Live: started ("Live to N destinations…"), ended, stopped with the reason.
  - [x] Privacy: "Meeting and recording see a blurred picture", "…be right back…", "You're visible again".
  - [x] Mic muted in Windows / unmuted again (watchdog).
  - [x] Captions on / off / unavailable / model download progress; caption text itself is a Polite live region.
- [x] The REC pill text is a Polite live region.

### Target size (at least 40 × 40 px)
- [x] Top bar buttons (`CapsuleButton`): min 40 × 40 (was 38 high). Meeting camera, output profile, menu,
      Support: min 40 high.
- [x] Icon buttons (`IconButton`): 40 × 40 (was 34 × 32). Well buttons: min 40 high (was 32).
- [x] Prompter toolbar: 40 × 40 (was 34 × 30).
- [x] Background tiles 64 high; Simple-mode buttons 148 × 96.
- [x] View → **Large controls** raises every button, switch, list, slider and text box to at least 48 × 48
      with 16 px text, and puts each back exactly when turned off. Saved as `LargeControls` in settings.

### Text scaling (200%) and high contrast
- [x] All text uses WinUI text styles or relative sizes that follow Windows "Text size"; nothing uses
      fixed-height text containers in the top bar now (`Height` → `MinHeight`), so it grows instead of clipping.
- [x] Every theme colour is a Windows theme brush (light, dark and high contrast follow automatically).
- [x] No fixed colours under a contrast theme: warning text (busy camera, meeting camera live, shutter label)
      uses the Windows critical colour; code-built UI picks from the HighContrast theme dictionary; the caption bar,
      the meeting check icons and the prompter switch to the user's own window and text colours. Switching the
      contrast theme while KISSIK is open re-applies them.
- [ ] Left panel is a fixed 316 px wide: at 200% text, long labels wrap (they are set to wrap or trim);
      not yet checked by eye on a running app.

### Motion
- [x] Respects Windows "Animation effects" off (Settings → Accessibility → Visual effects): built-in WinUI
      transitions follow it already; live look previews and scene thumbnails refresh every 10 s instead of 1–2 s.
- [x] No flashing content; the 3-2-1 countdown changes once a second.

### Simple and Studio
- [x] First run, after the welcome: "Simple or Studio?" (Simple is the default button).
- [x] Simple: five big centred buttons — Camera on, Background (off → blur → picture), Record,
      Meeting camera (opens the same meeting-camera panel), Hide me (be right back + mic muted, press again to come back).
      Their words and screen-reader names follow the state ("Camera off", "Stop recording", "Show me", "Background: blur").
- [x] Studio: today's full window.
- [x] Switch any time: menu → View → Simple / Studio. Saved as `UiMode` in `Settings.cs`.

### Still to verify on a running app (before tagging v1)
- [ ] Narrator walk-through of Simple and Studio: names, help text and each announcement above.
- [ ] Windows Text size 200% and display scale 200%: no clipping or overlap in the top bar, panels and dialogs.
- [ ] High contrast (Aquatic, Desert, Night sky): every control visible, focus rectangle visible.
- [ ] Accessibility Insights FastPass on both windows.

### Contrast measurements (WCAG 1.4.3, 1.4.11)
Computed with the WCAG relative-luminance formula against the Windows 11 base and card backgrounds
(light #F3F3F3 / #F9F9F9, dark #202020 / #2B2B2B). Theme tokens are Windows' own values.

| Colour | Use | Light | Dark | Result |
|---|---|---|---|---|
| Primary text | all labels | 17+:1 | 15+:1 | Pass |
| Secondary text (`TextFillColorSecondary`) | hints, values | 5.6:1 | 9.4:1 (8.1:1 on panels) | Pass |
| Accent text | Support, selected look | 9.0:1 | 12.2:1 | Pass |
| Critical (`SystemFillColorCritical`) | warnings, shutter label, failed checks | 5.1:1 | 8.0:1 | Pass |
| Success / caution | meeting check, status dots | 4.9:1 / 4.7:1 | 8.0:1 / 12.3:1 | Pass |
| Strong stroke | "off" status dots, control borders | 3.1:1 | 5.8:1 | Pass (3:1) |
| White on record red #D32F26 | Record button while recording, REC pill | 5.0:1 | 5.0:1 | Pass |
| Captions, white on 85% black | over any video (worst case: white video) | 15.1:1 | 15.1:1 | Pass |
| Prompter, white on #0C0C10 | script | 19.5:1 | 19.5:1 | Pass |
| **Was:** green #5BFF8F, gold #FFD36B, red #FF8A80 | meeting check icons, warnings | 1.2 / 1.4 / 2.2:1 | 10.9 / – / 6.2:1 | **Failed in light: fixed** (now success / caution / critical tokens) |
| **Was:** 40% white status dots | source and meeting camera dots | 1.0:1 | 3.7:1 | **Failed in light: fixed** (strong stroke / success / critical; the meeting camera's state is also in its name) |
| Focus rectangle (WinUI default: 2 px outer + 1 px inner in opposite colours) | every control | 3:1 or better on any background | same | Pass (2.4.7, 2.4.11; also 2.4.13 AAA) |

The record-red button against the dark panel is 2.8:1. 1.4.11 is still met because the button is identified
by its white text and icon (5.0:1), not by its outline.

### WCAG 2.2 level A and AA success criteria
"Code" = shown by the source and this audit. "Run" = still to confirm on the running app (list above).
Section 508 adopts WCAG 2.0 AA and EN 301 549 clause 11 maps to WCAG 2.1 AA, so this table covers both.
EN 301 549 11.7 (user preferences) is met by following the Windows theme, contrast theme, text size and animation settings.

| SC | Level | How KISSIK meets it | Evidence |
|---|---|---|---|
| 1.1.1 Non-text content | A | Every icon button and tile has an automation name; decorative dots hidden | Code; Run |
| 1.2.x Time-based media | A/AA | KISSIK makes media rather than publishing it; live captions are offered for meetings and recordings | N/A |
| 1.3.1 Info and relationships | A | Native WinUI controls, slider headers, named groups | Code |
| 1.3.2 Meaningful sequence | A | Tab order follows reading order | Code; Run |
| 1.3.3 Sensory characteristics | A | Instructions name controls by label, not shape or position | Code |
| 1.3.4 Orientation | AA | Window works at any size and aspect | Code |
| 1.3.5 Identify input purpose | AA | No personal-data fields | N/A |
| 1.4.1 Use of colour | A | States are also in words and names (Record/Stop, Visible/Blurred/Away, dot states) | Code |
| 1.4.2 Audio control | A | KISSIK plays no sound by itself | N/A |
| 1.4.3 Contrast (minimum) | AA | Table above | Measured |
| 1.4.4 Resize text | AA | Follows Windows text size; min heights, not fixed; Large controls | Code; Run at 200% |
| 1.4.5 Images of text | AA | None used | Code |
| 1.4.10 Reflow | AA | Simple mode works in a small window; Studio needs about 1000 px wide | Partial; Run |
| 1.4.11 Non-text contrast | AA | Table above | Measured |
| 1.4.12 Text spacing | AA | System text rendering; labels wrap | Code; Run |
| 1.4.13 Content on hover or focus | AA | Standard WinUI tooltips (Esc dismisses, hoverable, persistent) | Code |
| 2.1.1 Keyboard | A | All actions reachable; layers with Ctrl+arrow and Delete; shortcuts above | Code; Run |
| 2.1.2 No keyboard trap | A | Flyouts and dialogs close with Esc | Code; Run |
| 2.1.4 Character key shortcuts | A | Window shortcuts all use Ctrl; the prompter's single keys work only while it has focus | Code |
| 2.2.1 Timing adjustable | A | Pressing Record again cancels the countdown; no other time limits | Code |
| 2.2.2 Pause, stop, hide | A | Previews slow down under reduce motion; prompter scroll has play/pause | Code |
| 2.3.1 Three flashes | A | Nothing flashes more than once a second | Code |
| 2.4.1 Bypass blocks | A | Panels can be hidden; Simple mode | Code |
| 2.4.2 Page titled | A | Window titles: KISSIK, KISSIK prompter, Recordings | Code |
| 2.4.3 Focus order | A | Reading order | Code; Run |
| 2.4.4 Link purpose | A | Button and link text says what it does | Code |
| 2.4.5 Multiple ways | AA | Single-window app | N/A |
| 2.4.6 Headings and labels | AA | Section labels and slider headers describe each control | Code |
| 2.4.7 Focus visible | AA | System focus rectangle is never turned off | Code |
| 2.4.11 Focus not obscured (minimum) | AA | Panels sit beside the video; flyouts open on top of what they belong to | Code; Run |
| 2.5.1 Pointer gestures | A | No multi-point or path gestures needed | Code |
| 2.5.2 Pointer cancellation | A | WinUI buttons act on release | Code |
| 2.5.3 Label in name | A | Each name starts with the visible label ("Meeting camera, ready") | Code |
| 2.5.4 Motion actuation | A | None | N/A |
| 2.5.7 Dragging movements | AA | Layer drag has keyboard and Layers-tab alternatives; sliders take clicks and keys | Code |
| 2.5.8 Target size (minimum) | AA | 24 px required; KISSIK uses 40 px, 48 px with Large controls | Code; Run |
| 3.1.1 Language of page | A | App language from the package manifest | Code |
| 3.2.1 On focus / 3.2.2 On input | A | Focus never triggers actions; a change applies only what was chosen | Code |
| 3.2.3 / 3.2.4 Consistent navigation and identification | AA | One top bar; same names in Simple and Studio | Code |
| 3.2.6 Consistent help | A | Help is always in the same menu place | Code |
| 3.3.1 Error identification | A | Errors are described in words (status and Narrator) | Code |
| 3.3.2 Labels or instructions | A | Every input is labelled | Code |
| 3.3.3 Error suggestion | AA | Messages say what to do next ("Check the stream key and your internet connection") | Code |
| 3.3.4 Error prevention | AA | No legal or financial data; deleting a layer is undoable; the meeting camera can be reinstalled from the same menu | Code |
| 3.3.7 Redundant entry | A | Stream destinations and presets are remembered | Code |
| 3.3.8 Accessible authentication (minimum) | AA | No sign-in | N/A |
| 4.1.2 Name, role, value | A | Native controls; names follow state | Code; Run |
| 4.1.3 Status messages | AA | Every status is a UIA notification and a live region | Code; Run |

Open before claiming conformance: the "Run" checks, and 1.4.10 for Studio at small window widths.
