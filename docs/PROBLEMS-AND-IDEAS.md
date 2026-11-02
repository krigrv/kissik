# What goes wrong with camera apps, and what KISSIK does about it

A working list of the real problems people hit with webcam studios, virtual
cameras and phone-as-webcam apps, and the KISSIK feature that answers each
one. Items marked **★** are ideas we have not seen in other camera apps.
Build order is in [ROADMAP.md](../ROADMAP.md) §0c.

Sources for the common problems: troubleshooting guides and user forums
(see the links at the end), plus first-hand testing while building KISSIK.

## 1. The virtual camera

| Problem people hit | What KISSIK does |
|---|---|
| The virtual camera doesn't appear in Zoom/Teams/Meet until the meeting app is restarted (launch order) | Uses the Windows 11 system virtual camera, registered once, so it is listed in every app at all times, even before KISSIK starts |
| Black screen in the meeting when the studio app isn't running or the scene is empty | ★ **Friendly slate** instead of black: "KISSIK isn't open — your camera will appear here", with the logo; the slate is also used while switching cameras |
| "Camera is being used by another app" with no clue which app | ★ **Who has my camera?** Shows which app is holding the camera right now (Windows records it) with a one-click way to switch to it or close it |
| Only one app can use the webcam at a time | The virtual camera can be opened by several apps at once (Teams and OBS together) |
| Text and logos look mirrored to the other people | ★ **Smart mirror**: your preview is mirrored like a mirror, but what others see is never mirrored (unless you choose to) |
| Meeting apps silently squash the picture to 720p or crop it | ★ Per-app profiles: KISSIK notices which app opened the camera and switches to the profile you chose for it |
| Lag and stutter when effects are on | ★ **Smooth-first mode**: when the PC is busy, effects step down (High → Fast segmentation, lower blur cost) before frames are dropped; an on-screen latency meter shows end-to-end delay |
| Effects make the camera slow to start | Effects warm up in the background; the camera shows instantly and effects fade in |

## 2. Before and during a call

| Problem | What KISSIK does |
|---|---|
| Joining a call with bad light, wrong mic or the camera pointing at the ceiling | ★ **Meeting check**: one button checks camera, framing, face brightness, mic level, noise and background, and says what to fix in plain words |
| Too dark, face looks flat | Screen light / ring light with max brightness, auto-tune from the camera (done) |
| Looking at notes instead of the camera | ★ **Eye-line prompter**: a small, scrolling script placed right under the webcam position so your eyes stay near the lens |
| Someone walks behind you | ★ **Privacy guard**: when a second person appears in the background, the background is blurred automatically until they leave |
| Forgetting the camera is on | ★ **Privacy shutter** hotkey and an "away" slate (BRB card with your name) that also mutes the mic |
| The camera drifts or you move around | Auto-framing that keeps your face centred and the same size (planned) |
| Echo, fans, keyboards | Noise suppression with a per-mic fallback, limiter, EQ (done) |
| Captions missing in some meeting apps | ★ **Live captions burnt into the video** (on-device speech recognition), so every app shows them |

## 3. Phone as a webcam

| Problem | What KISSIK does |
|---|---|
| Pairing is fiddly; apps need accounts | ★ **Scan a QR code** on the PC with the phone: the phone opens a page and starts streaming. No account, no app install for the first version |
| Wi-Fi lag and drop-outs | Prefers USB when plugged in, switches to Wi-Fi without dropping the picture, and reconnects on its own |
| Phone overheats or the battery drains in long calls | ★ Phone **temperature and battery** shown in KISSIK; bitrate and frame rate step down before the phone throttles |
| Can't use the good rear camera because you can't see the screen | Rear camera by default; framing and preview are on the PC |
| Phone locks or a notification interrupts | The phone page keeps the screen awake and shows a dimmed "streaming" screen |
| Phone mic is better than the laptop's | Phone microphone as an audio source in the mixer |

## 4. Recording and streaming

| Problem | What KISSIK does |
|---|---|
| Recordings don't match what was live | Records exactly the composed picture and the mix (done) |
| Vertical clips need a second tool | Vertical profiles (done); dual landscape + vertical recording (planned) |
| Lost recordings when the app crashes | ★ Recordings are written in fragments so a crash keeps everything up to that second |

## Sources

- [Virtual camera not showing up in Zoom, Teams or OBS](https://cici-cam.com/blog/virtual-camera-not-showing-up/)
- [Video call lag: diagnose and fix](https://docam.io/knowledge/video-call-lag-fix)
- [How to fix webcam lag](https://www.ceedo.com/how-to-fix-webcam-lag/)
- [Camera not working in Teams or Zoom](https://testmypc.co.uk/blog/camera-not-working-teams-zoom-fix)
- [OBS virtual camera not detected on Windows](https://obsproject.com/forum/threads/windows-10-couldnt-detect-obs-virtual-camera.193805/)
- [OBS virtual camera black screen](https://obsproject.com/forum/threads/the-yy-virtual-camera-is-good-but-it-doesnt-display-any-image-in-obs-just-a-black-screen.194267/)

## What users of a popular phone-webcam app say (2026-11-02, ~40 Google Play reviews)
- **Expectations, in order:** connects every time; setup in under 2 minutes; honest pricing (no paywalled resolution, no watermark, no ads).
- **Top complaints:** disconnects and slow reconnects; USB that fails for unclear reasons; "couldn't connect" on the same Wi-Fi with no reason given; paywalls and default watermarks; the app hijacking the system mic and breaking the laptop mute key; keeps running after quit; phone drains and heats even while charging; lag with effects; too many first-run pop-ups; no help right before a meeting.
- **Requests:** stream with the phone screen off; torch button on the PC; external mic plugged into the phone; front/back switch always visible.
- **Loved:** QR pairing in minutes, phone quality beats webcams, low PC load, no ads, control from the PC, fast human support.
- Our answers are tracked in ROADMAP.md §0d.

