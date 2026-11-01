# Contributing to OpenKamera

Thanks for helping! This guide covers how to contribute and the rules every
contribution must follow.

## How to contribute

1. **Open an issue first** for anything bigger than a small fix, so we can agree
   on the approach before you spend time on it.
2. **Fork** the repository and create a branch from `main`
   (for example `fix/focus-slider` or `feature/scene-transitions`).
3. **Make your change**, keeping to the style of the code around it.
4. **Test it**: `dotnet build OpenKamera.slnx -c Release` must have no errors
   or warnings, and `dotnet test OpenKamera.slnx -c Release` must pass. Add a
   test for bug fixes and new logic in `OpenKamera.Core`. For UI changes, say in
   the pull request how you checked it (camera model, Windows version).
5. **Open a pull request** describing what changed and why. A maintainer will
   review it; we may ask for changes before merging.

Small, focused pull requests are reviewed fastest.

## Clean-room rules (required)

OpenKamera is an independent project. To keep it free of legal problems, every
contribution must be **your own original work**, or code under a licence
compatible with GPL-3.0 that you credit in [THIRD_PARTY.md](THIRD_PARTY.md).

You must **not** contribute:

- **Code, binaries or decompiled output** from any proprietary application —
  including other webcam, camera-control or streaming apps. No disassembly,
  decompilation or copying of their code, even "just for reference".
- **Their assets**: logos, icons, images, scene files, backgrounds, filter/LUT
  files, sounds, fonts or UI text.
- **Their names or branding** in the product: do not name OpenKamera features,
  presets, scenes or files after another company's products, and do not use
  other product names in the app's name, icon, store listing or UI.
- **Material you don't have the right to share**, such as photos of other
  people without their permission.

You **may** describe features in general terms (for example "background blur"
or "screen light"), build features that other apps also have, and mention other
products factually in documentation when explaining compatibility.

If you are unsure whether something is allowed, ask in an issue before
submitting it.

## Licensing of contributions

By submitting a pull request, you confirm that you wrote the contribution (or
otherwise have the right to submit it) and agree that it is licensed under the
project's [GPL-3.0 licence](LICENSE). To make this explicit, please sign off
your commits:

```
git commit -s -m "Fix focus slider jumping at the end of its range"
```

The `-s` flag adds a `Signed-off-by:` line certifying the
[Developer Certificate of Origin](https://developercertificate.org/).

## Models and third-party code

New AI models or libraries must have a licence that allows redistribution in a
GPL-3.0 app (for example Apache-2.0, MIT, BSD). Add the model or package, its
source URL, licence and SHA-256 to [THIRD_PARTY.md](THIRD_PARTY.md) in the same
pull request.

## Privacy

Never commit personal data: real photos or recordings of people, settings
files, API keys or tokens. Tests that need a real image read it from a local
path given in an environment variable (see `OPENKAMERA_SAMPLE` in the tests).

## Code of Conduct

Everyone taking part follows the [Code of Conduct](CODE_OF_CONDUCT.md).
