import { Video } from "@remotion/media";
import React from "react";
import {
  AbsoluteFill,
  Composition,
  Easing,
  Img,
  interpolate,
  spring,
  staticFile,
  useCurrentFrame,
  useVideoConfig,
} from "remotion";

// 12 s seamless loop for the website hero. Frame 0 and the last frame show the same settled studio window.
const FPS = 30;
const DURATION = 12 * FPS;
const ORANGE = "#ff8a3d";

// The studio screenshot is 1600x791; shown 1500 wide. The live preview area sits at these display coordinates.
const WIN_W = 1500;
const WIN_H = 741;
const PREVIEW = { x: 335, y: 74, w: 909, h: 511 };
const NAME_TILE = { x: 961, y: 653 };

const clamp = { extrapolateLeft: "clamp", extrapolateRight: "clamp" } as const;
const ease = Easing.bezier(0.16, 1, 0.3, 1);

// 0 → 1 between a and b, back to 0 between c and d.
const inOut = (f: number, a: number, b: number, c: number, d: number) =>
  interpolate(f, [a, b, c, d], [0, 1, 1, 0], { ...clamp, easing: ease });

const Glow: React.FC = () => {
  const f = useCurrentFrame();
  const t = (f / DURATION) * Math.PI * 2;
  return (
    <AbsoluteFill style={{ background: "#07070a", overflow: "hidden" }}>
      <div style={{ position: "absolute", width: 1300, height: 1300, borderRadius: "50%", left: 260 + Math.cos(t) * 120, top: -380 + Math.sin(t) * 80, background: "radial-gradient(circle, rgba(255,138,61,.42), rgba(255,138,61,0) 62%)", filter: "blur(30px)" }} />
      <div style={{ position: "absolute", width: 1100, height: 1100, borderRadius: "50%", left: 980 + Math.sin(t) * 140, top: 260 + Math.cos(t) * 90, background: "radial-gradient(circle, rgba(124,92,255,.35), rgba(124,92,255,0) 62%)", filter: "blur(30px)" }} />
      <div style={{ position: "absolute", width: 900, height: 900, borderRadius: "50%", left: -260 + Math.sin(t + 2) * 100, top: 420, background: "radial-gradient(circle, rgba(255,77,141,.22), rgba(255,77,141,0) 62%)", filter: "blur(30px)" }} />
      <AbsoluteFill style={{ backgroundImage: "linear-gradient(rgba(255,255,255,.035) 1px, transparent 1px), linear-gradient(90deg, rgba(255,255,255,.035) 1px, transparent 1px)", backgroundSize: "64px 64px", maskImage: "radial-gradient(ellipse at center, black 30%, transparent 75%)" }} />
    </AbsoluteFill>
  );
};

const Chip: React.FC<{ children: React.ReactNode; dot?: string }> = ({ children, dot = "#3ddc84" }) => (
  <div style={{ display: "inline-flex", alignItems: "center", gap: 10, padding: "10px 18px", borderRadius: 999, background: "rgba(20,20,24,.82)", border: "1px solid rgba(255,255,255,.14)", color: "#fff", font: "600 24px Segoe UI, Inter, sans-serif", backdropFilter: "blur(10px)", whiteSpace: "nowrap" }}>
    <span style={{ width: 10, height: 10, borderRadius: 5, background: dot, boxShadow: `0 0 12px ${dot}` }} />
    {children}
  </div>
);

// A floating camera card that flies out from behind the window and back.
const ClipCard: React.FC<{ src: string; label: string; x: number; y: number; w: number; rot: number; start: number }> = ({ src, label, x, y, w, rot, start }) => {
  const f = useCurrentFrame();
  const { fps } = useVideoConfig();
  const p = spring({ frame: f - start, fps, config: { damping: 16, mass: 0.9 } }) - spring({ frame: f - (start + 150), fps, config: { damping: 200 } });
  const h = (w * 9) / 16;
  return (
    <div style={{ position: "absolute", left: 960 - w / 2, top: 540 - h / 2, width: w, translate: `${x * p}px ${y * p}px`, rotate: `${rot * p}deg`, scale: String(0.5 + 0.5 * p), opacity: Math.min(1, p * 1.6) }}>
      <div style={{ width: w, height: h, borderRadius: 22, overflow: "hidden", border: "2px solid rgba(255,255,255,.18)", boxShadow: "0 30px 80px rgba(0,0,0,.6)" }}>
        <Video src={staticFile(src)} loop muted style={{ width: "100%", height: "100%", objectFit: "cover" }} />
      </div>
      <div style={{ marginTop: 14 }}><Chip>{label}</Chip></div>
    </div>
  );
};

