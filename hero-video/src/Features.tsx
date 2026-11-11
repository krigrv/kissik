import { Video } from "@remotion/media";
import React from "react";
import { LiveTiles, STRIP_TILES } from "./LiveThumbs";
import { AbsoluteFill, Composition, Easing, Img, interpolate, staticFile, useCurrentFrame } from "remotion";

// Short loops for the website's feature cards. Each one is 6 s and ends where it starts.
const FPS = 30;
const LEN = 6 * FPS;
const ORANGE = "#ff8a3d";
const clamp = { extrapolateLeft: "clamp", extrapolateRight: "clamp" } as const;
const ease = Easing.bezier(0.16, 1, 0.3, 1);
const inOut = (f: number, a: number, b: number, c: number, d: number) =>
  interpolate(f, [a, b, c, d], [0, 1, 1, 0], { ...clamp, easing: ease });

const Bg: React.FC<{ children: React.ReactNode }> = ({ children }) => (
  <AbsoluteFill style={{ background: "radial-gradient(900px 500px at 30% 0%, rgba(255,138,61,.25), transparent 70%), #0b0b0e", fontFamily: "Segoe UI, Inter, sans-serif" }}>{children}</AbsoluteFill>
);

const Cursor: React.FC<{ x: number; y: number; opacity: number; click: number }> = ({ x, y, opacity, click }) => (
  <div style={{ position: "absolute", left: x, top: y, opacity }}>
    <div style={{ position: "absolute", left: -30, top: -30, width: 60, height: 60, borderRadius: 30, border: `3px solid ${ORANGE}`, scale: String(0.4 + click * 1.2), opacity: click }} />
    <svg width="40" height="40" viewBox="0 0 24 24" style={{ filter: "drop-shadow(0 4px 8px rgba(0,0,0,.6))" }}><path d="M4 2l16 9-7 2-3 7z" fill="#fff" stroke="#111" strokeWidth="1.2" /></svg>
  </div>
);

// 16:9 clip that covers a box of any shape.
const cover = (w: number, h: number): React.CSSProperties => {
  const vw = Math.max(w, (h * 16) / 9);
  const vh = (vw * 9) / 16;
  return { position: "absolute", width: vw, height: vh, left: (w - vw) / 2, top: (h - vh) / 2, maxWidth: "none" };
};

const Preview: React.FC<{ src: string; w: number; h: number; x: number; y: number; children?: React.ReactNode; videoStyle?: React.CSSProperties }> = ({ src, w, h, x, y, children, videoStyle }) => (
  <div style={{ position: "absolute", left: x, top: y, width: w, height: h, borderRadius: 18, overflow: "hidden", background: "#000", border: "1px solid rgba(255,255,255,.12)", boxShadow: "0 30px 80px rgba(0,0,0,.6)" }}>
    <Video src={staticFile(src)} loop muted style={{ ...cover(w, h), ...videoStyle }} />
    {children}
  </div>
);

// Name card: the card builds up line by line over a live picture.
export const NameCard: React.FC = () => {
  const f = useCurrentFrame();
  const show = inOut(f, 15, 35, 150, 170);
  const line = (start: number) => interpolate(f, [start, start + 14], [0, 1], { ...clamp, easing: ease }) * show;
  const name = "Daniel Reyes";
  const typed = Math.round(interpolate(f, [30, 60], [0, name.length], clamp));
  return (
    <Bg>
      <Preview src="business.mp4" x={40} y={40} w={1200} h={640}>
        <div style={{ position: "absolute", left: 40, bottom: 40, padding: "22px 30px 22px 22px", borderRadius: 20, background: "rgba(18,18,22,.8)", border: "1px solid rgba(255,255,255,.12)", backdropFilter: "blur(12px)", opacity: show, translate: `${(1 - show) * -50}px 0px`, minWidth: 520 }}>
          <div style={{ display: "flex", alignItems: "center", gap: 20 }}>
            <div style={{ width: 76, height: 76, borderRadius: 38, background: ORANGE, color: "#fff", display: "flex", alignItems: "center", justifyContent: "center", font: "700 30px Segoe UI, sans-serif", scale: String(0.6 + 0.4 * line(20)) }}>DR</div>
            <div>
              <div style={{ color: "#fff", font: "700 42px Segoe UI, sans-serif" }}>{name.slice(0, typed)}<span style={{ opacity: f < 64 && f % 16 < 8 ? 1 : 0, color: ORANGE }}>|</span></div>
              <div style={{ color: ORANGE, font: "500 26px Segoe UI, sans-serif", opacity: line(62) }}>@danielreyes · he/him</div>
            </div>
          </div>
          <div style={{ display: "flex", justifyContent: "space-between", gap: 40, marginTop: 14, color: "rgba(255,255,255,.8)", font: "400 24px Segoe UI, sans-serif", opacity: line(76) }}>
            <span>daniel@example.com</span><span>Austin, Texas</span>
          </div>
        </div>
      </Preview>
    </Bg>
  );
};

