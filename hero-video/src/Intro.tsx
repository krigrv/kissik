import React from "react";
import { AbsoluteFill, Audio, Composition, Easing, Img, interpolate, Sequence, spring, staticFile, useCurrentFrame, useVideoConfig } from "remotion";
import { MyComponent } from "./Composition";
import { Scenes, Simple } from "./Features";
import { Pairing } from "./Pairing";
import { Fix, Layout } from "./Story";

// "Introducing KISSIK": a title, then one section per feature with its own clip, then the end card.
// Music: the gentle take of "Calm Resolve" (Suno), kept low under the picture.
const FPS = 30;
const ORANGE = "#ff8a3d";
const FONT = "Segoe UI, Inter, sans-serif";
const clamp = { extrapolateLeft: "clamp", extrapolateRight: "clamp" } as const;
const ease = Easing.bezier(0.16, 1, 0.3, 1);
const WIDE: Layout = { W: 1920, H: 1080, vertical: false };

type Section = {
  kick: string;
  head: string;
  body?: string;
  ticks?: { at: number; text: string }[];
  len: number;
  clip: React.FC;
  cw: number;
  ch: number;
  vfx?: number;
};

const SECTIONS: Section[] = [
  {
    kick: "Look better on any webcam", head: "Fix your picture in three clicks.", len: 288, cw: 1920, ch: 1080, vfx: 0.52,
    ticks: [{ at: 30, text: "Screen light: your monitor becomes a soft key light" }, { at: 102, text: "Smart framing: stays centred on you" }, { at: 174, text: "Background blur: on your own PC" }],
    clip: () => <Fix L={WIDE} bare />,
  },
  { kick: "Your phone as a webcam", head: "Scan a code. Your phone is the camera.", body: "Up to 4K over Wi-Fi with the free Android app.", len: 170, cw: 1280, ch: 720, vfx: 0.66, clip: () => <Sequence from={-24} layout="none"><Pairing src="professional.mp4" phoneFocus={0.36} overlays /></Sequence> },
  { kick: "Scenes and name cards", head: "Your name on screen in one click.", body: "Lower thirds, name cards, bubble cam and more.", len: 150, cw: 1280, ch: 720, vfx: 0.36, clip: () => <Sequence from={-14} layout="none"><Scenes /></Sequence> },
  { kick: "Works everywhere", head: "One camera in every app.", body: "Zoom, Teams, Google Meet, Discord, OBS and your browser.", len: 140, cw: 1920, ch: 1080, clip: () => <Sequence from={-160} layout="none"><MyComponent /></Sequence> },
  { kick: "Simple mode", head: "Five big buttons. Record in one tap.", body: "Recordings survive a crash.", len: 120, cw: 1280, ch: 720, clip: () => <Sequence from={-20} layout="none"><Simple /></Sequence> },
];
const TITLE = 96;
const END = 126;
export const INTRO_LEN = TITLE + SECTIONS.reduce((s, x) => s + x.len, 0) + END;
// Gentle second take of "Calm Resolve": soft from the start, its swell (~24 s in) arrives near the end card.
const MUSIC_START = Math.round(0.5 * FPS);

const Bg: React.FC = () => {
  const f = useCurrentFrame();
  const t = (f / INTRO_LEN) * Math.PI * 2;
  return (
    <AbsoluteFill style={{ background: "#07070a", overflow: "hidden" }}>
      <div style={{ position: "absolute", width: 1400, height: 1400, borderRadius: "50%", left: -300 + Math.cos(t) * 100, top: -700, background: "radial-gradient(circle, rgba(255,138,61,.26), rgba(255,138,61,0) 62%)", filter: "blur(30px)" }} />
      <div style={{ position: "absolute", width: 1200, height: 1200, borderRadius: "50%", right: -300 + Math.sin(t) * 120, bottom: -600, background: "radial-gradient(circle, rgba(124,92,255,.22), rgba(124,92,255,0) 62%)", filter: "blur(30px)" }} />
    </AbsoluteFill>
  );
};

const Fade: React.FC<{ len: number; children: React.ReactNode }> = ({ len, children }) => {
  const f = useCurrentFrame();
  return <AbsoluteFill style={{ opacity: interpolate(f, [0, 12, len - 12, len], [0, 1, 1, 0], clamp) }}>{children}</AbsoluteFill>;
};

