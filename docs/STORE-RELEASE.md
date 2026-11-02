# Releasing KISSIK on the Microsoft Store

The Store signs and hosts the app, updates it automatically, and removes the "unknown publisher" warning.
Individual developer accounts are free to open (company accounts have a one-time fee); free apps pay nothing.

## One-time setup (needs the owner's account)
1. Sign up at https://partner.microsoft.com/dashboard (Windows & Xbox → Apps and games), individual account.
2. **Reserve the name** "KISSIK" (Apps and games → New product → MSIX or PWA app).
3. Open the app → Product management → **Product identity** and copy:
   - Package/Identity/Name
   - Package/Identity/Publisher (starts with `CN=`)
   - Package/Properties/PublisherDisplayName

## Build the upload
```powershell
./tools/make-store-package.ps1 -IdentityName "<Name>" -Publisher "CN=<…>" -PublisherDisplayName "<Display name>" -Version 1.0.0.0
```
Writes `artifacts/store/OpenKamera.App_<version>_x64.msixupload` (about 66 MB). The manifest in the repo is
left unchanged. Bump `-Version` for every later submission (1.0.1.0, 1.1.0.0, …; the last number must stay 0).

## Submission answers
| Field | Value |
|---|---|
| Category | Photo & video |
| Pricing | Free |
| Privacy policy URL | the hosted copy of `docs/PRIVACY.md` (website `/privacy`) |
| Age rating | IARC questionnaire: no violence, no user-to-user chat, no purchases → 3+ |
| Restricted capability `runFullTrust` | "KISSIK registers a Windows virtual camera (Media Foundation) so meeting apps can use the processed video, and serves the phone-camera pairing page on the local network. Both need a full-trust desktop process." |
| `systemAIModels` | "Uses the on-device Windows AI models for face detection; no data leaves the device." |
| Webcam / microphone | Core features: the camera is processed and passed to meeting apps; the mic feeds recordings, streams and on-device captions. |
| Notes for certification | "To test: connect any webcam, press Connect. Open Teams/Zoom/Camera and pick 'KISSIK' as the camera. Phone camera: menu → Phone, scan the QR code on the same Wi-Fi." |

Store listing: short description, 5+ screenshots at 1920×1080 (use "Save a picture of this window"),
the logo from `docs/logo/kissik-logo-1024.png`, and the feature list from `website/index.html`.

## Before each submission
- `dotnet test tests/OpenKamera.Core.Tests -c Release` is green
- Walk `docs/RELEASE-CHECKLIST.md` and the hand-test list in `docs/V1-QUALITY-AUDIT.md`
- Publish the matching GitHub Release (tag `v<version>`) so the in-app update check agrees with the Store
