namespace OpenKamera.Core;

/// <summary>The page the phone opens from the QR code: camera preview, lens switch, quality, and the frame sender.</summary>
public static class PhonePage
{
    public const string Html = """
<!doctype html>
<html lang="en"><head>
<meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1,viewport-fit=cover">
<meta name="theme-color" content="#0b0b0f"><title>KISSIK camera</title>
<style>
  :root{color-scheme:dark;--gold:#ffd36b;--text:#f4f5f7;--dim:#9aa0aa;--well:#1b1c22}
  *{box-sizing:border-box} html,body{margin:0;height:100%;background:#0b0b0f;color:var(--text);font:16px/1.4 system-ui,-apple-system,"Segoe UI",sans-serif}
  main{display:flex;flex-direction:column;height:100%;padding:max(16px,env(safe-area-inset-top)) 16px max(16px,env(safe-area-inset-bottom))}
  h1{font:600 20px/1.2 Georgia,serif;margin:0 0 4px} h1 i{color:var(--gold)}
  #state{color:var(--dim);font-size:14px;min-height:20px}
  .stage{flex:1;display:grid;place-items:center;margin:12px 0;border-radius:18px;overflow:hidden;background:#000}
  video{width:100%;height:100%;object-fit:contain}
  .row{display:flex;gap:8px;flex-wrap:wrap}
  button,select{flex:1;min-height:48px;border-radius:14px;border:1px solid #2a2c33;background:var(--well);color:var(--text);font:inherit}
  button.primary{background:var(--gold);color:#111;border:0;font-weight:600}
  button:focus-visible,select:focus-visible{outline:2px solid var(--gold);outline-offset:2px}
  .live{color:#5bff8f}.bad{color:#ff8a80}
</style></head>
<body><main>
  <h1>KISSIK camera</h1>
  <div id="state" role="status" aria-live="polite">Tap Start to use this phone as your camera.</div>
  <div class="stage"><video id="v" playsinline muted autoplay aria-label="Camera preview"></video></div>
  <div class="row">
    <button id="go" class="primary">Start camera</button>
    <button id="flip" aria-label="Switch between front and back camera">Flip</button>
    <button id="mic" aria-pressed="false" aria-label="Use this phone's microphone">Mic off</button>
    <select id="q" aria-label="Quality">
      <option value="480">480p · smoothest</option><option value="720" selected>720p · balanced</option><option value="1080">1080p · sharpest</option>
    </select>
  </div>
</main>
<script>
const qs = new URLSearchParams(location.search), token = qs.get('t');
const v = document.getElementById('v'), state = document.getElementById('state'), go = document.getElementById('go');
let retries = 0;
let facing = 'environment', quality = 720, stream = null, ws = null, running = false, sending = false, wake = null;
const canvas = document.createElement('canvas'), ctx = canvas.getContext('2d');
const say = (t, cls) => { state.textContent = t; state.className = cls || ''; };

async function openCamera() {
  if (stream) stream.getTracks().forEach(t => t.stop());
  const h = quality, w = Math.round(h * 16 / 9);
  stream = await navigator.mediaDevices.getUserMedia({ audio: false,
    video: { facingMode: facing, width: { ideal: w }, height: { ideal: h }, frameRate: { ideal: 30 } } });
  v.srcObject = stream; await v.play();
}

function connect() {
  ws = new WebSocket(`${location.protocol === 'https:' ? 'wss' : 'ws'}://${location.host}/ws?t=${encodeURIComponent(token)}`);
  ws.binaryType = 'arraybuffer';
  ws.onopen = () => { retries = 0; say('Live in KISSIK', 'live'); sendStatus(); };
  ws.onclose = () => {
    if (!running) return;
    // Retry quickly at first (a blip), then every few seconds (KISSIK restarting) — the link stays the same.
    retries++; const wait = Math.min(250 * retries, 3000);
    say(retries > 3 ? 'Waiting for KISSIK on your PC…' : 'Reconnecting…', 'bad'); setTimeout(connect, wait);
  };
  ws.onmessage = async e => {
    if (e.data === 'front' || e.data === 'back') { facing = e.data === 'front' ? 'user' : 'environment'; await openCamera(); sendStatus(); }
    else if (typeof e.data === 'string' && e.data.startsWith('q:')) { quality = +e.data.slice(2) || 720; document.getElementById('q').value = String(quality); await openCamera(); sendStatus(); }
    else if (typeof e.data === 'string' && e.data.startsWith('torch:')) await setTrack({ torch: e.data === 'torch:on' });
    else if (typeof e.data === 'string' && e.data.startsWith('zoom:')) {
      const caps = stream?.getVideoTracks()[0]?.getCapabilities?.() || {};
      if (caps.zoom) await setTrack({ zoom: Math.min(caps.zoom.max, Math.max(caps.zoom.min, +e.data.slice(5) || 1)) });
    }
  };
}

// Torch and zoom are camera-track settings; phones that don't support them simply ignore the request.
async function setTrack(c) {
  const t = stream?.getVideoTracks()[0];
  if (!t) return;
  try { await t.applyConstraints({ advanced: [c] }); } catch {}
  sendStatus();
}

async function sendStatus() {
  if (!ws || ws.readyState !== 1) return;
  let battery = null, charging = null;
  try { const b = await navigator.getBattery(); battery = b.level; charging = b.charging; } catch {}
  const caps = stream?.getVideoTracks()[0]?.getCapabilities?.() || {}, set = stream?.getVideoTracks()[0]?.getSettings?.() || {};
  ws.send(JSON.stringify({ device: navigator.userAgent.match(/\(([^)]+)\)/)?.[1] || 'phone',
    w: v.videoWidth, h: v.videoHeight, battery, charging, facing: facing === 'user' ? 'front' : 'back',
    torch: caps.torch ? !!set.torch : null, zoomMax: caps.zoom ? caps.zoom.max : null, zoom: set.zoom ?? null,
    maxW: caps.width?.max ?? null, maxH: caps.height?.max ?? null, maxFps: caps.frameRate?.max ?? null }));
}

function pump() {
  if (!running) return;
  // Send the next frame only when the last one has gone, so a slow network lowers fps instead of adding delay.
  if (!sending && ws && ws.readyState === 1 && ws.bufferedAmount < 256 * 1024 && v.videoWidth) {
    sending = true;
    canvas.width = v.videoWidth; canvas.height = v.videoHeight;
    ctx.drawImage(v, 0, 0);
    canvas.toBlob(b => { if (b && ws.readyState === 1) b.arrayBuffer().then(a => { ws.send(a); sending = false; }); else sending = false; }, 'image/jpeg', 0.8);
  }
  (v.requestVideoFrameCallback ? v.requestVideoFrameCallback(pump) : requestAnimationFrame(pump));
}

go.onclick = async () => {
  if (running) { running = false; ws && ws.close(); stream && stream.getTracks().forEach(t => t.stop()); go.textContent = 'Start camera'; say('Stopped.'); wake && wake.release(); return; }
  try {
    await openCamera(); running = true; go.textContent = 'Stop'; connect(); pump();
    try { wake = await navigator.wakeLock.request('screen'); } catch {}
    setInterval(sendStatus, 15000);
  } catch (e) { say('Camera blocked: allow camera access for this page, then tap Start again.', 'bad'); }
};
let micStream = null, actx = null, micNode = null;
const micBtn = document.getElementById('mic');
micBtn.onclick = async () => {
  if (micStream) {
    micStream.getTracks().forEach(t => t.stop()); micNode && micNode.disconnect(); actx && actx.close();
    micStream = null; micBtn.textContent = 'Mic off'; micBtn.setAttribute('aria-pressed', 'false'); return;
  }
  try {
    micStream = await navigator.mediaDevices.getUserMedia({ audio: { echoCancellation: true, noiseSuppression: true, autoGainControl: true }, video: false });
    actx = new AudioContext();
    const src = actx.createMediaStreamSource(micStream);
    micNode = actx.createScriptProcessor(2048, 1, 1);
    micNode.onaudioprocess = e => {
      if (!ws || ws.readyState !== 1) return;
      const pcm = e.inputBuffer.getChannelData(0);
      const buf = new ArrayBuffer(5 + pcm.length * 4), dv = new DataView(buf);
      dv.setUint8(0, 65); dv.setUint32(1, actx.sampleRate, true);
      for (let i = 0; i < pcm.length; i++) dv.setFloat32(5 + i * 4, pcm[i], true); // header makes it unaligned
      ws.send(buf);
    };
    src.connect(micNode); micNode.connect(actx.destination);
    micBtn.textContent = 'Mic on'; micBtn.setAttribute('aria-pressed', 'true');
  } catch { say('Microphone blocked: allow it for this page to use the phone as your mic.', 'bad'); micStream = null; }
};
document.getElementById('flip').onclick = async () => { facing = facing === 'user' ? 'environment' : 'user'; if (running) { await openCamera(); sendStatus(); } };
document.getElementById('q').onchange = async e => { quality = +e.target.value; if (running) { await openCamera(); sendStatus(); } };
document.addEventListener('visibilitychange', () => { if (document.visibilityState === 'visible' && running && navigator.wakeLock) navigator.wakeLock.request('screen').then(w => wake = w).catch(() => {}); });
</script></body></html>
""";
}