const Title: React.FC<{ L: Layout }> = ({ L }) => {
  const f = useCurrentFrame();
  const { fps } = useVideoConfig();
  const p = spring({ frame: f, fps, config: { damping: 20 } });
  const a = interpolate(f, [14, 30], [0, 1], { ...clamp, easing: ease });
  const b = interpolate(f, [30, 46], [0, 1], { ...clamp, easing: ease });
  const v = L.vertical;
  return (
    <Fade len={TITLE}>
      <AbsoluteFill style={{ alignItems: "center", justifyContent: "center", flexDirection: "column", gap: v ? 34 : 26, fontFamily: FONT, padding: "0 70px", textAlign: "center" }}>
        <div style={{ color: "#bdbdc6", font: `600 ${v ? 44 : 38}px ${FONT}`, letterSpacing: ".04em", opacity: a }}>Introducing</div>
        <div style={{ display: "flex", alignItems: "center", gap: 28, opacity: p, scale: String(0.94 + 0.06 * p) }}>
          <Img src={staticFile("kissik-icon-512.png")} style={{ width: v ? 140 : 130, height: v ? 140 : 130, borderRadius: 30 }} />
          <div style={{ color: "#fff", font: `800 ${v ? 150 : 150}px ${FONT}`, letterSpacing: "-.03em" }}>KISSIK</div>
        </div>
        <div style={{ color: "#fff", font: `600 ${v ? 50 : 46}px ${FONT}`, opacity: b, lineHeight: 1.25 }}>
          A free, open-source camera studio <br />for <span style={{ color: ORANGE }}>Windows</span>.
        </div>
      </AbsoluteFill>
    </Fade>
  );
};

const Tick: React.FC<{ at: number; text: string; size: number }> = ({ at, text, size }) => {
  const f = useCurrentFrame();
  const o = interpolate(f, [at, at + 14], [0, 1], { ...clamp, easing: ease });
  return (
    <div style={{ display: "flex", gap: 16, alignItems: "flex-start", opacity: 0.35 + 0.65 * o }}>
      <div style={{ flex: "none", width: size * 1.25, height: size * 1.25, marginTop: size * 0.1, borderRadius: "50%", background: o > 0.5 ? ORANGE : "rgba(255,255,255,.14)", color: "#fff", display: "flex", alignItems: "center", justifyContent: "center", font: `900 ${size * 0.75}px ${FONT}`, scale: String(0.8 + 0.2 * o) }}>{o > 0.5 ? "✓" : ""}</div>
      <div style={{ color: "#fff", font: `600 ${size}px ${FONT}`, lineHeight: 1.3 }}>{text}</div>
    </div>
  );
};

const Feature: React.FC<{ s: Section; n: number; L: Layout }> = ({ s, n, L }) => {
  const f = useCurrentFrame();
  const t = interpolate(f, [0, 18], [0, 1], { ...clamp, easing: ease });
  const c = interpolate(f, [4, 26], [0, 1], { ...clamp, easing: ease });
  const v = L.vertical;
  const box = v ? { x: 0, y: 820, w: 1080, h: 1000 } : { x: 740, y: 214, w: 1120, h: 630 };
  const k = v ? box.h / s.ch : box.w / s.cw;
  const cl = v ? Math.min(0, Math.max(box.w - s.cw * k, box.w / 2 - s.cw * k * (s.vfx ?? 0.5))) : 0;
  return (
    <Fade len={s.len}>
      <div style={{ position: "absolute", left: v ? 70 : 90, top: v ? 200 : 250, width: v ? 940 : 600, fontFamily: FONT, opacity: t, translate: `0px ${(1 - t) * 20}px`, textAlign: "left" }}>
        <div style={{ color: ORANGE, font: `700 ${v ? 32 : 26}px ${FONT}`, letterSpacing: ".1em", textTransform: "uppercase" }}>{n}. {s.kick}</div>
        <div style={{ color: "#fff", font: `800 ${v ? 70 : 60}px ${FONT}`, letterSpacing: "-.02em", lineHeight: 1.1, marginTop: 14 }}>{s.head}</div>
        {s.body && <div style={{ color: "#bdbdc6", font: `500 ${v ? 38 : 32}px ${FONT}`, lineHeight: 1.35, marginTop: 22 }}>{s.body}</div>}
        {s.ticks && <div style={{ display: "flex", flexDirection: "column", gap: v ? 18 : 16, marginTop: 28 }}>{s.ticks.map((x) => <Tick key={x.text} at={x.at} text={x.text} size={v ? 36 : 28} />)}</div>}
      </div>
      <div style={{ position: "absolute", left: box.x, top: box.y, width: box.w, height: box.h, borderRadius: v ? 0 : 24, overflow: "hidden", boxShadow: "0 40px 120px rgba(0,0,0,.65), 0 0 0 1px rgba(255,255,255,.1)", opacity: c, translate: `${v ? 0 : (1 - c) * 80}px ${v ? (1 - c) * 60 : 0}px` }}>
        <div style={{ position: "absolute", left: cl, width: s.cw, height: s.ch, scale: String(k), transformOrigin: "0 0" }}><s.clip /></div>
      </div>
    </Fade>
  );
};

