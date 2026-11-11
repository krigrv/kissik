import React from "react";
import { AbsoluteFill, Audio, Composition, Easing, Img, interpolate, Sequence, spring, staticFile, useCurrentFrame, useVideoConfig } from "remotion";
import { MyComponent } from "./Composition";
import { Framing, NameCard, Scenes, Simple } from "./Features";
import { Pairing } from "./Pairing";

// 19 s social cut. Every cut lands on a beat of a 120 BPM track (15 frames at 30 fps).
const FPS = 30;
const BEAT = 15;
const ORANGE = "#ff8a3d";
const FONT = "Segoe UI, Inter, sans-serif";
const clamp = { extrapolateLeft: "clamp", extrapolateRight: "clamp" } as const;
const ease = Easing.bezier(0.16, 1, 0.3, 1);

type Shot = { C: React.FC; w: number; h: number; offset: number; beats: number; text: string; accent?: string; fx?: number };
const HOOK = 4 * BEAT;
const SHOTS: Shot[] = [
  { C: Pairing, w: 1280, h: 720, offset: 40, beats: 3, text: "Your phone", accent: "", fx: 0.72 },
  { C: Pairing, w: 1280, h: 720, offset: 112, beats: 3, text: "is the", accent: "webcam.", fx: 0.62 },
  { C: NameCard, w: 1280, h: 720, offset: 22, beats: 3, text: "Name", accent: "cards.", fx: 0.38 },
  { C: Scenes, w: 1280, h: 720, offset: 45, beats: 4, text: "Live", accent: "scenes." },
  { C: Framing, w: 1280, h: 720, offset: 12, beats: 3, text: "Follows", accent: "you.", fx: 0.4 },
  { C: MyComponent, w: 1920, h: 1080, offset: 178, beats: 5, text: "Works in", accent: "every app." },
  { C: Simple, w: 1280, h: 720, offset: 28, beats: 3, text: "One tap.", accent: "Record." },
];
const END = 10 * BEAT;
// "Launch Day" (Suno), 120 BPM, first beat at 0.24 s, drop at ~8.2 s: start so the drop lands on the hook's snap.
const MUSIC_START = Math.round(7.24 * FPS);
export const FAST_LEN = HOOK + SHOTS.reduce((s, x) => s + x.beats * BEAT, 0) + END;

type Layout = { W: number; H: number; vertical: boolean };
const VTOP = 560;
const VH = 1116;

const Pop: React.FC<{ text: string; accent?: string; size: number; y: number }> = ({ text, accent, size, y }) => {
  const f = useCurrentFrame();
  const { fps } = useVideoConfig();
  const p = spring({ frame: f, fps, config: { damping: 12, stiffness: 180 } });
  return (
    <div style={{ position: "absolute", left: 40, right: 40, top: y, textAlign: "center", font: `900 ${size}px ${FONT}`, letterSpacing: "-.03em", lineHeight: 1, color: "#fff", scale: String(1.35 - 0.35 * p), opacity: Math.min(1, p * 2), textShadow: "0 8px 40px rgba(0,0,0,.7)" }}>
      {text} {accent && <span style={{ color: ORANGE }}>{accent}</span>}
    </div>
  );
};

const Flash: React.FC = () => {
  const f = useCurrentFrame();
  return <AbsoluteFill style={{ background: "#fff", opacity: interpolate(f, [0, 5], [0.35, 0], clamp), pointerEvents: "none" }} />;
};

// Plays a clip from `offset`, scaled to fill the frame with a quick punch-in.
const Clip: React.FC<{ s: Shot; L: Layout }> = ({ s, L }) => {
  const f = useCurrentFrame();
  const punch = interpolate(f, [0, 10], [1.08, 1], { ...clamp, easing: ease });
  const drift = interpolate(f, [0, s.beats * BEAT], [1, 1.04]);
  const fit = L.vertical ? VH / s.h : Math.max(L.W / s.w, L.H / s.h);
  const top = L.vertical ? VTOP : (L.H - s.h * fit) / 2;
  const left = L.vertical ? Math.min(0, Math.max(L.W - s.w * fit, L.W / 2 - s.w * fit * (s.fx ?? 0.5))) : (L.W - s.w * fit) / 2;
  return (
    <AbsoluteFill style={{ overflow: "hidden" }}>
      <div style={{ position: "absolute", left, top, width: s.w, height: s.h, scale: String(fit * punch * drift), transformOrigin: "0 0" }}>
        <Sequence from={-s.offset} layout="none"><s.C /></Sequence>
      </div>
    </AbsoluteFill>
  );
};

const Hook: React.FC<{ L: Layout }> = ({ L }) => {
  const f = useCurrentFrame();
  const after = f >= 2 * BEAT;
  const z = after ? interpolate(f, [2 * BEAT, 2 * BEAT + 12], [1.1, 1], { ...clamp, easing: ease }) : interpolate(f, [0, 2 * BEAT], [1, 1.05]);
  const imgH = L.vertical ? VH : L.H;
  const imgW = L.vertical ? imgH * (16 / 9) : L.W;
  return (
    <AbsoluteFill style={{ overflow: "hidden" }}>
      <Img src={staticFile(after ? "hook-after.jpg" : "hook-before.jpg")} style={{ position: "absolute", width: imgW, height: imgH, left: (L.W - imgW) / 2, top: L.vertical ? VTOP : 0, objectFit: "cover", scale: String(z) }} />
      {!after && <div style={{ position: "absolute", left: 40, top: L.vertical ? VTOP + 30 : 50, padding: "10px 22px", borderRadius: 999, background: "rgba(0,0,0,.6)", color: "#ddd", font: `600 ${L.vertical ? 40 : 34}px ${FONT}` }}>Laptop webcam</div>}
      {after && <div style={{ position: "absolute", right: 40, top: L.vertical ? VTOP + 30 : 50, padding: "10px 22px", borderRadius: 999, background: ORANGE, color: "#fff", font: `700 ${L.vertical ? 40 : 34}px ${FONT}` }}>KISSIK</div>}
      <Sequence durationInFrames={2 * BEAT} layout="none"><Pop text="Your" accent="webcam." size={L.vertical ? 130 : 150} y={L.vertical ? 300 : L.H - 230} /></Sequence>
      <Sequence from={2 * BEAT} layout="none"><Flash /><Pop text="Studio" accent="look." size={L.vertical ? 130 : 150} y={L.vertical ? 300 : L.H - 230} /></Sequence>
    </AbsoluteFill>
  );
};

