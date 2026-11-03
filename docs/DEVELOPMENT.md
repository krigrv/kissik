# Developing KISSIK

## Layout

| Path | What it is |
|---|---|
| `src/OpenKamera.App` | WinUI 3 app (packaged MSIX): window, panels, scene editor, gallery |
| `src/OpenKamera.Core` | Everything testable: capture, camera controls, image pipeline, segmentation, scenes, recording |
| `src/OpenKamera.Core/Models` | Bundled ONNX segmentation models (licences in `THIRD_PARTY.md`) |
| `src/OpenKamera.MediaSource` | Placeholder for the future virtual camera (C++ media source) |
| `tests/OpenKamera.Core.Tests` | xUnit tests for Core |

## Frame pipeline

Each camera frame is processed once on the frame-reader thread
(`FrameProcessor.ProcessPixels`):

1. **Framing** — mirror, rotate, zoom/pan (`FrameTransform`)
2. **Output fit** — scale/crop to the selected profile size (`OutputFit`)
3. **Background** — person segmentation and blur/replace/remove/spotlight/retouch
   (`BackgroundPipeline` → `PersonSegmenter` → `BackgroundEffects`)
4. **Look** — adjustments, filters, LUTs (`ImageFilters`, `Lut3D`)
5. **Scene** — background, placed camera, then overlay layers (`SceneCompositor`)

The preview shows scene layers as live XAML (so they can be edited); the same
layers are captured with `RenderTargetBitmap` and blended into recordings and
snapshots, so what you see is what you record.

## Running the app

```powershell
dotnet run -c Release --project src\OpenKamera.App\OpenKamera.App.csproj
```

- Packaged apps need an identity: use `dotnet run`, not the built `.exe`.
- Windows camera privacy must allow the app (Settings → Privacy & security → Camera).
- Only one app can usually use a webcam at a time — close other camera apps
  before pressing Connect.
- Settings are stored under the package's `LocalCache\Roaming\OpenKamera`
  (Windows virtualises AppData for packaged apps).

## Tests

```powershell
dotnet test OpenKamera.slnx -c Release
```

- `Pipeline_720p_Throughput` measures speed (≥ 15 fps at 720p for the Fast
  segmentation path); run it on an idle machine.
- `RealPortrait_*` runs only when `OPENKAMERA_SAMPLE` points at a local portrait
  PNG; set `OPENKAMERA_SAMPLE_OUT` to a folder to also write rendered effects.
  Never commit such images.

## Diagnostics

The settings menu (gear in the top capsule) has **Save a picture of this
window**, which renders the app (not the desktop) to `Pictures\OpenKamera`.
