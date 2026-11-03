import React from "react";
import {
  AbsoluteFill,
  Composition,
  Easing,
  Img,
  Sequence,
  interpolate,
  spring,
  staticFile,
  useCurrentFrame,
  useVideoConfig,
} from "remotion";

// KISSIK promo: 20 s, 1920x1080, 30 fps. Colours follow the app (DgPage + gold).
const BG = "#050507";
const GOLD = "#FFD36B";
const TEXT = "#F4F5F7";
const MUTED = "#a7aab2";
const FONT = '"Segoe UI Variable Display","Segoe UI",system-ui,sans-serif';
const ease = Easing.bezier(0.16, 1, 0.3, 1);

export const MyComposition = () => (
  <Composition id="KissikPromo" component={Promo} durationInFrames={600} fps={30} width={1920} height={1080} />
);

const Bloom: React.FC = () => {
  const f = useCurrentFrame();
  return (
    <AbsoluteFill>
      {[
        { c: "#b98a1c", x: 38, y: 30, s: 900, o: 0.55, sp: 0.004 },
        { c: "#8a3f1c", x: 62, y: 55, s: 700, o: 0.35, sp: 0.006 },
        { c: "#c2541c", x: 20, y: 65, s: 650, o: 0.35, sp: 0.005 },
      ].map((b, i) => (
        <div
          key={i}
          style={{
            position: "absolute",
            left: `${b.x + Math.sin(f * b.sp * 6 + i) * 6}%`,
            top: `${b.y + Math.cos(f * b.sp * 5 + i) * 5}%`,
            width: b.s,
            height: b.s,
            marginLeft: -b.s / 2,
            marginTop: -b.s / 2,
            borderRadius: "50%",
            background: b.c,
            opacity: b.o,
            filter: "blur(140px)",
          }}
        />
      ))}
    </AbsoluteFill>
  );
};

// words rise in and come into focus, like the website's opening title
const Words: React.FC<{ text: string; size: number; delay?: number; color?: string }> = ({ text, size, delay = 0, color = TEXT }) => {
  const f = useCurrentFrame();
  return (
    <div style={{ fontFamily: FONT, fontWeight: 600, fontSize: size, lineHeight: 1.05, letterSpacing: "-0.02em", color, textAlign: "center" }}>
      {text.split(" ").map((w, i) => {
        const t = interpolate(f - delay - i * 4, [0, 22], [0, 1], { extrapolateLeft: "clamp", extrapolateRight: "clamp", easing: ease });
        return (
          <span key={i} style={{ display: "inline-block", marginRight: "0.25em", opacity: t, filter: `blur(${(1 - t) * 16}px)`, transform: `translateY(${(1 - t) * 0.45}em)` }}>
            {w}
          </span>
        );
      })}
    </div>
  );
};

const fadeOut = (f: number, end: number, len = 12) => interpolate(f, [end - len, end], [1, 0], { extrapolateLeft: "clamp", extrapolateRight: "clamp" });

const Shot: React.FC<{ src: string; style?: React.CSSProperties }> = ({ src, style }) => (
  <div style={{ borderRadius: 18, overflow: "hidden", boxShadow: "0 50px 140px rgba(0,0,0,.6)", border: "1px solid rgba(255,255,255,.12)", ...style }}>
    <Img src={staticFile(src)} style={{ width: "100%", display: "block" }} />
  </div>
);

const Title: React.FC = () => {
  const f = useCurrentFrame();
  return (
    <AbsoluteFill style={{ justifyContent: "center", alignItems: "center", opacity: fadeOut(f, 90) }}>
      <Words text="Your webcam," size={150} />
      <Words text="at its best." size={150} delay={10} color={GOLD} />
    </AbsoluteFill>
  );
};

const Hero: React.FC = () => {
  const f = useCurrentFrame();
  const { fps } = useVideoConfig();
  const s = spring({ frame: f, fps, config: { damping: 200 }, durationInFrames: 45 });
  const tilt = interpolate(f, [0, 70], [24, 0], { extrapolateRight: "clamp", easing: ease });
  const sheen = interpolate(f, [55, 95], [-120, 120], { extrapolateLeft: "clamp", extrapolateRight: "clamp" });
  return (
    <AbsoluteFill style={{ alignItems: "center", opacity: fadeOut(f, 120) }}>
      <div style={{ marginTop: 70 }}>
        <Words text="A free camera studio for Windows 11" size={60} delay={4} />
      </div>
      <div style={{ perspective: 1800, marginTop: 50 }}>
        <div style={{ position: "relative", width: 1440, transformOrigin: "50% 0", transform: `translateY(${(1 - s) * 300}px) rotateX(${tilt}deg) scale(${0.9 + 0.1 * s})`, opacity: s }}>
          <Shot src="studio.jpg" />
          <div style={{ position: "absolute", inset: 0, borderRadius: 18, overflow: "hidden", pointerEvents: "none" }}>
            <div style={{ position: "absolute", inset: 0, background: "linear-gradient(105deg,transparent 35%,rgba(255,255,255,.28) 50%,transparent 65%)", transform: `translateX(${sheen}%)` }} />
          </div>
        </div>
      </div>
    </AbsoluteFill>
  );
};