const Studio: React.FC = () => {
  const f = useCurrentFrame();
  const { fps } = useVideoConfig();
  const t = (f / DURATION) * Math.PI * 2;

  // Act 1 (1–4.6 s): cursor clicks the Name Card scene, the name card slides onto the live picture.
  const cursorIn = inOut(f, 20, 50, 120, 140);
  const click = interpolate(f, [52, 58, 70], [0, 1, 0], clamp);
  const card = inOut(f, 58, 78, 128, 140);
  // Act 2 (4.7–10 s): the window steps back while cards and the phone take the stage.
  const back = inOut(f, 140, 170, 290, 320);
  // Act 3: the phone joins as a camera.
  const phone = spring({ frame: f - 175, fps, config: { damping: 15 } }) - spring({ frame: f - 285, fps, config: { damping: 200 } });

  return (
    <AbsoluteFill>
      <AbsoluteFill style={{ perspective: 2200 }}>
        <div style={{ position: "absolute", left: 960 - WIN_W / 2, top: 540 - WIN_H / 2 + 20, width: WIN_W, height: WIN_H, rotate: `x ${6 + Math.sin(t) * 2}deg`, translate: `0px ${Math.sin(t * 2) * 6}px`, scale: String(1 - back * 0.3), filter: `brightness(${1 - back * 0.35})` }}>
          <div style={{ position: "absolute", inset: -2, borderRadius: 26, boxShadow: `0 0 0 1px rgba(255,255,255,.12), 0 50px 140px rgba(0,0,0,.7), 0 0 120px rgba(255,138,61,${0.18 + back * 0.1})` }} />
          <div style={{ position: "absolute", inset: 0, borderRadius: 24, overflow: "hidden" }}>
            <Img src={staticFile("studio.jpg")} style={{ width: WIN_W, height: WIN_H }} />
            <div style={{ position: "absolute", left: PREVIEW.x, top: PREVIEW.y, width: PREVIEW.w, height: PREVIEW.h, overflow: "hidden", background: "#000" }}>
              <Video src={staticFile("professional.mp4")} loop muted style={{ width: "100%", height: "100%", objectFit: "cover" }} />
              {/* The Name Card scene */}
              <div style={{ position: "absolute", left: 34, bottom: 34, display: "flex", alignItems: "center", gap: 18, padding: "18px 26px 18px 18px", borderRadius: 18, background: "rgba(18,18,22,.78)", border: "1px solid rgba(255,255,255,.12)", backdropFilter: "blur(12px)", translate: `${(1 - card) * -60}px 0px`, opacity: card }}>
                <div style={{ width: 62, height: 62, borderRadius: 31, background: ORANGE, color: "#fff", display: "flex", alignItems: "center", justifyContent: "center", font: "700 26px Segoe UI, sans-serif" }}>AO</div>
                <div>
                  <div style={{ color: "#fff", font: "700 32px Segoe UI, sans-serif" }}>Amara Okafor</div>
                  <div style={{ color: ORANGE, font: "500 21px Segoe UI, sans-serif" }}>VP Operations · she/her</div>
                </div>
              </div>
            </div>
            {/* Highlight on the clicked scene tile */}
            <div style={{ position: "absolute", left: NAME_TILE.x - 66, top: NAME_TILE.y - 40, width: 132, height: 82, borderRadius: 10, border: `3px solid ${ORANGE}`, opacity: card, boxShadow: `0 0 30px ${ORANGE}` }} />
          </div>
          {/* Cursor */}
          <div style={{ position: "absolute", left: interpolate(f, [20, 50], [1200, NAME_TILE.x], { ...clamp, easing: ease }), top: interpolate(f, [20, 50], [420, NAME_TILE.y], { ...clamp, easing: ease }), opacity: cursorIn }}>
            <div style={{ position: "absolute", left: -30, top: -30, width: 60, height: 60, borderRadius: 30, border: `3px solid ${ORANGE}`, scale: String(0.4 + click * 1.2), opacity: click }} />
            <svg width="38" height="38" viewBox="0 0 24 24" style={{ filter: "drop-shadow(0 4px 8px rgba(0,0,0,.6))" }}><path d="M4 2l16 9-7 2-3 7z" fill="#fff" stroke="#111" strokeWidth="1.2" /></svg>
          </div>
        </div>
      </AbsoluteFill>

      <ClipCard src="podcast.mp4" label="Podcasts" x={-600} y={-250} w={440} rot={-6} start={150} />
      <ClipCard src="education.mp4" label="Classes" x={-640} y={230} w={400} rot={4} start={160} />
      <ClipCard src="creative.mp4" label="Music" x={500} y={-290} w={420} rot={5} start={170} />
      <ClipCard src="streaming.mp4" label="Streams" x={20} y={300} w={380} rot={-2} start={185} />

      {/* The phone joins as a camera */}
      <div style={{ position: "absolute", left: 1560, top: 400, translate: `${(1 - phone) * 600}px 0px`, rotate: `${(1 - phone) * 12 + 6}deg`, opacity: Math.min(1, phone * 1.5) }}>
        <div style={{ width: 250, height: 520, borderRadius: 40, background: "#111", padding: 10, border: "2px solid rgba(255,255,255,.2)", boxShadow: "0 40px 100px rgba(0,0,0,.7)" }}>
          <div style={{ width: "100%", height: "100%", borderRadius: 32, overflow: "hidden", position: "relative" }}>
            <Video src={staticFile("social.mp4")} loop muted style={{ position: "absolute", height: "100%", width: 889, left: -330, maxWidth: "none" }} />
            <div style={{ position: "absolute", top: 16, left: 14 }}><Chip>Live · Wi-Fi</Chip></div>
          </div>
        </div>
        <div style={{ marginTop: 16, textAlign: "center" }}><Chip dot={ORANGE}>Your phone is the camera</Chip></div>
      </div>

      {/* Centre tag while the cards are out */}
      <div style={{ position: "absolute", left: 0, right: 0, top: 488, textAlign: "center", opacity: inOut(f, 175, 200, 280, 300), scale: String(0.9 + inOut(f, 175, 200, 280, 300) * 0.1) }}>
        <Chip dot={ORANGE}>One camera called “KISSIK” in every app</Chip>
      </div>
    </AbsoluteFill>
  );
};

export const MyComponent: React.FC = () => (
  <AbsoluteFill>
    <Glow />
    <Studio />
  </AbsoluteFill>
);

export const MyComposition = () => (
  <Composition id="Hero" component={MyComponent} durationInFrames={DURATION} fps={FPS} width={1920} height={1080} />
);
