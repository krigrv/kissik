import { Video } from "@remotion/media";
import React from "react";
import { LiveTiles, STUDIO_TILES } from "./LiveThumbs";
import { AbsoluteFill, Composition, Easing, Img, interpolate, spring, staticFile, useCurrentFrame, useVideoConfig } from "remotion";

// Phone pairing, 8 s loop: the PC shows a code, the phone scans it, the phone's picture appears on the PC.
const FPS = 30;
const LEN = 8 * FPS;
const ORANGE = "#ff8a3d";
const GREEN = "#3ddc84";
const clamp = { extrapolateLeft: "clamp", extrapolateRight: "clamp" } as const;
const ease = Easing.bezier(0.16, 1, 0.3, 1);
const inOut = (f: number, a: number, b: number, c: number, d: number) =>
  interpolate(f, [a, b, c, d], [0, 1, 1, 0], { ...clamp, easing: ease });

// PC window: the screenshot is 1600x791, shown 900 wide. Its preview area, scaled.
const PC = { x: 50, y: 150, w: 900, h: 445 };
const K = PC.w / 1600;
const PREVIEW = { x: 357 * K, y: 79 * K, w: 970 * K, h: 545 * K };

// An illustrative code pattern (not a real pairing code).
const QR: React.FC<{ size: number }> = ({ size }) => {
  const n = 21;
  const cells: React.ReactNode[] = [];
  const finder = (x: number, y: number) => x < 7 && y < 7 || x >= n - 7 && y < 7 || x < 7 && y >= n - 7;
  for (let y = 0; y < n; y++)
    for (let x = 0; x < n; x++) {
      let on: boolean;
      if (finder(x, y)) {
        const fx = x >= n - 7 ? x - (n - 7) : x, fy = y >= n - 7 ? y - (n - 7) : y;
        on = fx === 0 || fy === 0 || fx === 6 || fy === 6 || (fx >= 2 && fx <= 4 && fy >= 2 && fy <= 4);
      } else on = ((x * 7 + y * 13 + x * y) % 5) < 2;
      if (on) cells.push(<rect key={`${x}-${y}`} x={x} y={y} width={1} height={1} />);
    }
  return <svg width={size} height={size} viewBox={`-1 -1 ${n + 2} ${n + 2}`} style={{ background: "#fff", borderRadius: 10 }}><g fill="#111">{cells}</g></svg>;
};

const Chip: React.FC<{ children: React.ReactNode; dot?: string }> = ({ children, dot = GREEN }) => (
  <div style={{ display: "inline-flex", alignItems: "center", gap: 10, padding: "9px 16px", borderRadius: 999, background: "rgba(20,20,24,.85)", border: "1px solid rgba(255,255,255,.14)", color: "#fff", font: "600 22px Segoe UI, sans-serif", whiteSpace: "nowrap" }}>
    <span style={{ width: 10, height: 10, borderRadius: 5, background: dot, boxShadow: `0 0 12px ${dot}` }} />{children}
  </div>
);