const End: React.FC<{ L: Layout }> = ({ L }) => {
  const f = useCurrentFrame();
  const { fps } = useVideoConfig();
  const p = spring({ frame: f, fps, config: { damping: 18 } });
  const b = interpolate(f, [18, 34], [0, 1], { ...clamp, easing: ease });
  const v = L.vertical;
  return (
    <Fade len={END}>
      <AbsoluteFill style={{ alignItems: "center", justifyContent: "center", flexDirection: "column", gap: v ? 40 : 28, fontFamily: FONT, textAlign: "center", padding: "0 60px" }}>
        <div style={{ display: "flex", alignItems: "center", gap: 26, opacity: p, scale: String(0.92 + 0.08 * p) }}>
          <Img src={staticFile("kissik-icon-512.png")} style={{ width: 120, height: 120, borderRadius: 28 }} />
          <div style={{ color: "#fff", font: `800 116px ${FONT}`, letterSpacing: "-.02em" }}>KISSIK</div>
        </div>
        <div style={{ color: "#e8e8ee", font: `600 ${v ? 50 : 46}px ${FONT}`, opacity: b }}>Make your video look its best.</div>
        <div style={{ color: "#a9a9b3", font: `500 ${v ? 38 : 34}px ${FONT}`, opacity: b }}>Free · No account · No watermark · Open source</div>
        <div style={{ marginTop: 10, padding: "18px 40px", borderRadius: 999, background: ORANGE, color: "#fff", font: `700 ${v ? 46 : 42}px ${FONT}`, opacity: b }}>kissik-studio.vercel.app</div>
        <div style={{ color: "#8d8d97", font: `500 ${v ? 32 : 28}px ${FONT}`, opacity: b }}>Microsoft Store · Android app for the phone camera</div>
      </AbsoluteFill>
    </Fade>
  );
};

const Intro: React.FC<{ L: Layout }> = ({ L }) => {
  let at = TITLE;
  return (
    <AbsoluteFill>
      <Bg />
      <Audio src={staticFile("intro-music.mp3")} trimBefore={MUSIC_START} volume={(f) => interpolate(f, [0, 30, INTRO_LEN - 45, INTRO_LEN], [0, 0.6, 0.6, 0], clamp)} />
      <Sequence durationInFrames={TITLE}><Title L={L} /></Sequence>
      {SECTIONS.map((s, i) => {
        const from = at;
        at += s.len;
        return <Sequence key={s.kick} from={from} durationInFrames={s.len}><Feature s={s} n={i + 1} L={L} /></Sequence>;
      })}
      <Sequence from={at} durationInFrames={END}><End L={L} /></Sequence>
    </AbsoluteFill>
  );
};

export const IntroCompositions = () => (
  <>
    <Composition id="IntroWide" component={Intro} durationInFrames={INTRO_LEN} fps={FPS} width={1920} height={1080} defaultProps={{ L: WIDE }} />
    <Composition id="IntroVertical" component={Intro} durationInFrames={INTRO_LEN} fps={FPS} width={1080} height={1920} defaultProps={{ L: { W: 1080, H: 1920, vertical: true } }} />
  </>
);
