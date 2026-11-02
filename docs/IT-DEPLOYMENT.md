# KISSIK for IT teams

KISSIK runs as a normal user app. Only two optional parts ever ask for admin rights, and only when the person
chooses them.

| Part | Needs admin? | What it does | How to remove |
|---|---|---|---|
| KISSIK app (MSIX) | No | Camera studio, recording, captions (offline), phone camera | Settings → Apps → KISSIK → Uninstall |
| Meeting camera | Yes, once (UAC) | Windows 11 virtual camera (`C:\ProgramData\OpenKamera\VirtualCamera`), registered with `regsvr32` | KISSIK → Meeting camera → Remove the camera |
| Phone firewall rule | Yes, once (UAC) | One inbound TCP rule, port 47820, local subnet only, named "OpenKemera phone camera" | `netsh advfirewall firewall delete rule name="OpenKemera phone camera"` |

## Privacy and network

- No account, no telemetry, no ads. Nothing is uploaded.
- Captions use an on-device model (downloaded once from alphacephei.com, ~40 MB). Audio never leaves the PC.
- Phone camera: HTTPS/WebSocket on the local network only, protected by a random pairing token.
- Remote control (MCP/REST) is off by default and listens on 127.0.0.1 only, with a bearer token.
- Livestreaming (optional) needs FFmpeg and sends only to destinations the user enters; keys are DPAPI-encrypted.

## Rolling out

- Install the MSIX with Intune, Configuration Manager or `Add-AppxPackage`. No admin rights are needed for the app.
- Pre-install the meeting camera once per machine with an elevated script: copy
  `OpenKamera.VirtualCamera.dll` to `C:\ProgramData\OpenKamera\VirtualCamera\`, grant `*S-1-5-19` read access, then
  `regsvr32 /s` it. The app detects it and doesn't ask users for admin.
- Camera and microphone privacy (Settings → Privacy & security) apply as for any app; set them with your usual policy.

## Files

| Path | Contents |
|---|---|
| `%LOCALAPPDATA%\Packages\6FC3C948-…\LocalCache\Roaming\OpenKamera\` | settings, scenes, LUTs, phone pairing, speech model, control token |
| `%USERPROFILE%\Videos\OpenKamera\` | recordings (crash-safe pieces in `*.parts` until joined) |
| `%USERPROFILE%\Pictures\OpenKamera\` | snapshots |