// src is the phone camera feed; phoneFocus picks the part of it the upright phone screen shows.
export const Pairing: React.FC<{ src?: string; still?: string; phoneFocus?: number; overlays?: boolean }> = ({ src = "social.mp4", still = "live-person.jpg", phoneFocus, overlays = false }) => {
  const f = useCurrentFrame();
  const { fps } = useVideoConfig();
  const qr = inOut(f, 10, 25, 95, 108);           // code shows on the PC
  const phoneIn = spring({ frame: f - 25, fps, config: { damping: 16 } });
  const scan = interpolate(f, [45, 90], [0, 1], clamp); // scan line on the phone
  const paired = inOut(f, 92, 102, 210, 225);
  const live = inOut(f, 104, 124, 215, 232);       // phone picture on the PC and phone
  const out = interpolate(f, [228, 240], [0, 1], clamp);
  const studio = interpolate(f, [100, 116], [0, 1], clamp) * (1 - out);
  return (
    <AbsoluteFill style={{ background: "radial-gradient(800px 500px at 70% 10%, rgba(61,220,132,.18), transparent 70%), radial-gradient(700px 500px at 10% 100%, rgba(255,138,61,.22), transparent 70%), #0b0b0e", fontFamily: "Segoe UI, Inter, sans-serif" }}>
      {/* PC */}
      <div style={{ position: "absolute", left: PC.x, top: PC.y, width: PC.w, height: PC.h, borderRadius: 16, overflow: "hidden", border: "1px solid rgba(255,255,255,.14)", boxShadow: "0 40px 100px rgba(0,0,0,.6)" }}>
        <Img src={staticFile("first-screen.jpg")} style={{ position: "absolute", width: PC.w, height: PC.h }} />
        <Img src={staticFile("studio.jpg")} style={{ position: "absolute", width: PC.w, height: PC.h, opacity: studio }} />
        <div style={{ position: "absolute", left: PREVIEW.x, top: PREVIEW.y, width: PREVIEW.w, height: PREVIEW.h, overflow: "hidden", opacity: live, background: "#000" }}>
          <Video src={staticFile(src)} loop muted style={{ position: "absolute", width: PREVIEW.w, height: (PREVIEW.w * 9) / 16, top: (PREVIEW.h - (PREVIEW.w * 9) / 16) / 2, maxWidth: "none" }} />
        </div>
        <div style={{ position: "absolute", left: 1356 * K, top: 109 * K, width: 215 * K, height: 120 * K, overflow: "hidden", borderRadius: 6, opacity: live }}>
          <Video src={staticFile(src)} loop muted style={{ position: "absolute", width: 215 * K, height: (215 * K * 9) / 16, maxWidth: "none" }} />
        </div>
        <div style={{ position: "absolute", inset: 0, opacity: live }}><LiveTiles src={src} tiles={STUDIO_TILES} k={K} overlays={overlays} /></div>
        {/* code card */}
        <div style={{ position: "absolute", left: PREVIEW.x + PREVIEW.w / 2 - 150, top: PREVIEW.y + PREVIEW.h / 2 - 150, width: 300, padding: 20, borderRadius: 18, background: "#1c1c1f", border: "1px solid rgba(255,255,255,.12)", textAlign: "center", opacity: qr, scale: String(0.9 + 0.1 * qr), boxShadow: "0 30px 80px rgba(0,0,0,.6)" }}>
          <QR size={200} />
          <div style={{ color: "#fff", font: "600 20px Segoe UI", marginTop: 12 }}>Scan with KISSIK Camera</div>
        </div>
      </div>
      <div style={{ position: "absolute", left: PC.x, top: PC.y - 64 }}><Chip dot={ORANGE}>KISSIK on your PC</Chip></div>

      {/* Phone */}
      <div style={{ position: "absolute", left: 985, top: 110, translate: `${(1 - phoneIn) * 400}px 0px`, rotate: `${(1 - phoneIn) * 10}deg` }}>
        <div style={{ width: 230, height: 500, borderRadius: 36, background: "#111", padding: 9, border: "2px solid rgba(255,255,255,.22)", boxShadow: "0 40px 100px rgba(0,0,0,.7)" }}>
          <div style={{ position: "relative", width: "100%", height: "100%", borderRadius: 28, overflow: "hidden", background: "#050505" }}>
            {/* scanning view */}
            <div style={{ position: "absolute", inset: 0, opacity: 1 - live }}>
              <div style={{ position: "absolute", left: 40, top: 150, width: 132, height: 132, border: `3px solid ${GREEN}`, borderRadius: 14 }} />
              <div style={{ position: "absolute", left: 44, width: 124, height: 3, top: 152 + scan * 126, background: GREEN, boxShadow: `0 0 16px ${GREEN}`, opacity: scan > 0 && scan < 1 ? 1 : 0 }} />
              <div style={{ position: "absolute", left: 0, right: 0, top: 310, textAlign: "center", color: "#fff", font: "600 17px Segoe UI" }}>Point at the code<br />on your PC</div>
            </div>
            {phoneFocus !== undefined
              ? <Video src={staticFile(src)} loop muted style={{ position: "absolute", height: 482, width: (482 * 16) / 9, maxWidth: "none", left: 106 - ((482 * 16) / 9) * phoneFocus, opacity: live }} />
              : <Img src={staticFile(still)} style={{ position: "absolute", width: "100%", height: "100%", objectFit: "cover", opacity: live }} />}
          </div>
        </div>
        <div style={{ marginTop: 16, width: 250, display: "flex", justifyContent: "center", opacity: phoneIn }}><Chip dot={paired > 0.5 ? GREEN : ORANGE}>{paired > 0.5 ? "Paired · Live over Wi-Fi" : "KISSIK Camera on Android"}</Chip></div>
      </div>

      {/* link line */}
      <svg width={1280} height={720} style={{ position: "absolute", inset: 0, opacity: live }}>
        <path d="M 990 360 C 960 360, 950 380, 930 380" stroke={GREEN} strokeWidth={4} fill="none" strokeDasharray="10 10" strokeDashoffset={-f * 2} />
      </svg>
    </AbsoluteFill>
  );
};

export const PairingComposition = () => (
  <Composition id="Pairing" component={Pairing} durationInFrames={LEN} fps={FPS} width={1280} height={720} />
);
