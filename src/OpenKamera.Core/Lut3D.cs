using System.Globalization;

namespace OpenKamera.Core;

/// <summary>A 3D colour lookup table loaded from an Adobe/Resolve <c>.cube</c> file.</summary>
public sealed class Lut3D
{
    public string Name { get; }
    public int Size { get; }
    private readonly float[] _data; // r fastest, then g, then b; 3 floats per entry
    private readonly float[] _min = [0, 0, 0], _max = [1, 1, 1];

    private Lut3D(string name, int size, float[] data, float[] min, float[] max)
    {
        Name = name; Size = size; _data = data; _min = min; _max = max;
    }

    /// <summary>Raw entries (r fastest, 3 floats each) and input domain, for the GPU path.</summary>
    internal float[] Data => _data;
    internal (float[] Min, float[] Max) Domain => (_min, _max);

    public static Lut3D Load(string path) =>
        Parse(File.ReadAllText(path), Path.GetFileNameWithoutExtension(path));

    public const int MaxSize = 256;

    public static Lut3D Parse(string text, string name = "LUT")
    {
        int size = 0;
        float[] min = [0, 0, 0], max = [1, 1, 1];
        var values = new List<float>();
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            switch (parts[0].ToUpperInvariant())
            {
                case "TITLE": name = line[5..].Trim().Trim('"'); continue;
                case "LUT_3D_SIZE": size = int.Parse(parts[1], CultureInfo.InvariantCulture); continue;
                case "LUT_1D_SIZE": throw new FormatException("1D LUTs are not supported; use a 3D .cube file.");
                case "DOMAIN_MIN": min = ParseTriple(parts); continue;
                case "DOMAIN_MAX": max = ParseTriple(parts); continue;
            }
            if (parts.Length == 3 && float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var r))
            {
                values.Add(r);
                values.Add(float.Parse(parts[1], CultureInfo.InvariantCulture));
                values.Add(float.Parse(parts[2], CultureInfo.InvariantCulture));
            }
        }
        if (size < 2) throw new FormatException("Missing or invalid LUT_3D_SIZE.");
        if (size > MaxSize) throw new FormatException($"LUT_3D_SIZE {size} is too large (the limit is {MaxSize}).");
        if (values.Count != size * size * size * 3)
            throw new FormatException($"Expected {size * size * size} entries, found {values.Count / 3}.");
        return new Lut3D(name, size, values.ToArray(), min, max);
    }

    private static float[] ParseTriple(string[] p) =>
        [float.Parse(p[1], CultureInfo.InvariantCulture), float.Parse(p[2], CultureInfo.InvariantCulture), float.Parse(p[3], CultureInfo.InvariantCulture)];

    /// <summary>Trilinear sample; inputs and outputs are 0..1.</summary>
    public (float R, float G, float B) Sample(float r, float g, float b)
    {
        int n = Size - 1;
        float fr = Norm(r, 0) * n, fg = Norm(g, 1) * n, fb = Norm(b, 2) * n;
        int r0 = (int)fr, g0 = (int)fg, b0 = (int)fb;
        int r1 = Math.Min(r0 + 1, n), g1 = Math.Min(g0 + 1, n), b1 = Math.Min(b0 + 1, n);
        float dr = fr - r0, dg = fg - g0, db = fb - b0;
        float or = 0, og = 0, ob = 0;
        Accumulate(r0, g0, b0, (1 - dr) * (1 - dg) * (1 - db));
        Accumulate(r1, g0, b0, dr * (1 - dg) * (1 - db));
        Accumulate(r0, g1, b0, (1 - dr) * dg * (1 - db));
        Accumulate(r1, g1, b0, dr * dg * (1 - db));
        Accumulate(r0, g0, b1, (1 - dr) * (1 - dg) * db);
        Accumulate(r1, g0, b1, dr * (1 - dg) * db);
        Accumulate(r0, g1, b1, (1 - dr) * dg * db);
        Accumulate(r1, g1, b1, dr * dg * db);
        return (or, og, ob);

        void Accumulate(int ri, int gi, int bi, float w)
        {
            int idx = ((bi * Size + gi) * Size + ri) * 3;
            or += _data[idx] * w; og += _data[idx + 1] * w; ob += _data[idx + 2] * w;
        }
    }

    private float Norm(float v, int c) => Math.Clamp((v - _min[c]) / (_max[c] - _min[c]), 0f, 1f);
}
