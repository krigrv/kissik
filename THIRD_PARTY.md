# Third-party components

## Models (bundled in `src/OpenKamera.Core/Models/`)

| File | Model | Licence | Source | SHA-256 |
|---|---|---|---|---|
| `selfie_segmentation_fp16.onnx` (251 KB) | MediaPipe Selfie Segmentation (© Google LLC), ONNX conversion | Apache-2.0 (as stated by the converter; Google's model card governs the original weights — confirm before commercial redistribution) | https://huggingface.co/onnx-community/mediapipe_selfie_segmentation | `3B2A53A26B6E7C00B5E2415443E45DFA8C66C62BD496F7F2EE7DCF9CE47DEA4F` |
| `modnet_fp16.onnx` (13 MB) | MODNet portrait matting (Ke et al.), ONNX conversion | Apache-2.0 (original repo https://github.com/ZHKKKe/MODNet) | https://huggingface.co/Xenova/modnet | `25F165DA9BFD30830A575F1F0490F1ACD995975CB349BC02F3D79332E1FE5CF6` |

## Runtime packages

| Package | Licence |
|---|---|
| Microsoft.WindowsAppSDK 2.6.4 | MIT |
| Microsoft.Windows.AI.MachineLearning 2.4.89 (ONNX Runtime + DirectML) | MIT |
| QRCoder 1.6.0 (UPI donation QR) | MIT |
| Vortice.Direct3D11 and Vortice.D3DCompiler 3.8.3 (GPU frame path) | MIT |

## Assets

Backgrounds, scene templates, filter looks and icons are generated in code or use
the Segoe Fluent Icons font shipped with Windows. No images or scenes from other
camera apps are included.
