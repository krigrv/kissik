import { Video } from "@remotion/media";
import React from "react";
import { AbsoluteFill, Audio, Composition, Easing, Img, interpolate, Sequence, spring, staticFile, useCurrentFrame, useVideoConfig } from "remotion";
import { MyComponent } from "./Composition";
import { LiveBox, STUDIO_FRAMING } from "./LiveThumbs";
import { Pairing } from "./Pairing";

// A 31 s story: Amara has a board call in two minutes, her laptop camera looks bad, KISSIK fixes it in three clicks.
// Music: "Calm Resolve" (Suno), 100 BPM, beats every 18 frames.
const FPS = 30;
const ORANGE = "#ff8a3d";
const GREEN = "#3ddc84";
const FONT = "Segoe UI, Inter, sans-serif";
const clamp = { extrapolateLeft: "clamp", extrapolateRight: "clamp" } as const;
const ease = Easing.bezier(0.16, 1, 0.3, 1);

// S2 the three-click fix, S3 the phone bonus, S3b the name card, S4 the meeting, S5 the end card.
const S1 = 0, S2 = 126, S3 = 414, S3b = 534, S4 = 660, S5 = 858, LEN = 966;
// Music lift (21.7 s into the track) lands on the first click, S2 + 30 frames.
const MUSIC_START = Math.round((21.7 - (S2 + 30) / FPS) * FPS);

export type Layout = { W: number; H: number; vertical: boolean };
const VTOP = 600;
const VH = 1116;

// Places a 1920x1080 stage in the frame. Vertical crops around fx (0 to 1 across the stage).
const Stage: React.FC<{ L: Layout; fx?: number; children: React.ReactNode }> = ({ L, fx = 0.5, children }) => {
  const k = L.vertical ? VH / 1080 : L.W / 1920;
  const left = L.vertical ? Math.min(0, Math.max(L.W - 1920 * k, L.W / 2 - 1920 * k * fx)) : 0;
  return (
    <div style={{ position: "absolute", left, top: L.vertical ? VTOP : 0, width: 1920, height: 1080, scale: String(k), transformOrigin: "0 0", overflow: "hidden" }}>
      {children}
    </div>
  );
};

// Fits a 1280x720 clip to the 1920x1080 stage.
const Up: React.FC<{ children: React.ReactNode }> = ({ children }) => (
  <div style={{ position: "absolute", width: 1280, height: 720, scale: "1.5", transformOrigin: "0 0" }}>{children}</div>
);

const Fade: React.FC<{ len: number; children: React.ReactNode }> = ({ len, children }) => {
  const f = useCurrentFrame();
  return <AbsoluteFill style={{ opacity: interpolate(f, [0, 12, len - 12, len], [0, 1, 1, 0], clamp) }}>{children}</AbsoluteFill>;
};

// Narration line: fades up, holds, fades out.
const Line: React.FC<{ L: Layout; text: React.ReactNode; len: number }> = ({ L, text, len }) => {
  const f = useCurrentFrame();
  const o = interpolate(f, [0, 10, len - 10, len], [0, 1, 1, 0], clamp);
  const y = interpolate(f, [0, 14], [14, 0], { ...clamp, easing: ease });
  const v = L.vertical;
  return (
    <div style={{ position: "absolute", left: v ? 60 : 90, right: v ? 60 : 500, top: v ? 300 : undefined, bottom: v ? undefined : 80, opacity: o, translate: `0px ${y}px`, color: "#fff", font: `700 ${v ? 64 : 58}px ${FONT}`, lineHeight: 1.2, letterSpacing: "-.01em", textAlign: v ? "center" : "left", textShadow: "0 6px 30px rgba(0,0,0,.8)" }}>
      {text}
    </div>
  );
};

const Shade: React.FC<{ L: Layout }> = ({ L }) =>
  L.vertical ? null : <AbsoluteFill style={{ background: "linear-gradient(0deg, rgba(0,0,0,.7), transparent 40%)" }} />;

