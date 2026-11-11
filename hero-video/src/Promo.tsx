import React from "react";
import { AbsoluteFill, Composition, Easing, Img, interpolate, Sequence, spring, staticFile, useCurrentFrame, useVideoConfig } from "remotion";
import { Framing, NameCard, Scenes, Simple } from "./Features";
import { Pairing } from "./Pairing";

// Social promo (X, Reddit): a hook, five features with a headline each, and an end card.
const FPS = 30;
const ORANGE = "#ff8a3d";
const FONT = "Segoe UI, Inter, sans-serif";
const clamp = { extrapolateLeft: "clamp", extrapolateRight: "clamp" } as const;
const ease = Easing.bezier(0.16, 1, 0.3, 1);

const TITLE = 75;
const END = 105;
const FEATURES = [
  { C: Pairing, len: 8 * FPS, kick: "Phone as webcam", head: "Scan a code. Your phone is the camera." },
  { C: NameCard, len: 6 * FPS, kick: "Name cards", head: "Your name on screen in one click." },
  { C: Scenes, len: 6 * FPS, kick: "Scenes", head: "Switch the look live, mid-call." },
  { C: Framing, len: 6 * FPS, kick: "Smart framing", head: "Stays on you when you move." },
  { C: Simple, len: 6 * FPS, kick: "Simple mode", head: "Five big buttons. Record in one tap." },
];
export const PROMO_LEN = TITLE + FEATURES.reduce((s, x) => s + x.len, 0) + END;

const Bg: React.FC = () => {
  const f = useCurrentFrame();
  const t = (f / PROMO_LEN) * Math.PI * 2;
  return (
    <AbsoluteFill style={{ background: "#07070a", overflow: "hidden" }}>
      <div style={{ position: "absolute", width: 1300, height: 1300, borderRadius: "50%", left: 200 + Math.cos(t) * 120, top: -500, background: "radial-gradient(circle, rgba(255,138,61,.35), rgba(255,138,61,0) 62%)", filter: "blur(30px)" }} />
      <div style={{ position: "absolute", width: 1100, height: 1100, borderRadius: "50%", left: 1000 + Math.sin(t) * 140, top: 400, background: "radial-gradient(circle, rgba(124,92,255,.28), rgba(124,92,255,0) 62%)", filter: "blur(30px)" }} />
    </AbsoluteFill>
  );
};

const Logo: React.FC<{ size: number }> = ({ size }) => (
  <Img src={staticFile("kissik-icon-512.png")} style={{ width: size, height: size, borderRadius: size * 0.22 }} />
);

const Fade: React.FC<{ len: number; children: React.ReactNode }> = ({ len, children }) => {
  const f = useCurrentFrame();
  const o = interpolate(f, [0, 10, len - 10, len], [0, 1, 1, 0], clamp);
  return <AbsoluteFill style={{ opacity: o }}>{children}</AbsoluteFill>;
};

const Title: React.FC = () => {
  const f = useCurrentFrame();
  const { fps } = useVideoConfig();
  const p = spring({ frame: f, fps, config: { damping: 18 } });
  const sub = interpolate(f, [18, 34], [0, 1], { ...clamp, easing: ease });
  return (
    <Fade len={TITLE}>
      <AbsoluteFill style={{ alignItems: "center", justifyContent: "center", flexDirection: "column", gap: 34, fontFamily: FONT }}>
        <div style={{ scale: String(0.8 + 0.2 * p), opacity: p }}><Logo size={150} /></div>
        <div style={{ color: "#fff", font: `800 104px ${FONT}`, letterSpacing: "-.02em", textAlign: "center", lineHeight: 1.05, opacity: p, translate: `0px ${(1 - p) * 30}px` }}>
          Any camera. Studio look. <span style={{ color: ORANGE }}>Free.</span>
        </div>
        <div style={{ color: "#c9c9d1", font: `500 40px ${FONT}`, opacity: sub }}>KISSIK for Windows 11 · your phone as a webcam</div>
      </AbsoluteFill>
    </Fade>
  );
};

// A 1280x720 feature clip shown at 1.3x under a headline band.
const Feature: React.FC<{ C: React.FC; len: number; kick: string; head: string; n: number }> = ({ C, len, kick, head, n }) => {
  const f = useCurrentFrame();
  const h = interpolate(f, [4, 22], [0, 1], { ...clamp, easing: ease });
  const S = 1.2;
  return (
    <Fade len={len}>
      <div style={{ position: "absolute", left: 0, right: 0, top: 34, textAlign: "center", fontFamily: FONT, opacity: h, translate: `0px ${(1 - h) * -16}px` }}>
        <div style={{ color: ORANGE, font: `700 26px ${FONT}`, letterSpacing: ".12em", textTransform: "uppercase" }}>{n} · {kick}</div>
        <div style={{ color: "#fff", font: `800 56px ${FONT}`, letterSpacing: "-.01em", marginTop: 6 }}>{head}</div>
      </div>
      <div style={{ position: "absolute", left: (1920 - 1280 * S) / 2, top: 172, width: 1280, height: 720, scale: String(S), transformOrigin: "0 0", borderRadius: 18, overflow: "hidden" }}>
        <C />
      </div>
    </Fade>
  );
};

const EndCard: React.FC = () => {
  const f = useCurrentFrame();
  const { fps } = useVideoConfig();
  const p = spring({ frame: f, fps, config: { damping: 18 } });
  const b = interpolate(f, [16, 32], [0, 1], { ...clamp, easing: ease });
  return (
    <Fade len={END}>
      <AbsoluteFill style={{ alignItems: "center", justifyContent: "center", flexDirection: "column", gap: 30, fontFamily: FONT }}>
        <div style={{ display: "flex", alignItems: "center", gap: 28, opacity: p, scale: String(0.9 + 0.1 * p) }}>
          <Logo size={120} />
          <div style={{ color: "#fff", font: `800 110px ${FONT}`, letterSpacing: "-.02em" }}>KISSIK</div>
        </div>
        <div style={{ color: "#fff", font: `700 50px ${FONT}`, opacity: b }}>Free · No account · No watermark · Open source</div>
        <div style={{ marginTop: 14, padding: "18px 40px", borderRadius: 999, background: ORANGE, color: "#fff", font: `700 44px ${FONT}`, opacity: b }}>kissik-studio.vercel.app</div>
        <div style={{ color: "#a9a9b3", font: `500 32px ${FONT}`, opacity: b }}>On the Microsoft Store · Android app for the phone camera</div>
      </AbsoluteFill>
    </Fade>
  );
};

const Promo: React.FC = () => {
  let at = TITLE;
  return (
    <AbsoluteFill>
      <Bg />
      <Sequence durationInFrames={TITLE}><Title /></Sequence>
      {FEATURES.map((x, i) => {
        const from = at;
        at += x.len;
        return (
          <Sequence key={x.kick} from={from} durationInFrames={x.len}>
            <Feature {...x} n={i + 1} />
          </Sequence>
        );
      })}
      <Sequence from={at} durationInFrames={END}><EndCard /></Sequence>
    </AbsoluteFill>
  );
};

export const PromoComposition = () => (
  <Composition id="Promo" component={Promo} durationInFrames={PROMO_LEN} fps={FPS} width={1920} height={1080} />
);
