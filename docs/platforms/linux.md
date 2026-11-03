# KISSIK for Linux — Ubuntu and Fedora

Status: **plan, no code yet.** Read [README.md](README.md) first for shared contracts and parity.

Supported distributions: **Ubuntu 22.04 LTS and 24.04 LTS**, **Fedora 40 and newer** (Workstation, GNOME and KDE).
Both X11 and Wayland sessions are supported, with differences noted.

## 1. Architecture choice

| Option | Pros | Cons | Verdict |
| --- | --- | --- | --- |
| **Avalonia 11 (.NET 10)** | Reuses the portable half of `OpenKamera.Core` as-is (scenes, filters, LUTs, framing, screen-light maths, segmentation via ONNX Runtime); same language as Windows; X11 + Wayland | Not a native-feel GTK/Qt app; Wayland backend newer | **Recommended** |
| GTK4 + libadwaita (Rust or C) | Very native on GNOME | Rewrite of all Core logic | No |
| Qt 6 / QML (C++) | Native on KDE, good multimedia | Rewrite of all Core logic | No |

The UI lives in `src/OpenKamera.Linux` (Avalonia), drawing frames with Avalonia's Skia/OpenGL surface.
Media plumbing uses GStreamer through the `gstreamer-sharp` bindings or a small C helper (`libokmedia.so`) called by
P/Invoke — the helper is often simpler and faster. Pick the C helper.

## 2. Repository layout

```
src/
  OpenKamera.Core/              # portable part after the split (README §1.2), net10.0
  OpenKamera.Linux/             # Avalonia app
    App.axaml  MainWindow.axaml  Views/  ViewModels/
    Platform/
      PortalCamera.cs           # xdg-desktop-portal Camera + PipeWire
      V4l2Camera.cs             # direct V4L2 path (+ controls)
      V4l2Loopback.cs           # virtual camera writer
      PipeWireAudio.cs          # mixer I/O + virtual mic
      GstRecorder.cs            # recording
      ScreenLightX11.cs / ScreenLightLayerShell.cs
      DdcBrightness.cs          # ddcutil
  native/libokmedia/            # C helper: V4L2, PipeWire, GStreamer glue (meson)
packaging/
  flatpak/org.openkamera.OpenKamera.yml
  deb/   rpm/openkamera.spec
  dkms/  (docs only — we do not fork v4l2loopback)
```

## 3. Packages per distro

### 3.1 Build dependencies

| Need | Ubuntu 22.04 / 24.04 (`apt`) | Fedora 40+ (`dnf`) |
| --- | --- | --- |
| .NET 10 SDK | `dotnet-sdk-10.0` (24.04 archive or Microsoft feed on 22.04) | `dotnet-sdk-10.0` |
| C toolchain | `build-essential meson ninja-build pkg-config` | `gcc meson ninja-build pkgconf-pkg-config` |
| GStreamer dev | `libgstreamer1.0-dev libgstreamer-plugins-base1.0-dev` | `gstreamer1-devel gstreamer1-plugins-base-devel` |
| PipeWire dev | `libpipewire-0.3-dev libspa-0.2-dev` | `pipewire-devel` |
| V4L2 utils | `libv4l-dev v4l-utils` | `libv4l-devel v4l-utils` |
| libcamera (optional) | `libcamera-dev` | `libcamera-devel` |
| Portal / D-Bus | `libportal-dev` (optional; we call D-Bus directly via Tmds.DBus) | `libportal-devel` |
| Layer shell | `libgtk-layer-shell-dev` (only for the helper window) | `gtk-layer-shell-devel` |
| Fonts / Skia | `libfontconfig1-dev libice6 libsm6` | `fontconfig-devel libICE libSM` |
| RNNoise | build from source (vendored) | build from source (vendored) |
| USB phone link | `libimobiledevice-dev libusbmuxd-dev usbmuxd` | `libimobiledevice-devel libusbmuxd-devel usbmuxd` |