// 1. The problem: a call reminder over her dim laptop camera.
const Problem: React.FC<{ L: Layout }> = ({ L }) => {
  const f = useCurrentFrame();
  const { fps } = useVideoConfig();
  const toast = spring({ frame: f - 8, fps, config: { damping: 16 } });
  return (
    <Fade len={S2 - S1}>
      <Stage L={L} fx={0.38}>
        <Video src={staticFile("amara-dim.mp4")} muted style={{ width: 1920, height: 1080, objectFit: "cover" }} />
        <div style={{ position: "absolute", left: 40, top: 40, padding: "8px 18px", borderRadius: 999, background: "rgba(0,0,0,.55)", color: "#ddd", font: `600 30px ${FONT}` }}>Laptop camera</div>
        <div style={{ position: "absolute", right: 40, top: 40, width: 520, padding: "22px 26px", borderRadius: 20, background: "rgba(28,28,32,.92)", border: "1px solid rgba(255,255,255,.12)", boxShadow: "0 20px 60px rgba(0,0,0,.5)", fontFamily: FONT, translate: `${(1 - toast) * 600}px 0px`, opacity: toast }}>
          <div style={{ color: "#aaa", font: `600 22px ${FONT}` }}>Calendar · now</div>
          <div style={{ color: "#fff", font: `700 32px ${FONT}`, marginTop: 6 }}>Board call starts in 2 min</div>
        </div>
      </Stage>
      <Shade L={L} />
      <Sequence from={24} durationInFrames={50} layout="none"><Line L={L} len={50} text="Two minutes to the board call." /></Sequence>
      <Sequence from={74} durationInFrames={52} layout="none"><Line L={L} len={52} text="And her laptop camera looks like this." /></Sequence>
    </Fade>
  );
};

// 2. The fix: KISSIK on the same laptop camera. Three clicks, each one improves the live picture.
// Studio window coordinates are studio.jpg pixels (1600 wide), shown 1500 wide in the middle of the stage.
const WK = 1500 / 1600;
const WX = 210, WY = 170;
const PV = { x: 358, y: 78, w: 969, h: 558 };
const CLICKS = [
  { at: 30, x: 1115, y: 30, name: "Screen light", note: "Your monitor becomes a soft key light" },
  { at: 102, x: 1532, y: 465, name: "Smart framing", note: "Centres you and moves in close" },
  { at: 174, x: 137, y: 300, name: "Background blur", note: "The room softens, you stay sharp" },
];
const toStage = (x: number, y: number) => ({ x: WX + x * WK, y: WY + y * WK });

const FixPicture: React.FC<{ l: number; fr: number; b: number }> = ({ l, fr, b }) => {
  const look = `brightness(${1 + 0.38 * l}) saturate(${1 + 0.22 * l}) contrast(${1 + 0.06 * l})`;
  const frame: React.CSSProperties = { position: "absolute", inset: 0, scale: String(1 + 0.24 * fr), transformOrigin: "37% 34%" };
  const vid: React.CSSProperties = { width: "100%", height: "100%", objectFit: "cover" };
  return (
    <div style={{ position: "absolute", inset: 0, overflow: "hidden", background: "#000", filter: look }}>
      <div style={frame}>
        <Video src={staticFile("amara-fix.mp4")} muted style={{ ...vid, filter: `blur(${26 * b}px) brightness(${1 - 0.18 * b})` }} />
        <div style={{ position: "absolute", inset: 0, WebkitMaskImage: "radial-gradient(ellipse 27% 62% at 36% 55%, #000 62%, transparent 100%)", maskImage: "radial-gradient(ellipse 27% 62% at 36% 55%, #000 62%, transparent 100%)", opacity: b }}>
          <Video src={staticFile("amara-fix.mp4")} muted style={vid} />
        </div>
      </div>
      <div style={{ position: "absolute", inset: 0, background: "radial-gradient(45% 70% at 34% 36%, rgba(255,236,215,.55), transparent 75%)", mixBlendMode: "soft-light", opacity: l }} />
    </div>
  );
};

