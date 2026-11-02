namespace OpenKamera.Core;

/// <summary>
/// Exposure check (preview only): diagonal red stripes where highlights are clipped, blue where shadows are
/// crushed, like a camera's zebras. Returns the share of each so the app can say "your face is too bright".
/// </summary>
public static class ExposureCheck
{
    public const int High = 245, Low = 12;

    public static (double Clipped, double Crushed) Apply(byte[] bgra, int w, int h, int phase = 0)
    {
        long clipped = 0, crushed = 0;
        object gate = new();
        Parallel.For(0, h, () => (0L, 0L), (y, _, acc) =>
        {
            for (int x = 0, i = y * w * 4; x < w; x++, i += 4)
            {
                int luma = (bgra[i + 2] * 77 + bgra[i + 1] * 150 + bgra[i] * 29) >> 8;
                bool stripe = ((x + y + phase) / 6 & 1) == 0;
                if (luma >= High) { acc.Item1++; if (stripe) { bgra[i] = 40; bgra[i + 1] = 40; bgra[i + 2] = 230; } }
                else if (luma <= Low) { acc.Item2++; if (stripe) { bgra[i] = 230; bgra[i + 1] = 110; bgra[i + 2] = 30; } }
            }
            return acc;
        }, acc => { lock (gate) { clipped += acc.Item1; crushed += acc.Item2; } });
        double n = Math.Max(1, (long)w * h);
        return (clipped / n, crushed / n);
    }
}
