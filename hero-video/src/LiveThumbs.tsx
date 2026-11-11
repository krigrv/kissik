import { Video } from "@remotion/media";
import React from "react";
import { staticFile } from "remotion";

// The app's scene strip and framing box show the live camera, so these lay moving video over the
// still thumbnails baked into the screenshots. Rects are in the screenshot's own pixels; k scales them.
type Rect = { x: number; y: number; w: number; h: number };

const NAME = "Amara Okafor";

const Overlay: React.FC<{ i: number; u: number }> = ({ i, u }) => {
  const box: React.CSSProperties = { position: "absolute", left: 3 * u, bottom: 3 * u, color: "#fff", fontFamily: "Segoe UI, sans-serif", whiteSpace: "nowrap" };
  if (i === 1) return <div style={{ ...box, padding: `${1 * u}px ${3 * u}px`, borderRadius: 2 * u, background: "rgba(0,0,0,.6)", fontSize: 6 * u, fontWeight: 700 }}>{NAME}</div>;
  if (i === 2) return <div style={{ ...box, padding: `${1.5 * u}px ${3 * u}px`, borderLeft: `${1.2 * u}px solid #3b82f6`, background: "rgba(10,10,14,.82)", fontSize: 5.5 * u, fontWeight: 700 }}>{NAME}<div style={{ fontSize: 4 * u, fontWeight: 400, opacity: .8 }}>VP Operations</div></div>;
  if (i === 3) return <div style={{ ...box, display: "flex", gap: 2 * u, alignItems: "center", padding: `${1.5 * u}px ${3 * u}px ${1.5 * u}px ${1.5 * u}px`, borderRadius: 3 * u, background: "rgba(18,18,22,.85)" }}><div style={{ width: 7 * u, height: 7 * u, borderRadius: 4 * u, background: "#ff8a3d" }} /><div style={{ fontSize: 5 * u, fontWeight: 700 }}>{NAME}</div></div>;
  return null;
};

export const LiveTiles: React.FC<{ src: string; tiles: Rect[]; k: number; clipRight?: number; overlays?: boolean }> = ({ src, tiles, k, clipRight, overlays = true }) => (
  <>
    {tiles.map((t, i) => {
      const w = clipRight ? Math.min(t.w, clipRight - t.x) : t.w;
      const u = (t.h / 70) * k;
      return (
        <div key={i} style={{ position: "absolute", left: t.x * k, top: t.y * k, width: w * k, height: t.h * k, borderRadius: 8 * k, overflow: "hidden", background: "#000" }}>
          <div style={{ position: "absolute", left: 0, top: 0, width: t.w * k, height: t.h * k }}>
            <Video src={staticFile(src)} loop muted style={{ width: "100%", height: "100%", objectFit: "cover" }} />
            {overlays && <Overlay i={i} u={u} />}
            <div style={{ position: "absolute", right: 3 * u, top: 3 * u, padding: `${0.6 * u}px ${2.4 * u}px`, borderRadius: 2 * u, background: "rgba(20,20,24,.75)", color: "#fff", font: `600 ${5.5 * u}px Segoe UI, sans-serif` }}>Ctrl {i + 1}</div>
          </div>
        </div>
      );
    })}
  </>
);

export const LiveBox: React.FC<{ src: string; r: Rect; k: number; zoom?: number }> = ({ src, r, k, zoom = 1 }) => (
  <div style={{ position: "absolute", left: r.x * k, top: r.y * k, width: r.w * k, height: r.h * k, borderRadius: 6 * k, overflow: "hidden", background: "#000" }}>
    <Video src={staticFile(src)} loop muted style={{ width: "100%", height: "100%", objectFit: "cover", scale: String(zoom) }} />
  </div>
);

// Positions in studio.jpg (1600x791) and f-scenes.jpg (1000x128).
export const STUDIO_TILES: Rect[] = [512, 662, 812, 962, 1112].map((x) => ({ x, y: 650, w: 130, h: 65 }));
export const STUDIO_FRAMING: Rect = { x: 1356, y: 109, w: 218, h: 112 };
export const STRIP_TILES: Rect[] = [159, 314, 469, 624, 779].map((x) => ({ x, y: 10, w: 134, h: 70 }));