const End: React.FC<{ L: Layout }> = ({ L }) => {
  const f = useCurrentFrame();
  const { fps } = useVideoConfig();
  const p = spring({ frame: f, fps, config: { damping: 14 } });
  const b = interpolate(f, [BEAT, 2 * BEAT], [0, 1], { ...clamp, easing: ease });
  const c = interpolate(f, [2 * BEAT, 3 * BEAT], [0, 1], { ...clamp, easing: ease });
  const v = L.vertical;
  return (
    <AbsoluteFill style={{ alignItems: "center", justifyContent: "center", flexDirection: "column", gap: v ? 44 : 30, fontFamily: FONT, background: "radial-gradient(900px 600px at 50% 30%, rgba(255,138,61,.28), transparent 70%), #07070a" }}>
      <div style={{ display: "flex", alignItems: "center", gap: 28, scale: String(0.7 + 0.3 * p), opacity: p }}>
        <Img src={staticFile("kissik-icon-512.png")} style={{ width: v ? 150 : 130, height: v ? 150 : 130, borderRadius: 30 }} />
        <div style={{ color: "#fff", font: `900 ${v ? 130 : 130}px ${FONT}`, letterSpacing: "-.03em" }}>KISSIK</div>
      </div>
      <div style={{ color: "#fff", font: `800 ${v ? 62 : 58}px ${FONT}`, opacity: b, textAlign: "center", maxWidth: v ? 900 : 1600, lineHeight: 1.2 }}>
        Free. <span style={{ color: ORANGE }}>No watermark.</span> Open source.
      </div>
      <div style={{ padding: v ? "22px 44px" : "18px 40px", borderRadius: 999, background: ORANGE, color: "#fff", font: `800 ${v ? 50 : 46}px ${FONT}`, opacity: c, scale: String(0.9 + 0.1 * c) }}>kissik-studio.vercel.app</div>
      <div style={{ color: "#a9a9b3", font: `500 ${v ? 36 : 32}px ${FONT}`, opacity: c }}>Windows 11 · Android phone camera</div>
    </AbsoluteFill>
  );
};

const Cut: React.FC<{ L: Layout; music: boolean }> = ({ L, music }) => {
  let at = HOOK;
  return (
    <AbsoluteFill style={{ background: "#07070a" }}>
      {music && <Audio src={staticFile("music.mp3")} trimBefore={MUSIC_START} volume={(f) => interpolate(f, [0, 3, FAST_LEN - 30, FAST_LEN], [0, 1, 1, 0], clamp)} />}
      <Sequence durationInFrames={HOOK}><Hook L={L} /></Sequence>
      <Sequence durationInFrames={10}><Audio src={staticFile("sfx-whoosh.wav")} volume={0.5} /></Sequence>
      <Sequence from={2 * BEAT} durationInFrames={20}><Audio src={staticFile("sfx-impact.wav")} volume={0.9} /></Sequence>
      {SHOTS.map((s, i) => {
        const from = at;
        const len = s.beats * BEAT;
        at += len;
        return (
          <React.Fragment key={i}>
            <Sequence from={from} durationInFrames={len}>
              <Clip s={s} L={L} />
              <AbsoluteFill style={{ background: L.vertical ? "linear-gradient(180deg, #07070a 0%, #07070a 30%, transparent 32%)" : "linear-gradient(0deg, rgba(0,0,0,.75), transparent 38%)" }} />
              <Flash />
              <Pop text={s.text} accent={s.accent} size={L.vertical ? 120 : 130} y={L.vertical ? 300 : L.H - 210} />
            </Sequence>
            <Sequence from={from - 4} durationInFrames={14}><Audio src={staticFile("sfx-whoosh.wav")} volume={0.3} /></Sequence>
            <Sequence from={from} durationInFrames={4}><Audio src={staticFile("sfx-click.wav")} volume={0.35} /></Sequence>
          </React.Fragment>
        );
      })}
      <Sequence from={at} durationInFrames={END}><Flash /><End L={L} /></Sequence>
      <Sequence from={at} durationInFrames={20}><Audio src={staticFile("sfx-impact.wav")} volume={0.9} /></Sequence>
    </AbsoluteFill>
  );
};

export const PromoFastCompositions = () => (
  <>
    <Composition id="PromoWide" component={Cut} durationInFrames={FAST_LEN} fps={FPS} width={1920} height={1080} defaultProps={{ L: { W: 1920, H: 1080, vertical: false }, music: true }} />
    <Composition id="PromoVertical" component={Cut} durationInFrames={FAST_LEN} fps={FPS} width={1080} height={1920} defaultProps={{ L: { W: 1080, H: 1920, vertical: true }, music: true }} />
  </>
);
