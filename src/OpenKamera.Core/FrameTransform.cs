namespace OpenKamera.Core;

/// <summary>Framing applied to every frame: mirror, 90° rotation, digital zoom and pan.</summary>
/// <param name="Rotation">0, 90, 180 or 270 degrees clockwise.</param>
/// <param name="Zoom">1 = full frame; 2 = half-width crop scaled back up.</param>
/// <param name="PanX">−1 (left edge) .. 1 (right edge) within the zoomed area.</param>
/// <param name="PanY">−1 (top) .. 1 (bottom).</param>
public sealed record FrameTransform(bool Mirror = false, int Rotation = 0, double Zoom = 1.0, double PanX = 0, double PanY = 0)
{
    public static readonly FrameTransform Identity = new();
    public bool IsIdentity => !Mirror && Rotation % 360 == 0 && Zoom <= 1.0;

    /// <summary>
    /// Maps a point on the transformed picture (0..1 each way) back to the camera's own picture: undoes the rotation,
    /// mirror, zoom and pan, the same way <see cref="Apply"/> samples. Used for tap-to-focus.
    /// </summary>
    public (double U, double V) ToSource(double nx, double ny)
    {
        int rot = ((Rotation % 360) + 360) % 360;
        double u, v;
        switch (rot)
        {
            case 90: u = ny; v = 1 - nx; break;
            case 180: u = 1 - nx; v = 1 - ny; break;
            case 270: u = 1 - ny; v = nx; break;
            default: u = nx; v = ny; break;
        }
        if (Mirror) u = 1 - u;
        double zoom = Math.Max(1.0, Zoom);
        double left = (1 - 1 / zoom) / 2 * (1 + Math.Clamp(PanX, -1, 1));
        double top = (1 - 1 / zoom) / 2 * (1 + Math.Clamp(PanY, -1, 1));
        return (Math.Clamp(left + u / zoom, 0, 1), Math.Clamp(top + v / zoom, 0, 1));
    }

    /// <summary>Returns the transformed BGRA buffer and its dimensions (swapped for 90°/270°).</summary>
    public (byte[] Pixels, int Width, int Height) Apply(byte[] src, int width, int height)
    {
        if (IsIdentity) return (src, width, height);
        int rot = ((Rotation % 360) + 360) % 360;
        if (rot % 90 != 0) throw new ArgumentException("Rotation must be a multiple of 90°.");
        bool swap = rot is 90 or 270;
        int outW = swap ? height : width, outH = swap ? width : height;

        // Crop window in source coordinates.
        double zoom = Math.Max(1.0, Zoom);
        double cropW = width / zoom, cropH = height / zoom;
        double left = (width - cropW) / 2 * (1 + Math.Clamp(PanX, -1, 1));
        double top = (height - cropH) / 2 * (1 + Math.Clamp(PanY, -1, 1));

        var dst = new byte[outW * outH * 4];
        Parallel.For(0, outH, oy =>
        {
            for (int ox = 0; ox < outW; ox++)
            {
                // Output pixel → unrotated, unmirrored normalised coords (u, v) in 0..1.
                double nx = (ox + 0.5) / outW, ny = (oy + 0.5) / outH;
                double u, v;
                switch (rot)
                {
                    case 90: u = ny; v = 1 - nx; break;
                    case 180: u = 1 - nx; v = 1 - ny; break;
                    case 270: u = 1 - ny; v = nx; break;
                    default: u = nx; v = ny; break;
                }
                if (Mirror) u = 1 - u;
                int sx = Math.Clamp((int)(left + u * cropW), 0, width - 1);
                int sy = Math.Clamp((int)(top + v * cropH), 0, height - 1);
                Buffer.BlockCopy(src, (sy * width + sx) * 4, dst, (oy * outW + ox) * 4, 4);
            }
        });
        return (dst, outW, outH);
    }
}