### 3.2 Runtime packages

| Need | Ubuntu | Fedora |
| --- | --- | --- |
| GStreamer plugins | `gstreamer1.0-plugins-good gstreamer1.0-plugins-bad gstreamer1.0-plugins-ugly gstreamer1.0-libav gstreamer1.0-pipewire gstreamer1.0-vaapi` | `gstreamer1-plugins-good gstreamer1-plugins-bad-free gstreamer1-plugin-libav gstreamer1-plugins-ugly-free pipewire-gstreamer gstreamer1-vaapi` — H.264 encode on Fedora: `gstreamer1-plugin-openh264` (Cisco repo, enabled by default) or RPM Fusion `gstreamer1-plugins-ugly` (x264) / `ffmpeg` |
| Virtual camera | `v4l2loopback-dkms v4l2loopback-utils` | RPM Fusion free: `akmod-v4l2loopback v4l2loopback` |
| Monitor brightness | `ddcutil` (+ user in `i2c` group) | `ddcutil` |
| VA-API driver | `intel-media-va-driver-non-free` / `mesa-va-drivers` | `intel-media-driver` / `mesa-va-drivers-freeworld` (RPM Fusion) |
| GPU inference | see §5 | see §5 |

## 4. Capture

Three paths, chosen in this order:

1. **PipeWire camera portal** (`org.freedesktop.portal.Camera`, `AccessCamera` → `OpenPipeWireRemote`).
   Required inside Flatpak; works with V4L2 *and* libcamera cameras (laptops with MIPI/IPU6 sensors only work here).
   Fedora 40+ and Ubuntu 24.04 ship PipeWire camera support; Ubuntu 22.04's PipeWire (0.3.48) is too old —
   use path 2 there.
2. **Direct V4L2** (`/dev/videoN`, `VIDIOC_*` ioctls, MJPEG/YUYV/NV12, mmap buffers). Native packages only.
   Gives full **controls** (`VIDIOC_QUERYCTRL`: focus_absolute, exposure_time_absolute, white_balance_temperature,
   zoom_absolute, pan/tilt, backlight_compensation, power_line_frequency) — this maps 1:1 to `CameraController.cs`.
   Hot-plug via udev (`SUBSYSTEM=="video4linux"`), skip our own loopback device.
3. **libcamera** directly (later), only if the portal route is missing on a target.

MJPEG decode: `jpegdec` / VA-API `vajpegdec` in the GStreamer helper; output BGRA into the Core `Frame` struct.

## 5. Effects and segmentation

- `PersonSegmenter.cs` with the **same models** (`Models/selfie_segmentation_fp16.onnx`, `modnet_fp16.onnx`) on
  ONNX Runtime 1.20+:
  | Hardware | NuGet package | Execution provider | Extra system packages |
  | --- | --- | --- | --- |
  | Any (default) | `Microsoft.ML.OnnxRuntime` | CPU | none |
  | NVIDIA | `Microsoft.ML.OnnxRuntime.Gpu.Linux` | CUDA 12 + cuDNN 9 | Ubuntu: `nvidia-driver-550`, CUDA from NVIDIA repo; Fedora: RPM Fusion `akmod-nvidia xorg-x11-drv-nvidia-cuda` |
  | Intel iGPU/NPU | `Intel.ML.OnnxRuntime.OpenVino` | OpenVINO | `intel-opencl-icd` (Ubuntu) / `intel-compute-runtime` (Fedora) |
  | AMD | custom ROCm build of ONNX Runtime | ROCm / MIGraphX | ROCm 6 repo (Ubuntu 22.04/24.04 officially; Fedora ships `rocm-*` packages from 40) |
  Fast model on CPU runs 720p30 on a 4-core laptop; MODNet needs a GPU for 1080p30. Run inference at mask resolution
  (256×256 / 512×512) and upscale the mask with the guided filter planned on Windows.
