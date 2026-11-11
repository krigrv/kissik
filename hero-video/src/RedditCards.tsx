import React from "react";
import { AbsoluteFill, Composition, Img, staticFile } from "remotion";

// Still images for Reddit galleries: a title card with the app, and a before/after of the same laptop camera.
const ORANGE = "#ff8a3d";
const FONT = "Segoe UI, Inter, sans-serif";

const Bg: React.FC = () => (
  <AbsoluteFill style={{ background: "radial-gradient(1100px 700px at 15% 0%, rgba(255,138,61,.28), transparent 65%), radial-gradient(1000px 700px at 100% 100%, rgba(124,92,255,.22), transparent 65%), #08080b" }} />
);

const Chip: React.FC<{ children: React.ReactNode }> = ({ children }) => (
  <div style={{ padding: "12px 22px", borderRadius: 999, background: "rgba(255,255,255,.08)", border: "1px solid rgba(255,255,255,.14)", color: "#fff", font: `600 28px ${FONT}` }}>{children}</div>
);

const TitleCard: React.FC = () => (
  <AbsoluteFill style={{ fontFamily: FONT }}>
    <Bg />
    <div style={{ position: "absolute", left: 90, top: 110, width: 700 }}>
      <div style={{ display: "flex", alignItems: "center", gap: 22 }}>
        <Img src={staticFile("kissik-icon-512.png")} style={{ width: 96, height: 96, borderRadius: 22 }} />
        <div style={{ color: "#fff", font: `800 92px ${FONT}`, letterSpacing: "-.02em" }}>KISSIK</div>
      </div>
      <div style={{ color: ORANGE, font: `700 40px ${FONT}`, marginTop: 10 }}>Camera Studio</div>
      <div style={{ color: "#fff", font: `700 56px ${FONT}`, lineHeight: 1.15, marginTop: 40, letterSpacing: "-.01em" }}>A free, open-source camera studio for Windows.</div>
      <div style={{ display: "flex", flexWrap: "wrap", gap: 14, marginTop: 44 }}>
        {["Background blur", "Screen light", "Smart framing", "Name cards", "Recording", "Phone as webcam"].map((t) => <Chip key={t}>{t}</Chip>)}
      </div>
      <div style={{ color: "#bdbdc6", font: `600 32px ${FONT}`, marginTop: 50 }}>No account · No watermark · Open source</div>
    </div>
    <div style={{ position: "absolute", left: 830, top: 170, width: 1240, height: 613, borderRadius: 22, overflow: "hidden", boxShadow: "0 50px 140px rgba(0,0,0,.7), 0 0 0 1px rgba(255,255,255,.12)" }}>
      <Img src={staticFile("studio.jpg")} style={{ width: "100%", height: "100%" }} />
    </div>
    <div style={{ position: "absolute", left: 830, top: 830, color: "#8d8d97", font: `500 28px ${FONT}` }}>Works in Zoom, Teams, Meet, Discord and OBS · kissik-studio.vercel.app</div>
  </AbsoluteFill>
);

const BeforeAfter: React.FC = () => (
  <AbsoluteFill style={{ fontFamily: FONT }}>
    <Bg />
    <div style={{ position: "absolute", left: 0, right: 0, top: 70, textAlign: "center", color: "#fff", font: `800 64px ${FONT}`, letterSpacing: "-.02em" }}>
      Same laptop camera. <span style={{ color: ORANGE }}>Three clicks.</span>
    </div>
    {[
      { src: "amara-dim.png", label: "Laptop webcam", x: 80, chip: "rgba(0,0,0,.65)" },
      { src: "amara-kissik.png", label: "With KISSIK", x: 1000, chip: ORANGE },
    ].map((p) => (
      <div key={p.label} style={{ position: "absolute", left: p.x, top: 210, width: 840, height: 472, borderRadius: 20, overflow: "hidden", boxShadow: "0 40px 120px rgba(0,0,0,.6), 0 0 0 1px rgba(255,255,255,.12)" }}>
        <Img src={staticFile(p.src)} style={{ width: "100%", height: "100%", objectFit: "cover" }} />
        <div style={{ position: "absolute", left: 22, top: 22, padding: "10px 20px", borderRadius: 999, background: p.chip, color: "#fff", font: `700 30px ${FONT}` }}>{p.label}</div>
      </div>
    ))}
    <div style={{ position: "absolute", left: 0, right: 0, top: 740, display: "flex", justifyContent: "center", gap: 18 }}>
      <Chip>1. Screen light</Chip><Chip>2. Smart framing</Chip><Chip>3. Background blur</Chip>
    </div>
    <div style={{ position: "absolute", left: 0, right: 0, top: 860, display: "flex", justifyContent: "center", alignItems: "center", gap: 18 }}>
      <Img src={staticFile("kissik-icon-512.png")} style={{ width: 64, height: 64, borderRadius: 15 }} />
      <div style={{ color: "#fff", font: `800 48px ${FONT}` }}>KISSIK - Camera Studio</div>
      <div style={{ color: "#a9a9b3", font: `500 32px ${FONT}`, marginLeft: 12 }}>Free for Windows · kissik-studio.vercel.app</div>
    </div>
  </AbsoluteFill>
);

export const RedditCardCompositions = () => (
  <>
    <Composition id="RedditTitle" component={TitleCard} durationInFrames={1} fps={30} width={2160} height={1000} />
    <Composition id="RedditBeforeAfter" component={BeforeAfter} durationInFrames={1} fps={30} width={1920} height={1000} />
  </>
);
