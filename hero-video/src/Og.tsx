import React from "react";
import { AbsoluteFill, Composition, Img, staticFile } from "remotion";

// 1200x630 share card for WhatsApp, X, LinkedIn and others.
export const OgCard: React.FC = () => (
  <AbsoluteFill style={{ background: "#07070a", overflow: "hidden", fontFamily: "Segoe UI, Inter, sans-serif" }}>
    <div style={{ position: "absolute", width: 900, height: 900, borderRadius: "50%", left: -200, top: -420, background: "radial-gradient(circle, rgba(255,138,61,.45), rgba(255,138,61,0) 62%)", filter: "blur(20px)" }} />
    <div style={{ position: "absolute", width: 800, height: 800, borderRadius: "50%", right: -260, bottom: -380, background: "radial-gradient(circle, rgba(124,92,255,.4), rgba(124,92,255,0) 62%)", filter: "blur(20px)" }} />
    <div style={{ position: "absolute", right: -80, top: 120, width: 700, rotate: "-4deg", borderRadius: 18, overflow: "hidden", border: "1px solid rgba(255,255,255,.18)", boxShadow: "0 40px 100px rgba(0,0,0,.7)" }}>
      <Img src={staticFile("studio.jpg")} style={{ width: 700, display: "block" }} />
    </div>
    <div style={{ position: "absolute", left: 64, top: 64, display: "flex", alignItems: "center", gap: 16 }}>
      <Img src={staticFile("kissik-icon-512.png")} style={{ width: 64, height: 64, borderRadius: 14 }} />
      <span style={{ color: "#fff", font: "700 38px Segoe UI, sans-serif" }}>KISSIK</span>
    </div>
    <div style={{ position: "absolute", left: 64, top: 200, color: "#fff", font: "800 76px/0.98 Segoe UI, sans-serif", letterSpacing: "-0.03em" }}>
      Any camera.<br />Studio look.<br />
      <span style={{ background: "linear-gradient(90deg,#ff8a3d,#ff4d8d)", WebkitBackgroundClip: "text", backgroundClip: "text", color: "transparent" }}>Free.</span>
    </div>
    <div style={{ position: "absolute", left: 64, bottom: 64, color: "rgba(255,255,255,.75)", font: "500 26px Segoe UI, sans-serif" }}>
      Webcam or Android phone · Windows 11 · Open source
    </div>
  </AbsoluteFill>
);

export const OgComposition = () => (
  <Composition id="Og" component={OgCard} durationInFrames={1} fps={30} width={1200} height={630} />
);