export const Fix: React.FC<{ L: Layout; bare?: boolean }> = ({ L, bare }) => {
  const f = useCurrentFrame();
  const { fps } = useVideoConfig();
  const len = S3 - S2;
  const prog = (at: number) => interpolate(f, [at + 4, at + 22], [0, 1], { ...clamp, easing: ease });
  const [l, fr, b] = CLICKS.map((c) => prog(c.at));
  const open = spring({ frame: f, fps, config: { damping: 18 } });

  // Virtual camera: drifts to each button just before its click, back to the picture as it changes.
  const pv = toStage(PV.x + PV.w / 2, PV.y + PV.h / 2);
  const keys: number[] = [0];
  const fxs: number[] = [pv.x];
  const fys: number[] = [pv.y];
  const zs: number[] = [1];
  CLICKS.forEach((c) => {
    const p = toStage(c.x, c.y);
    keys.push(c.at - 16, c.at + 4, c.at + 34);
    fxs.push(p.x, p.x, pv.x);
    fys.push(p.y, p.y, pv.y);
    zs.push(1.28, 1.28, 1.1);
  });
  keys.push(len);
  fxs.push(pv.x);
  fys.push(pv.y);
  zs.push(1.04);
  const camX = interpolate(f, keys, fxs, { ...clamp, easing: ease });
  const camY = interpolate(f, keys, fys, { ...clamp, easing: ease });
  const camZ = interpolate(f, keys, zs, { ...clamp, easing: ease });

  // Cursor glides from button to button.
  const ck = [0, ...CLICKS.flatMap((c) => [c.at - 18, c.at])];
  const cxs = [toStage(1300, 400).x, ...CLICKS.flatMap((c) => [toStage(c.x, c.y).x, toStage(c.x, c.y).x])];
  const cys = [toStage(1300, 400).y, ...CLICKS.flatMap((c) => [toStage(c.x, c.y).y, toStage(c.x, c.y).y])];
  const curX = interpolate(f, ck, cxs, { ...clamp, easing: ease });
  const curY = interpolate(f, ck, cys, { ...clamp, easing: ease });

  return (
    <Fade len={len}>
      <Stage L={L} fx={camX / 1920}>
        <AbsoluteFill style={{ background: "radial-gradient(1000px 600px at 50% 0%, rgba(255,138,61,.18), transparent 70%), #08080b" }} />
        <div style={{ position: "absolute", inset: 0, scale: String(camZ), transformOrigin: `${camX}px ${camY}px`, translate: `${(960 - camX) * (camZ - 1) * 0.6}px ${(540 - camY) * (camZ - 1) * 0.6}px` }}>
          <div style={{ position: "absolute", left: WX, top: WY, width: 1500, height: 741, borderRadius: 22, overflow: "hidden", boxShadow: "0 50px 140px rgba(0,0,0,.7), 0 0 0 1px rgba(255,255,255,.1)", scale: String(0.94 + 0.06 * open), opacity: open }}>
            <Img src={staticFile("studio.jpg")} style={{ width: 1500, height: 741 }} />
            <div style={{ position: "absolute", left: PV.x * WK, top: PV.y * WK, width: PV.w * WK, height: PV.h * WK }}>
              <FixPicture l={l} fr={fr} b={b} />
              <div style={{ position: "absolute", left: 20, top: 20, display: "flex", flexDirection: "column", gap: 10 }}>
                {CLICKS.map((c, i) => {
                  const on = [l, fr, b][i];
                  return (
                    <div key={c.name} style={{ display: "flex", alignItems: "center", gap: 10, padding: "8px 16px 8px 10px", borderRadius: 999, background: "rgba(14,14,18,.8)", color: "#fff", font: `700 22px ${FONT}`, opacity: on, translate: `${(1 - on) * -30}px 0px` }}>
                      <span style={{ width: 26, height: 26, borderRadius: 13, background: ORANGE, display: "flex", alignItems: "center", justifyContent: "center", font: `900 16px ${FONT}` }}>✓</span>
                      {c.name}
                    </div>
                  );
                })}
              </div>
            </div>
            <LiveBox src="amara-fix.mp4" r={STUDIO_FRAMING} k={WK} zoom={1 + 0.24 * fr} />
            {CLICKS.map((c, i) => {
              const on = [l, fr, b][i];
              const ring = interpolate(f, [c.at, c.at + 16], [0, 1], clamp);
              return (
                <React.Fragment key={c.name}>
                  <div style={{ position: "absolute", left: c.x * WK - 60, top: c.y * WK - 26, width: 120, height: 52, borderRadius: 12, border: `3px solid ${ORANGE}`, boxShadow: `0 0 26px ${ORANGE}`, opacity: on }} />
                  <div style={{ position: "absolute", left: c.x * WK - 40, top: c.y * WK - 40, width: 80, height: 80, borderRadius: 40, border: `3px solid ${ORANGE}`, scale: String(0.3 + ring * 1.4), opacity: ring > 0 && ring < 1 ? 1 - ring : 0 }} />
                </React.Fragment>
              );
            })}
          </div>
          <svg width="44" height="44" viewBox="0 0 24 24" style={{ position: "absolute", left: curX - 6, top: curY - 4, filter: "drop-shadow(0 4px 8px rgba(0,0,0,.6))", opacity: interpolate(f, [8, 18, len - 40, len - 26], [0, 1, 1, 0], clamp) }}>
            <path d="M4 2l16 9-7 2-3 7z" fill="#fff" stroke="#111" strokeWidth="1.2" />
          </svg>
        </div>
      </Stage>
      {!bare && <>
      <Shade L={L} />
      <Sequence from={0} durationInFrames={30} layout="none"><Line L={L} len={30} text={<>She opens <span style={{ color: ORANGE }}>KISSIK.</span></>} /></Sequence>
      {CLICKS.map((c, i) => (
        <Sequence key={c.name} from={c.at} durationInFrames={62} layout="none">
          <Line L={L} len={62} text={<><span style={{ color: ORANGE }}>{i + 1}.</span> {c.name}. <span style={{ color: "#cfcfd6", fontWeight: 500 }}>{c.note}.</span></>} />
        </Sequence>
      ))}
      <Sequence from={238} durationInFrames={len - 238} layout="none"><Line L={L} len={len - 238} text={<>Same laptop camera. <span style={{ color: ORANGE }}>Three clicks.</span></>} /></Sequence>
      </>}
    </Fade>
  );
};