- Blur/replace/remove, spotlight, retouch: `BackgroundEffects.cs` as-is first (CPU, SIMD via `System.Numerics.Vector`),
  then an OpenGL ES 3 shader path inside Avalonia's GL context for 1080p60.
- Looks + LUTs: `ImageFilters.cs` + `Lut3D.cs` reused; GPU path uploads the LUT to a 3D texture.
  LUT folder `$XDG_DATA_HOME/openkamera/LUTs`.
- Framing: `FrameTransform.cs`, `OutputFit.cs` reused. Auto-framing: an ONNX face detector (e.g. an open
  BlazeFace-style or YuNet model, Apache/MIT licensed) — same model planned for Windows.

## 6. Virtual camera

### 6.1 v4l2loopback (works everywhere today)
```bash
# Ubuntu
sudo apt install v4l2loopback-dkms v4l2loopback-utils
# Fedora (enable RPM Fusion free first)
sudo dnf install https://mirrors.rpmfusion.org/free/fedora/rpmfusion-free-release-$(rpm -E %fedora).noarch.rpm
sudo dnf install akmod-v4l2loopback v4l2loopback && sudo akmods --force
```
Load with a fixed device number and name, and `exclusive_caps=1` (needed for browsers to see it):
```bash
echo 'options v4l2loopback devices=1 video_nr=42 card_label="KISSIK" exclusive_caps=1 max_buffers=2' | sudo tee /etc/modprobe.d/openkamera.conf
echo v4l2loopback | sudo tee /etc/modules-load.d/openkamera.conf
sudo modprobe v4l2loopback
```
The app writes frames to `/dev/video42` (`VIDIOC_S_FMT` YUYV or NV12 at the profile size, then `write()` or
mmap output). When idle, write the **slate** at 5 fps instead of stopping (some apps freeze on a silent device).
An in-app "Set up virtual camera" button runs the commands above with `pkexec` after explaining them.

**Secure Boot:** an unsigned module will not load.
- Ubuntu: DKMS creates a Machine Owner Key on install and asks for a one-time password; on the next boot choose
  "Enroll MOK" in the blue MokManager screen. Check with `mokutil --sb-state` and `dmesg | grep -i v4l2loopback`.
- Fedora: `sudo dnf install kmodtool akmods mokutil openssl && sudo kmodgenca -a && sudo mokutil --import /etc/pki/akmods/certs/public_key.der`,
  reboot, enroll, then `sudo akmods --force`.
Document both in the app with a clear "Module blocked by Secure Boot" detector (`modprobe` exit + `dmesg`).

