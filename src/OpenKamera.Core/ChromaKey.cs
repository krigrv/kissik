namespace OpenKamera.Core;

/// <summary>
/// Physical green/blue screen keying: builds a person mask from colour distance in CbCr (brightness-independent,
/// so creases and shadows on the cloth still key) and removes coloured spill from the person's edges.
/// </summary>
public static class ChromaKey
{
    /// <param name="keyColor">"#RRGGBB" of the screen (default studio green).</param>
    /// <param name="similarity">0..1: how far from the key colour still counts as screen.</param>
    /// <param name="smoothness">0..1: width of the soft edge.</param>
    /// <param name="spill">0..1: how strongly the key colour is removed from the person's edges.</param>
    public static float[] Mask(byte[] bgra, int w, int h, string keyColor, double similarity, double smoothness, double spill)
    {
        var (_, kr, kg, kb) = ColorHex.Parse(keyColor, (255, 0, 177, 64));
        var (kcb, kcr) = CbCr(kr, kg, kb);
        float keyY = Math.Max(1f, Luma(kr, kg, kb));
        float sim = (float)Math.Clamp(similarity, 0.01, 1) * 80f;   // CbCr distance units (~0..180)
        float soft = Math.Max(1f, (float)Math.Clamp(smoothness, 0, 1) * 80f);
        float spillAmount = (float)Math.Clamp(spill, 0, 1);
        // Which channel the key colour pushes up: green screens raise G, blue screens raise B.
        int dominant = kg >= kb ? 1 : 0;
        var alpha = new float[w * h];
        Parallel.For(0, h, y =>
        {
            for (int x = 0, i = y * w * 4; x < w; x++, i += 4)
            {
                int b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
                var (cb, cr) = CbCr(r, g, b);
                // Chroma shrinks with brightness, so scale it to the key's brightness: shadowed cloth still keys.
                float s = Math.Min(3f, keyY / Math.Max(1f, Luma(r, g, b)));
                float dcb = (cb - 128) * s - (kcb - 128), dcr = (cr - 128) * s - (kcr - 128);
                float d = MathF.Sqrt(dcb * dcb + dcr * dcr);
                float a = Math.Clamp((d - sim) / soft, 0f, 1f);
                alpha[y * w + x] = a;
                if (spillAmount > 0 && a > 0)
                {
                    // Despill: clamp the dominant channel to the average of the other two near the edge.
                    float limit = dominant == 1 ? (r + b) / 2f : (r + g) / 2f;
                    float k = spillAmount * (1 - a * a);
                    if (dominant == 1 && g > limit) bgra[i + 1] = (byte)(g - (g - limit) * Math.Max(k, spillAmount * 0.5f));
                    if (dominant == 0 && b > limit) bgra[i] = (byte)(b - (b - limit) * Math.Max(k, spillAmount * 0.5f));
                }
            }
        });
        return alpha;
    }

    private static float Luma(int r, int g, int b) => 0.299f * r + 0.587f * g + 0.114f * b;

    private static (float Cb, float Cr) CbCr(int r, int g, int b) =>
        (128 - 0.168736f * r - 0.331264f * g + 0.5f * b, 128 + 0.5f * r - 0.418688f * g - 0.081312f * b);
}