// 2b. The bonus: her phone as an even better camera.
const Bonus: React.FC<{ L: Layout }> = ({ L }) => (
  <Fade len={S3b - S3}>
    <Stage L={L} fx={0.62}>
      <Up><Sequence from={-92} layout="none"><Pairing src="professional.mp4" phoneFocus={0.36} overlays /></Sequence></Up>
    </Stage>
    <Shade L={L} />
    <Sequence from={6} durationInFrames={S3b - S3 - 6} layout="none"><Line L={L} len={S3b - S3 - 6} text={<>Want it even sharper? Her <span style={{ color: ORANGE }}>phone</span> becomes the camera.</>} /></Sequence>
  </Fade>
);

// 3. Getting ready: one click puts her name on screen.
const Ready: React.FC<{ L: Layout }> = ({ L }) => (
  <Fade len={S4 - S3b}>
    <Stage L={L} fx={0.45}><MyComponent /></Stage>
    <Shade L={L} />
    <Sequence from={36} durationInFrames={84} layout="none"><Line L={L} len={84} text={<>One click adds her <span style={{ color: ORANGE }}>name card.</span></>} /></Sequence>
  </Fade>
);

// 4. The payoff: the meeting, where her tile stands out.
const Tile: React.FC<{ src: string; name: string; x: number; y: number; dim?: boolean; speaking?: boolean }> = ({ src, name, x, y, dim, speaking }) => (
  <div style={{ position: "absolute", left: x, top: y, width: 680, height: 382, borderRadius: 16, overflow: "hidden", background: "#000", outline: speaking ? `4px solid ${GREEN}` : "1px solid rgba(255,255,255,.08)", outlineOffset: speaking ? -4 : 0 }}>
    <Video src={staticFile(src)} loop muted style={{ width: "100%", height: "100%", objectFit: "cover", filter: dim ? "brightness(.72) saturate(.7) contrast(.9) blur(.6px)" : undefined }} />
    <div style={{ position: "absolute", left: 14, bottom: 14, padding: "6px 14px", borderRadius: 8, background: "rgba(0,0,0,.6)", color: "#fff", font: `600 24px ${FONT}` }}>{name}</div>
  </div>
);

const Bubble: React.FC<{ from: string; text: string; at: number; y: number }> = ({ from, text, at, y }) => {
  const f = useCurrentFrame();
  const { fps } = useVideoConfig();
  const p = spring({ frame: f - at, fps, config: { damping: 15 } });
  return (
    <div style={{ position: "absolute", left: 720, top: y, padding: "16px 22px", borderRadius: "20px 20px 20px 6px", background: "#fff", color: "#111", fontFamily: FONT, boxShadow: "0 16px 40px rgba(0,0,0,.45)", opacity: p, scale: String(0.85 + 0.15 * p), transformOrigin: "0 100%" }}>
      <div style={{ font: `700 22px ${FONT}`, color: "#666" }}>{from}</div>
      <div style={{ font: `700 34px ${FONT}` }}>{text}</div>
    </div>
  );
};