### 6.2 PipeWire virtual camera node (later, no kernel module)
Create a PipeWire `Video/Source` node from the app (`pw_stream` with `PW_DIRECTION_OUTPUT`, `media.class=Video/Source`).
Apps that use the camera portal / PipeWire (newer Chromium-based browsers with the PipeWire camera flag, Firefox
work in progress, the streaming-studio app's PipeWire source) see it; classic V4L2-only apps do not. Ship both:
loopback for compatibility, PipeWire for Flatpak users who cannot load modules.

Replace `VirtualCameraChannel.cs` with a Linux `IFrameSink`; keep its header semantics (sequence, timestamp, writer pid,
client count) for the in-app "Who has my camera?" view (read `/proc/*/fd` links to `/dev/video42` for client pids).

## 7. Audio and virtual microphone

- Mixer I/O through PipeWire (`pw_stream` capture per input; monitor of the default sink = system audio;
  per-app capture via the app's own node). Port the gain/meter/limiter/EQ maths from `AudioEngine.cs` into the portable
  `AudioMath.cs` (README §1.2) and reuse it. RNNoise for noise suppression.
- Virtual mic, PipeWire native (Fedora 40+, Ubuntu 22.10+; Ubuntu 22.04 uses PulseAudio by default — same commands
  via `pactl` work on both because PipeWire implements the Pulse protocol):
```bash
pactl load-module module-null-sink sink_name=openkamera_mix sink_properties=device.description="OpenKamera_Mix"
pactl load-module module-remap-source master=openkamera_mix.monitor source_name=openkamera_mic source_properties=device.description="OpenKamera_Microphone"
```
  The app plays its mixed output into `openkamera_mix`; meeting apps pick "KISSIK Microphone".
  For PipeWire-only persistence, ship `~/.config/pipewire/pipewire.conf.d/openkamera.conf` with a `libpipewire-module-loopback`
  (`capture.props = { media.class = Audio/Sink }`, `playback.props = { media.class = Audio/Source }`). The app creates
  these at runtime and removes them on exit (unload module ids) unless "keep virtual mic" is on.

## 8. Recording

GStreamer pipeline from the helper (appsrc → encoder → mp4mux, fragmented):
```
appsrc name=v format=time is-live=true caps=video/x-raw,format=BGRA,… ! videoconvert !
  <encoder> ! h264parse ! mp4mux fragment-duration=2000 name=m ! filesink location=…
appsrc name=a … ! audioconvert ! avenc_aac ! m.
```
Encoder pick order: `nvh264enc` (NVIDIA) → `vah264enc` / `vaapih264enc` (Intel/AMD) → `x264enc tune=zerolatency`
(Ubuntu `gstreamer1.0-plugins-ugly`, Fedora RPM Fusion) → `openh264enc` (Fedora default). AAC: `avenc_aac`
(gst-libav) or `fdkaacenc`. Default folder `$XDG_VIDEOS_DIR/OpenKamera`. FFmpeg (`libavcodec`) is the fallback
for distros without the GStreamer plugins.

## 9. Screen light

`ScreenLight.cs` maths reused; windows differ by session:
- **X11:** override-redirect, always-on-top, input-shape set to empty (`XShapeCombineRectangles` with `ShapeInput`)
  so clicks pass through; one per monitor (XRandR).
- **Wayland (KDE Plasma, Sway, Hyprland, other wlroots):** `wlr-layer-shell` surface on the `overlay` layer with an
  empty input region, via a tiny GTK helper using `gtk-layer-shell`.
- **Wayland (GNOME):** no layer-shell. Fallback: a fullscreen, undecorated window on the chosen monitor with an empty
  input region (works but appears in the window list) or the "inside KISSIK's window" mode (Windows default too).
- Exclusion from screen capture is **not possible** on Linux; tell the user.
- Monitor brightness: `ddcutil setvcp 10 <0-100>` for external monitors (needs `i2c-dev` module and the `i2c` group);
  laptop panels via `/sys/class/backlight/*/brightness` through `systemd-logind` `SetBrightness` (no root needed).

## 10. Feature mapping summary

| Feature | Linux implementation | Core reuse |
| --- | --- | --- |
| Camera list / hot-plug | Portal + PipeWire registry, or V4L2 + udev | — |
| Hardware controls | V4L2 controls | `CameraController` interface only |
| Profiles | Portal/V4L2 format enumeration | `FormatChooser.cs`, `OutputFit.cs` |
| Background blur/replace/remove | ONNX Runtime + effects | `PersonSegmenter.cs` (package swap), `BackgroundEffects.cs`, `BackgroundPipeline.cs` |
| Looks / LUTs | CPU then GL | `ImageFilters.cs`, `Lut3D.cs` |
| Framing | — | `FrameTransform.cs` |
| Auto framing (Auto / Smart) | ONNX face detector on a 320 px copy every 3rd frame | `AutoFramer` class (split out of `AutoFramer.cs`; `FaceTracker` is Windows-only) |
| Steady picture | CPU first, GL later | `Stabilizer.cs` |
| Chroma key | CPU first, GL shader later | `ChromaKey.cs` |
| Look presets | `$XDG_CONFIG_HOME/openkamera/looks` | `LookPresets.cs` |
| Who has my camera? | Scan `/proc/*/fd` symlinks for `/dev/video*`, map pid → `/proc/<pid>/comm`; PipeWire: `pw-dump` node `application.name` | replaces `DeviceUsage.cs` |
| Scenes | Avalonia drawing of layers | `Scenes.cs`, `SceneCompositor.cs` (`$XDG_DATA_HOME/openkamera/Scenes`) |
| Screen light | X11 / layer-shell / fallback | `ScreenLight.cs` |
| Audio mixer | PipeWire | `AudioMath.cs` (after split) |
| Recording | GStreamer | — |
| Meeting camera (virtual cam) | v4l2loopback, PipeWire node | header semantics of `VirtualCameraChannel.cs` |
| Phone as camera | Receiver: usbmuxd/libimobiledevice for iPhone USB, Avahi (`_openkamera._tcp`) for Wi-Fi, decode via GStreamer | `OKPhoneLink` spec (C# port) |

## 11. Packaging

1. **Flatpak (first)** — `org.openkamera.OpenKamera`, runtime `org.freedesktop.Platform//24.08`,
   SDK extension `org.freedesktop.Sdk.Extension.dotnet10`. Permissions: `--device=all` is **not** needed when using
   the camera portal; use `--socket=pipewire`, `--socket=wayland`, `--socket=fallback-x11`, `--share=ipc`,
   `--talk-name=org.freedesktop.portal.Desktop`. The v4l2loopback module cannot be installed from a Flatpak: the
   app detects it and shows distro commands; if present, it needs `--device=all` to write to `/dev/video42`, so ship
   that permission and explain why on Flathub. Publish to Flathub after a test remote.
2. **.deb** (Ubuntu): self-contained .NET publish (`dotnet publish -r linux-x64 --self-contained -p:PublishReadyToRun=true`),
   `Depends: gstreamer1.0-plugins-good, gstreamer1.0-plugins-bad, gstreamer1.0-libav, libpipewire-0.3-0t64 | libpipewire-0.3-0`,
   `Recommends: v4l2loopback-dkms, ddcutil, usbmuxd`. Host in a signed APT repo or a Launchpad PPA.
3. **.rpm** (Fedora): `openkamera.spec`, `Requires: gstreamer1-plugins-good pipewire-gstreamer`, `Recommends: akmod-v4l2loopback ddcutil usbmuxd`
   (the akmod is in RPM Fusion; say so). Build in COPR.
4. arm64 builds for both (`linux-arm64`) once CI is green.

## 12. Step-by-step: set up and build

### Ubuntu 24.04
```bash
sudo apt update
sudo apt install -y dotnet-sdk-10.0 build-essential meson ninja-build pkg-config \
  libgstreamer1.0-dev libgstreamer-plugins-base1.0-dev gstreamer1.0-plugins-good gstreamer1.0-plugins-bad \
  gstreamer1.0-plugins-ugly gstreamer1.0-libav gstreamer1.0-pipewire libpipewire-0.3-dev libv4l-dev v4l-utils \
  v4l2loopback-dkms v4l2loopback-utils ddcutil
git clone https://github.com/<owner>/OpenKamera.git && cd KISSIK
meson setup build/native src/native/libokmedia && ninja -C build/native
dotnet test tests/OpenKamera.Core.Tests        # portable tests
dotnet run --project src/OpenKamera.Linux
```
### Ubuntu 22.04
Same as above, but: install .NET 10 from the Microsoft package feed (`packages-microsoft-prod.deb`), and the app uses
direct V4L2 capture (PipeWire camera support is too old). Keep the default PulseAudio; `pactl` virtual mic works.

### Fedora 40+
```bash
sudo dnf install -y dotnet-sdk-10.0 gcc meson ninja-build pkgconf-pkg-config \
  gstreamer1-devel gstreamer1-plugins-base-devel gstreamer1-plugins-good gstreamer1-plugins-bad-free \
  gstreamer1-plugin-libav gstreamer1-plugin-openh264 pipewire-gstreamer pipewire-devel libv4l-devel v4l-utils ddcutil
# optional, RPM Fusion: x264 / VA-API freeworld / v4l2loopback
sudo dnf install -y https://mirrors.rpmfusion.org/free/fedora/rpmfusion-free-release-$(rpm -E %fedora).noarch.rpm
sudo dnf install -y akmod-v4l2loopback v4l2loopback gstreamer1-plugins-ugly mesa-va-drivers-freeworld
git clone https://github.com/<owner>/OpenKamera.git && cd KISSIK
meson setup build/native src/native/libokmedia && ninja -C build/native
dotnet run --project src/OpenKamera.Linux
```
### Flatpak
```bash
flatpak install flathub org.freedesktop.Sdk//24.08 org.freedesktop.Sdk.Extension.dotnet10//24.08
flatpak-builder --user --install --force-clean build-flatpak packaging/flatpak/org.openkamera.OpenKamera.yml
flatpak run org.openkamera.OpenKamera
```

## 13. Numbered build plan

| # | Milestone | Done when |
| --- | --- | --- |
| 1 | Core split (README §1.2) + Linux CI (`ubuntu-24.04`, Fedora container) | Portable tests green on both |
| 2 | Avalonia shell + V4L2 capture + preview | 1080p30 preview on Ubuntu 22.04/24.04 and Fedora |
| 3 | Portal capture path | Works in Flatpak and with a libcamera-only laptop camera |
| 4 | Effects CPU path (segmentation, blur/replace, looks, LUTs, framing) | 720p30 on CPU |
| 5 | v4l2loopback output + slate + setup helper (incl. Secure Boot guidance) | Visible in browser meetings and the streaming-studio app |
| 6 | GPU paths (GL shaders, CUDA/OpenVINO EPs) | 1080p30 with MODNet on NVIDIA and Intel |
| 7 | Recording via GStreamer | 10-min 1080p30 MP4, hardware encode where available |
| 8 | Scenes + editor | `.okscene` from Windows renders identically |
| 9 | Screen light (X11, layer-shell, GNOME fallback) + ddcutil | All three sessions behave as documented |
| 10 | Audio mixer + virtual mic | Mic selectable in browser meetings |
| 11 | Phone receiver | iPhone over USB and Wi-Fi |
| 12 | Flatpak on Flathub, .deb PPA, .rpm COPR | Clean-VM install tests pass on all four distros |

## 14. Testing

- Automated: portable Core tests in CI; libokmedia tests with `v4l2loopback` loaded in a VM runner (feed a test pattern,
  read it back with `v4l2-ctl --stream-mmap`).
- Manual matrix: Ubuntu 22.04 (X11 + Wayland), 24.04 (GNOME Wayland), Fedora (GNOME Wayland, KDE Plasma Wayland).
- Meeting apps: Zoom, Microsoft Teams and Google Meet **in Chromium-based browsers and Firefox** (their Linux
  desktop clients where available), plus OBS Studio (V4L2 source and PipeWire source). Check device name, resolution,
  that `exclusive_caps=1` lets browsers list the device, the slate when paused, two consumers at once.
- Check `v4l2-ctl --list-devices`, `pw-cli ls Node`, `pactl list short sources` in support logs ("Copy diagnostics" button).

## 15. Risks

| Risk | Mitigation |
| --- | --- |
| Secure Boot blocks v4l2loopback | Guided MOK enrolment; PipeWire virtual node as module-free fallback |
| Kernel updates break the module | DKMS/akmods rebuild automatically; detect "module missing" at start |
| Browsers do not list loopback without `exclusive_caps=1` or when no producer is writing | Always set it; always write slate frames |
| GNOME Wayland has no layer-shell | Fallback modes; document honestly |
| Codec licensing (H.264/AAC) differs by distro | Use system GStreamer plugins, never bundle encoders in .deb/.rpm; Flatpak uses the freedesktop codecs extension |
| ROCm support is narrow | CPU and OpenVINO paths always work |
| Avalonia Wayland quirks | Allow `--x11` fallback (XWayland) |