// Scenes: the cursor clicks through the scene strip and the picture changes with it.
const TILES = [225, 380, 535, 690]; // Blank, Just my name, Lower Third, Name Card (centres in the strip image)
export const Scenes: React.FC = () => {
  const f = useCurrentFrame();
  const step = Math.min(3, Math.floor(Math.max(0, f - 20) / 38));
  const idx = f > 170 ? 0 : step;
  const cx = interpolate(f, [0, 20, 58, 96, 134, 170, 180], [900, TILES[0], TILES[1], TILES[2], TILES[3], TILES[3], 900], { ...clamp, easing: ease });
  const local = (f - 20) % 38;
  const click = f > 20 && f < 170 ? interpolate(local, [0, 6, 18], [0, 1, 0], clamp) : 0;
  const s = 1.24;
  return (
    <Bg>
      <Preview src="professional.mp4" x={140} y={30} w={1000} h={500}>
        {idx === 1 && <div style={{ position: "absolute", left: 30, bottom: 30, padding: "10px 18px", borderRadius: 10, background: "rgba(0,0,0,.55)", color: "#fff", font: "700 36px Segoe UI, sans-serif" }}>Amara Okafor</div>}
        {idx === 2 && <div style={{ position: "absolute", left: 30, bottom: 30, padding: "12px 22px", borderLeft: "6px solid #3b82f6", background: "rgba(10,10,14,.8)", color: "#fff" }}><div style={{ font: "700 36px Segoe UI, sans-serif" }}>Amara Okafor</div><div style={{ font: "400 24px Segoe UI, sans-serif", opacity: .8 }}>VP Operations</div></div>}
        {idx === 3 && <div style={{ position: "absolute", left: 30, bottom: 30, display: "flex", gap: 16, alignItems: "center", padding: "16px 24px 16px 16px", borderRadius: 16, background: "rgba(18,18,22,.8)", border: "1px solid rgba(255,255,255,.12)" }}><div style={{ width: 60, height: 60, borderRadius: 30, background: ORANGE, color: "#fff", display: "flex", alignItems: "center", justifyContent: "center", font: "700 24px Segoe UI" }}>AO</div><div><div style={{ color: "#fff", font: "700 32px Segoe UI" }}>Amara Okafor</div><div style={{ color: ORANGE, font: "500 20px Segoe UI" }}>VP Operations · she/her</div></div></div>}
      </Preview>
      <div style={{ position: "absolute", left: (1280 - 1000 * s) / 2, top: 560, width: 1000 * s, height: 128 * s, borderRadius: 16, overflow: "hidden", boxShadow: "0 20px 60px rgba(0,0,0,.6)" }}>
        <Img src={staticFile("f-scenes.jpg")} style={{ width: "100%", height: "100%" }} />
        <LiveTiles src="professional.mp4" tiles={STRIP_TILES} k={s} clipRight={898} />
        <div style={{ position: "absolute", left: (TILES[idx] - 68) * s, top: 6 * s, width: 136 * s, height: 82 * s, borderRadius: 10, border: `3px solid ${ORANGE}`, boxShadow: `0 0 24px ${ORANGE}` }} />
      </div>
      <Cursor x={(1280 - 1000 * s) / 2 + cx * s} y={560 + 50 * s} opacity={inOut(f, 0, 15, 165, 178)} click={click} />
    </Bg>
  );
};

