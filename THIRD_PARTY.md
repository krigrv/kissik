# Third-party components

## Models (bundled in `src/OpenKamera.Core/Models/`)

| File | Model | Licence | Source | SHA-256 |
|---|---|---|---|---|
| `selfie_segmentation_fp16.onnx` (251 KB) | MediaPipe Selfie Segmentation (© Google LLC), ONNX conversion | Apache-2.0 (as stated by the converter; Google's model card governs the original weights — confirm before commercial redistribution) | https://huggingface.co/onnx-community/mediapipe_selfie_segmentation | `3B2A53A26B6E7C00B5E2415443E45DFA8C66C62BD496F7F2EE7DCF9CE47DEA4F` |
| `modnet_fp16.onnx` (13 MB) | MODNet portrait matting (Ke et al.), ONNX conversion | Apache-2.0 (original repo https://github.com/ZHKKKe/MODNet) | https://huggingface.co/Xenova/modnet | `25F165DA9BFD30830A575F1F0490F1ACD995975CB349BC02F3D79332E1FE5CF6` |

## Voice noise removal (`src/OpenKamera.Core/Native/{x64,x86,arm64}` and `Models/`)

| File | Component | Licence | Source | SHA-256 |
|---|---|---|---|---|
| `rnnoise.dll` per architecture (1.7 MB each) | RNNoise 0.2 with its built-in model ("Light"), built with MSVC | BSD-3-Clause (`Native/RNNOISE-LICENSE.txt`) | https://github.com/xiph/rnnoise/releases/tag/v0.2 | x64 `32B38B748C6584F687090DAEA1DF053D6C7226075B3C5E1FA9A3C001989EAFD6`, x86 `887D73393969C914BDC6C799CCD03EF979221C4240749FC956A7F219067FCD09`, arm64 `CACDE34B6DD0F7C679B8F3BEF0841124A3EEAFBED96C75A59CD997B3595D81AF` |
| `kissik_voice.dll` per architecture (13–18 MB) | DeepFilterNet 0.5.6 runtime (with tract) behind KISSIK's panic-safe C API, source in `native/kissik-voice` | MIT or Apache-2.0 (`Native/DEEPFILTERNET-LICENSE-*.txt`); wrapper MIT | https://github.com/Rikorose/DeepFilterNet/tree/v0.5.6 | x64 `ED71295BE1A45656EFCEA9C5439306E732C22335160764E172D8919A981E14B5`, x86 `945E517BC8DF50082EB59133BFE50FEE6BA86B79C5454C7B2D0F2763F1AB485E`, arm64 `9C4578717DA809BA4A781D4A9CE652116B50382137C429E439F32C8B84956ABC` |
| `Models/DeepFilterNet3_ll_onnx.tar.gz` (36 MB) | DeepFilterNet 3 low-latency model ("Studio voice") | MIT or Apache-2.0 | https://github.com/Rikorose/DeepFilterNet/tree/v0.5.6/models | `5998E58E8BA0E09BB76986EF97B84AFA065A571EF282D4A1222F341E3251CF3A` |

Build steps for both libraries: `native/kissik-voice/README.md`. The Rust dependency tree of
`kissik_voice.dll` is MIT/Apache-2.0/BSD-style only (no GPL). For ARM64 it vendors `tract-linalg` 0.19.16
(MIT/Apache-2.0) with a one-line build fix, in `native/kissik-voice/vendor/tract-linalg`.

## Runtime packages

| Package | Licence |
|---|---|
| Microsoft.WindowsAppSDK 2.6.4 | MIT |
| Microsoft.Windows.AI.MachineLearning 2.4.89 (ONNX Runtime + DirectML) | MIT |
| QRCoder 1.6.0 (UPI donation QR) | MIT |

## Assets

Backgrounds, scene templates, filter looks and icons are generated in code or use
the Segoe Fluent Icons font shipped with Windows. No images or scenes from other
camera apps are included.
