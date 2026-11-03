namespace OpenKamera.Core;

public enum ColourVisionType { Off, Deuteranopia, Protanopia, Tritanopia }

/// <summary>
/// Shows the picture as people with colour-vision deficiency see it (Machado, Oliveira and Fernandes 2009,
/// full severity), so you can check that scene text and slides still read well. Preview only.
/// </summary>
public static class ColourVision
{
    private static readonly float[][] M =
    [
        [],
        [0.367322f, 0.860646f, -0.227968f, 0.280085f, 0.672501f, 0.047413f, -0.011820f, 0.042940f, 0.968881f],   // deuteranopia
        [0.152286f, 1.052583f, -0.204868f, 0.114503f, 0.786281f, 0.099216f, -0.003882f, -0.048116f, 1.051998f],  // protanopia
        [1.255528f, -0.076749f, -0.178779f, -0.078411f, 0.930809f, 0.147602f, 0.004733f, 0.691367f, 0.303900f],  // tritanopia
    ];

    public static void Apply(byte[] bgra, ColourVisionType type)
    {
        if (type == ColourVisionType.Off) return;
        var m = M[(int)type];
        Parallel.For(0, bgra.Length / 4 / 4096 + 1, chunk =>
        {
            int start = chunk * 4096 * 4, end = Math.Min(bgra.Length, start + 4096 * 4);
            for (int i = start; i < end; i += 4)
            {
                float b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
                bgra[i + 2] = Sat(m[0] * r + m[1] * g + m[2] * b);
                bgra[i + 1] = Sat(m[3] * r + m[4] * g + m[5] * b);
                bgra[i] = Sat(m[6] * r + m[7] * g + m[8] * b);
            }
        });
    }

    private static byte Sat(float v) => (byte)Math.Clamp(v, 0, 255);
}