// one feature beat: screenshot wipes open from the side while slowly zooming; caption on the other side
const Feature: React.FC<{ src: string; title: string; line: string; flip?: boolean; focus?: string }> = ({ src, title, line, flip, focus = "top left" }) => {
  const f = useCurrentFrame();
  const wipe = interpolate(f, [0, 24], [100, 0], { extrapolateRight: "clamp", easing: Easing.bezier(0.77, 0, 0.18, 1) });
  const zoom = interpolate(f, [0, 60], [1.18, 1.04], { extrapolateRight: "clamp" });
  const out = fadeOut(f, 60, 8);
  const clip = flip ? `inset(0 ${wipe}% 0 0 round 18px)` : `inset(0 0 0 ${wipe}% round 18px)`;
  return (
    <AbsoluteFill style={{ flexDirection: flip ? "row-reverse" : "row", alignItems: "center", gap: 80, padding: "0 110px", opacity: out }}>
      <div style={{ width: 560, flex: "none" }}>
        <Words text={title} size={84} />
        <div style={{ fontFamily: FONT, fontSize: 34, color: MUTED, marginTop: 24, textAlign: "center", opacity: interpolate(f, [12, 28], [0, 1], { extrapolateLeft: "clamp", extrapolateRight: "clamp" }) }}>{line}</div>
      </div>
      <div style={{ position: "relative", flex: 1, height: 580, borderRadius: 18, overflow: "hidden", clipPath: clip, boxShadow: "0 50px 140px rgba(0,0,0,.6)", background: "#0b0c0f" }}>
        {/* top: -6% hides the app's title bar, which still carries the old name in these window pictures */}
        <Img src={staticFile(src)} style={{ position: "absolute", left: 0, top: "-6%", width: "100%", transform: `scale(${zoom})`, transformOrigin: focus }} />
      </div>
    </AbsoluteFill>
  );
};

const Stats: React.FC = () => {
  const f = useCurrentFrame();
  const items: [number, string, string][] = [
    [0, "", "accounts"],
    [4, "K", "recording"],
    [100, "%", "on your computer"],
    [0, "", "watermarks"],
  ];
  return (
    <AbsoluteFill style={{ justifyContent: "center", alignItems: "center", flexDirection: "row", gap: 110, opacity: fadeOut(f, 90) }}>
      {items.map(([n, suf, label], i) => {
        const t = interpolate(f - i * 6, [0, 36], [0, 1], { extrapolateLeft: "clamp", extrapolateRight: "clamp", easing: ease });
        return (
          <div key={i} style={{ textAlign: "center", opacity: t, transform: `translateY(${(1 - t) * 40}px)` }}>
            <div style={{ fontFamily: FONT, fontWeight: 600, fontSize: 170, color: GOLD, lineHeight: 1 }}>{Math.round(n * t)}{suf}</div>
            <div style={{ fontFamily: FONT, fontSize: 36, color: MUTED, marginTop: 16 }}>{label}</div>
          </div>
        );
      })}
    </AbsoluteFill>
  );
};

const EndCard: React.FC = () => {
  const f = useCurrentFrame();
  const { fps } = useVideoConfig();
  const s = spring({ frame: f, fps, config: { damping: 14, mass: 0.6 } });
  // the icon presses down once, like a click
  const press = interpolate(f, [18, 24, 32], [0, 1, 0], { extrapolateLeft: "clamp", extrapolateRight: "clamp" });
  return (
    <AbsoluteFill style={{ justifyContent: "center", alignItems: "center" }}>
      <div style={{ display: "flex", alignItems: "center", gap: 36, transform: `scale(${0.8 + 0.2 * s})`, opacity: s }}>
        <Img src={staticFile("kissik-icon-512.png")} style={{ width: 170, height: 170, borderRadius: 38, transform: `translateY(${press * 10}px) scale(${1 - press * 0.06})`, boxShadow: "0 30px 80px rgba(232,85,26,.35)" }} />
        <div style={{ fontFamily: FONT, fontWeight: 600, fontSize: 170, color: TEXT, letterSpacing: "-0.02em" }}>KISSIK</div>
      </div>
      <div style={{ marginTop: 40 }}>
        <Words text="Free for Windows 11. No account. No watermark." size={44} delay={14} color={MUTED} />
      </div>
    </AbsoluteFill>
  );
};

export const Promo: React.FC = () => (
  <AbsoluteFill style={{ background: BG }}>
    <Bloom />
    <Sequence durationInFrames={90}><Title /></Sequence>
    <Sequence from={90} durationInFrames={120}><Hero /></Sequence>
    <Sequence from={210} durationInFrames={60}><Feature src="camera.jpg" title="Blur or replace" line="Your background, on your own GPU." /></Sequence>
    <Sequence from={270} durationInFrames={60}><Feature src="studio.jpg" title="Always in frame" line="Auto-framing follows you." flip focus="top right" /></Sequence>
    <Sequence from={330} durationInFrames={60}><Feature src="layers.jpg" title="Your name, your logo" line="Scenes in one click." /></Sequence>
    <Sequence from={390} durationInFrames={60}><Feature src="light.jpg" title="Your screen is your light" line="Ring, bars or frame." flip focus="center" /></Sequence>
    <Sequence from={450} durationInFrames={90}><Stats /></Sequence>
    <Sequence from={540} durationInFrames={60}><EndCard /></Sequence>
  </AbsoluteFill>
);