const Meeting: React.FC<{ L: Layout }> = ({ L }) => {
  const f = useCurrentFrame();
  const z = interpolate(f, [0, S5 - S4], [1, 1.04]);
  return (
    <Fade len={S5 - S4}>
      <Stage L={L} fx={0.3}>
        <AbsoluteFill style={{ background: "#0e0e11", scale: String(z), transformOrigin: "40% 40%" }}>
          <div style={{ position: "absolute", left: 40, top: 30, color: "#cfcfd6", font: `600 28px ${FONT}` }}>Board meeting · Q3 review</div>
          <div style={{ position: "absolute", right: 40, top: 30, color: "#cfcfd6", font: `600 28px ${FONT}` }}>4 people</div>
          <Tile src="professional.mp4" name="Amara Okafor" x={260} y={90} speaking />
          <Tile src="podcast.mp4" name="Omar" x={980} y={90} dim />
          <Tile src="business.mp4" name="Daniel" x={260} y={492} dim />
          <Tile src="education.mp4" name="Mei" x={980} y={492} dim />
          <div style={{ position: "absolute", left: 760, top: 950, display: "flex", gap: 22 }}>
            {["#2a2a30", "#2a2a30", "#2a2a30", "#e5484d"].map((c, i) => <div key={i} style={{ width: 84, height: 84, borderRadius: 42, background: c }} />)}
          </div>
          <Bubble from="Omar" text="Amara, new camera?" at={60} y={290} />
          <Bubble from="Mei" text="You look so clear!" at={110} y={400} />
        </AbsoluteFill>
      </Stage>
      <Shade L={L} />
      <Sequence from={8} durationInFrames={184} layout="none"><Line L={L} len={184} text={<>She joins. <span style={{ color: ORANGE }}>Everyone notices.</span></>} /></Sequence>
    </Fade>
  );
};

const End: React.FC<{ L: Layout }> = ({ L }) => {
  const f = useCurrentFrame();
  const { fps } = useVideoConfig();
  const p = spring({ frame: f, fps, config: { damping: 18 } });
  const b = interpolate(f, [20, 36], [0, 1], { ...clamp, easing: ease });
  const v = L.vertical;
  return (
    <Fade len={LEN - S5}>
      <AbsoluteFill style={{ alignItems: "center", justifyContent: "center", flexDirection: "column", gap: v ? 40 : 28, fontFamily: FONT, background: "radial-gradient(900px 600px at 50% 35%, rgba(255,138,61,.22), transparent 70%), #07070a" }}>
        <div style={{ display: "flex", alignItems: "center", gap: 26, opacity: p, scale: String(0.92 + 0.08 * p) }}>
          <Img src={staticFile("kissik-icon-512.png")} style={{ width: 120, height: 120, borderRadius: 28 }} />
          <div style={{ color: "#fff", font: `800 116px ${FONT}`, letterSpacing: "-.02em" }}>KISSIK</div>
        </div>
        <div style={{ color: "#e8e8ee", font: `600 ${v ? 50 : 46}px ${FONT}`, opacity: b, textAlign: "center", padding: "0 60px" }}>Make your video look its best.</div>
        <div style={{ color: "#a9a9b3", font: `500 ${v ? 38 : 34}px ${FONT}`, opacity: b }}>Free · No watermark · Open source</div>
        <div style={{ marginTop: 10, padding: "18px 40px", borderRadius: 999, background: ORANGE, color: "#fff", font: `700 ${v ? 46 : 42}px ${FONT}`, opacity: b }}>kissik-studio.vercel.app</div>
      </AbsoluteFill>
    </Fade>
  );
};

const Story: React.FC<{ L: Layout }> = ({ L }) => (
  <AbsoluteFill style={{ background: "#07070a" }}>
    <Audio src={staticFile("story-music.mp3")} trimBefore={MUSIC_START} volume={(f) => interpolate(f, [0, 8, LEN - 36, LEN], [0, 0.9, 0.9, 0], clamp)} />
    <Sequence from={S1} durationInFrames={S2 - S1}><Problem L={L} /></Sequence>
    <Sequence from={S2} durationInFrames={S3 - S2}><Fix L={L} /></Sequence>
    <Sequence from={S3} durationInFrames={S3b - S3}><Bonus L={L} /></Sequence>
    <Sequence from={S3b} durationInFrames={S4 - S3b}><Ready L={L} /></Sequence>
    <Sequence from={S4} durationInFrames={S5 - S4}><Meeting L={L} /></Sequence>
    <Sequence from={S5} durationInFrames={LEN - S5}><End L={L} /></Sequence>
  </AbsoluteFill>
);

export const StoryCompositions = () => (
  <>
    <Composition id="StoryWide" component={Story} durationInFrames={LEN} fps={FPS} width={1920} height={1080} defaultProps={{ L: { W: 1920, H: 1080, vertical: false } }} />
    <Composition id="StoryVertical" component={Story} durationInFrames={LEN} fps={FPS} width={1080} height={1920} defaultProps={{ L: { W: 1080, H: 1920, vertical: true } }} />
  </>
);