// Smart framing: the frame follows and zooms, the mini map shows the crop.
export const Framing: React.FC = () => {
  const f = useCurrentFrame();
  const z = interpolate(f, [0, 40, 90, 130, 180], [1, 1.45, 1.45, 1.2, 1], { ...clamp, easing: ease });
  const px = interpolate(f, [0, 40, 90, 130, 180], [0, -6, 6, 0, 0], { ...clamp, easing: ease });
  return (
    <Bg>
      <Preview src="creative.mp4" x={40} y={40} w={860} h={640} videoStyle={{ scale: String(z), translate: `${px}% 0%`, transformOrigin: "50% 25%" }}>
        <div style={{ position: "absolute", top: 24, left: 24, padding: "8px 16px", borderRadius: 999, background: "rgba(0,0,0,.6)", color: "#fff", font: "600 22px Segoe UI" }}>Smart framing · {z.toFixed(1)}×</div>
      </Preview>
      <div style={{ position: "absolute", left: 930, top: 40, width: 310, height: 640, borderRadius: 18, background: "#1c1c1f", border: "1px solid rgba(255,255,255,.1)", padding: 22, color: "#fff" }}>
        <div style={{ display: "flex", justifyContent: "space-between", font: "500 20px Segoe UI", opacity: .85 }}><span>Framing</span><span>{z.toFixed(1)}×</span></div>
        <div style={{ position: "relative", marginTop: 14, height: 150, borderRadius: 10, overflow: "hidden", border: "2px solid rgba(255,255,255,.12)" }}>
          <Video src={staticFile("creative.mp4")} loop muted style={{ ...cover(266, 150), opacity: .55 }} />
          <div style={{ position: "absolute", left: `${50 - 50 / z - px / 2}%`, top: `${50 - 50 / z}%`, width: `${100 / z}%`, height: `${100 / z}%`, border: `3px solid ${ORANGE}`, borderRadius: 6 }} />
        </div>
        {["Zoom", "Left · right", "Up · down"].map((l, i) => {
          const v = i === 0 ? (z - 1) / 1 : i === 1 ? 0.5 + px / 20 : 0.5;
          return (
            <div key={l} style={{ marginTop: 34 }}>
              <div style={{ font: "400 18px Segoe UI", opacity: .75 }}>{l}</div>
              <div style={{ position: "relative", marginTop: 14, height: 4, borderRadius: 2, background: "rgba(255,255,255,.2)" }}>
                <div style={{ position: "absolute", left: 0, width: `${v * 100}%`, height: 4, borderRadius: 2, background: ORANGE }} />
                <div style={{ position: "absolute", left: `calc(${v * 100}% - 9px)`, top: -7, width: 18, height: 18, borderRadius: 9, background: "#fff", border: `4px solid ${ORANGE}` }} />
              </div>
            </div>
          );
        })}
        <div style={{ display: "flex", justifyContent: "space-around", marginTop: 44, font: "500 20px Segoe UI" }}>
          <span style={{ opacity: .6 }}>Manual</span><span style={{ opacity: .6 }}>Auto</span><span style={{ borderBottom: `3px solid ${ORANGE}`, paddingBottom: 4 }}>Smart</span>
        </div>
      </div>
    </Bg>
  );
};

// Simple mode: one big Record button, then a recording timer.
export const Simple: React.FC = () => {
  const f = useCurrentFrame();
  const click = interpolate(f, [38, 44, 58], [0, 1, 0], clamp);
  const rec = inOut(f, 44, 52, 160, 170);
  const secs = Math.max(0, Math.floor((f - 44) / FPS));
  const s = 1.2;
  return (
    <Bg>
      <Preview src="education.mp4" x={190} y={24} w={900} h={506}>
        <div style={{ position: "absolute", top: 20, left: 20, display: "flex", alignItems: "center", gap: 10, padding: "8px 16px", borderRadius: 999, background: "rgba(0,0,0,.65)", color: "#fff", font: "700 22px Segoe UI", opacity: rec }}>
          <span style={{ width: 14, height: 14, borderRadius: 7, background: "#ef4444" }} />REC 00:0{secs}
        </div>
      </Preview>
      <div style={{ position: "absolute", left: (1280 - 1000 * s) / 2, top: 548, width: 1000 * s, height: 149 * s, borderRadius: 16, overflow: "hidden", boxShadow: "0 20px 60px rgba(0,0,0,.6)" }}>
        <Img src={staticFile("f-simple.jpg")} style={{ width: "100%", height: "100%" }} />
        <div style={{ position: "absolute", left: 412 * s, top: 14 * s, width: 176 * s, height: 116 * s, borderRadius: 10, border: "3px solid #ef4444", opacity: rec, boxShadow: "0 0 28px rgba(239,68,68,.8)" }} />
      </div>
      <Cursor x={interpolate(f, [0, 36], [1000, (1280 - 1000 * s) / 2 + 500 * s], { ...clamp, easing: ease })} y={interpolate(f, [0, 36], [420, 548 + 70 * s], { ...clamp, easing: ease })} opacity={inOut(f, 0, 12, 70, 90)} click={click} />
    </Bg>
  );
};

export const FeatureCompositions = () => (
  <>
    <Composition id="FNameCard" component={NameCard} durationInFrames={LEN} fps={FPS} width={1280} height={720} />
    <Composition id="FScenes" component={Scenes} durationInFrames={LEN} fps={FPS} width={1280} height={720} />
    <Composition id="FFraming" component={Framing} durationInFrames={LEN} fps={FPS} width={1280} height={720} />
    <Composition id="FSimple" component={Simple} durationInFrames={LEN} fps={FPS} width={1280} height={720} />
  </>
);
