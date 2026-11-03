# kissik-voice

A small C API over the DeepFilterNet runtime (MIT/Apache-2.0) used by KISSIK's "Studio voice"
noise removal. Every call catches Rust panics, so a model error passes audio through instead of
closing the app. Processing is synchronous: one 480-sample (10 ms at 48 kHz) mono frame per call.

| Function | |
|---|---|
| `kv_create(model_path, atten_lim_db)` | Loads a DeepFilterNet `.tar.gz` model; null on failure. |
| `kv_frame_length(st)` | Samples per frame (480). |
| `kv_process(st, in, out)` | 0 on success; non-zero = passed through, stop using this state. |
| `kv_destroy(st)` | Frees the state. |

## Build

Needs Rust (stable, MSVC toolchain) and the Visual Studio C++ build tools.

```powershell
cargo build --release                                   # x64
cargo build --release --target i686-pc-windows-msvc     # x86
```

Copy `target\release\kissik_voice.dll` to `src\OpenKamera.Core\Native\x64\` and
`target\i686-pc-windows-msvc\release\kissik_voice.dll` to `src\OpenKamera.Core\Native\x86\`.
The model, `DeepFilterNet3_ll_onnx.tar.gz`, comes from the DeepFilterNet repository's `models` folder
(tag v0.5.6) and lives in `src\OpenKamera.Core\Models\`.

ARM64 needs the "MSVC ARM64 build tools" and "C++ Clang tools" Visual Studio components. tract-linalg's ARM64
assembly is GNU syntax, so it is built with clang (the vendored copy in `vendor/tract-linalg` stops its build script
from sending it to the x86-only Microsoft assembler):

```bash
L="C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/VC/Tools/Llvm/x64/bin"
export CC_aarch64_pc_windows_msvc="$L/clang.exe" AR_aarch64_pc_windows_msvc="$L/llvm-lib.exe"
export CFLAGS_aarch64_pc_windows_msvc="--target=aarch64-pc-windows-msvc -c"
cargo build --release --target aarch64-pc-windows-msvc   # copy to Native\arm64\
```

## RNNoise

`rnnoise.dll` is RNNoise 0.2 (BSD-3-Clause) built from the release tarball with MSVC:

```
cl /O2 /fp:fast /LD /DWIN32 /D__SSE2__ /D__SSE__ /DDLL_EXPORT /DRNNOISE_BUILD /D_USE_MATH_DEFINES /Iinclude /Isrc ^
   src\denoise.c src\rnn.c src\pitch.c src\kiss_fft.c src\celt_lpc.c src\nnet.c src\nnet_default.c ^
   src\parse_lpcnet_weights.c src\rnnoise_data.c src\rnnoise_tables.c /Fe:rnnoise.dll
```

(x86 adds `/arch:SSE2`.) ARM64 uses `clang-cl --target=aarch64-pc-windows-msvc /D__ARM_NEON` from a
`vcvarsall x64_arm64` prompt, plus an include folder with this `os_support.h`, which the 0.2 tarball leaves out:

```c
#include <string.h>
#define OPUS_INLINE __inline
#define OPUS_COPY(dst, src, n) (memcpy((dst), (src), (n)*sizeof(*(dst))))
#define OPUS_MOVE(dst, src, n) (memmove((dst), (src), (n)*sizeof(*(dst))))
#define OPUS_CLEAR(dst, n) (memset((dst), 0, (n)*sizeof(*(dst))))
```
