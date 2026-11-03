# Release compatibility checklist

Run before every release. Record results in the release notes ("works in" table) so users can see them.

## Meeting camera
- [ ] Zoom (desktop) lists "KISSIK", shows the picture, and the slate when KISSIK is closed
- [ ] Microsoft Teams (new), Google Meet in Chrome and Edge, Firefox, Discord, Slack huddles
- [ ] OBS Studio (Video Capture Device), Streamlabs
- [ ] Two apps use the camera at once
- [ ] After "Remove the camera": no app lists KISSIK; the built-in webcam still works (test on a laptop)

## Audio
- [ ] "Send to meetings" with VB-CABLE: meeting hears the processed voice
- [ ] Mic unplugged mid-recording: watchdog restarts, recording keeps going and saves
- [ ] Hardware mute key still works; KISSIK never changes the default mic

## Phone
- [ ] Android Chrome and iPhone Safari over Wi-Fi: pair, stream, flip lens, phone mic
- [ ] Restart KISSIK: the phone reconnects with no rescan
- [ ] Android over USB: page opens on the phone, no certificate warning

## Stability
- [ ] 2-hour soak (auto-framing + blur + recording): memory flat, no crash
- [ ] Unplug and replug the webcam: KISSIK reconnects
- [ ] Windows Insider / latest preview build: app starts, camera listed

## Outputs
- [ ] Recording with a scene change, pause/resume and the vertical copy: both files play, no gaps
- [ ] Go live to a test RTMP server and two destinations at once
- [ ] Captions on/off repeatedly while speaking: no freeze
