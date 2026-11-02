namespace OpenKamera.Core;

/// <summary>An H.264 packet from the native phone app: 'V' | flags (bit0 keyframe) | uint64 LE pts µs | Annex-B.</summary>
public readonly record struct PhoneVideoPacket(bool Keyframe, long TimestampUs, ReadOnlyMemory<byte> AnnexB)
{
    public static PhoneVideoPacket? TryParse(byte[] data)
    {
        if (data.Length <= 10 || data[0] != (byte)'V') return null;
        long pts = (long)BitConverter.ToUInt64(data, 2);
        return new PhoneVideoPacket((data[1] & 1) != 0, pts, data.AsMemory(10));
    }
}

/// <summary>Pixel helpers for decoded phone video.</summary>
public static class PhoneVideo
{
    /// <summary>
    /// NV12 (Y plane, then interleaved UV at half size) to BGRA, turned clockwise by <paramref name="rotation"/>
    /// (0/90/180/270). BT.709 limited range, which is what phone encoders send.
    /// </summary>
    public static byte[] Nv12ToBgra(ReadOnlySpan<byte> nv12, int pitch, int width, int height, int uvOffset, int rotation, out int outW, out int outH)
    {
        var dst = new byte[width * height * 4];
        Nv12ToBgra(nv12, pitch, width, height, uvOffset, rotation, dst, out outW, out outH);
        return dst;
    }

    /// <summary>Same, into <paramref name="dst"/> (at least width × height × 4 bytes), so callers can reuse buffers.</summary>
    public static unsafe void Nv12ToBgra(ReadOnlySpan<byte> nv12, int pitch, int width, int height, int uvOffset, int rotation, byte[] dst, out int outW, out int outH)
    {
        rotation = ((rotation % 360) + 360) % 360;
        bool swap = rotation is 90 or 270;
        int ow = swap ? height : width, oh = swap ? width : height;
        outW = ow; outH = oh;
        if ((long)uvOffset + pitch * ((height + 1) / 2) > nv12.Length || uvOffset < pitch * height) throw new ArgumentException("NV12 buffer too small.");
        if (dst.Length < ow * oh * 4) throw new ArgumentException("Destination too small.", nameof(dst));
        // Upright pictures convert straight into the result; turned ones go through a scratch picture first, then a
        // tiled rotate (writing pixels in scan order and turning in small blocks keeps both passes in cache).
        uint[]? temp = rotation == 0 ? null : System.Buffers.ArrayPool<uint>.Shared.Rent(width * height);
        try
        {
            fixed (byte* srcFixed = nv12)
            fixed (byte* dstFixed = dst)
            fixed (uint* tempFixed = temp)
            fixed (byte* clampFixed = Clamp)
            {
                byte* src = srcFixed, cl = clampFixed + 384;
                uint* conv = rotation == 0 ? (uint*)dstFixed : tempFixed;
                int w = width, p = pitch, uv = uvOffset;
                Parallel.For(0, (height + 15) / 16, band =>
                {
                    for (int y = band * 16, yEnd = Math.Min(y + 16, height); y < yEnd; y++)
                    {
                        byte* yRow = src + (long)y * p, uvRow = src + uv + (long)(y >> 1) * p;
                        uint* o = conv + (long)y * w;
                        for (int x = 0; x < w; x += 2)
                        {
                            int u = uvRow[x] - 128, v = uvRow[x + 1] - 128;
                            int rv = 459 * v + 128, gv = -55 * u - 136 * v + 128, bu = 541 * u + 128;
                            int c = (yRow[x] - 16) * 298;
                            o[x] = 0xFF000000u | (uint)cl[(c + rv) >> 8] << 16 | (uint)cl[(c + gv) >> 8] << 8 | cl[(c + bu) >> 8];
                            if (x + 1 < w)
                            {
                                c = (yRow[x + 1] - 16) * 298;
                                o[x + 1] = 0xFF000000u | (uint)cl[(c + rv) >> 8] << 16 | (uint)cl[(c + gv) >> 8] << 8 | cl[(c + bu) >> 8];
                            }
                        }
                    }
                });
                if (rotation != 0) Rotate(tempFixed, (uint*)dstFixed, width, height, rotation);
            }
        }
        finally { if (temp is not null) System.Buffers.ArrayPool<uint>.Shared.Return(temp); }
    }

    // Clamp table for -384..639 (the full range the integer maths above can reach).
    private static readonly byte[] Clamp = Enumerable.Range(-384, 1024).Select(i => (byte)Math.Clamp(i, 0, 255)).ToArray();

    private static unsafe void Rotate(uint* src, uint* dst, int w, int h, int rotation)
    {
        const int T = 32;
        int ow = rotation == 180 ? w : h;
        Parallel.For(0, (h + T - 1) / T, by =>
        {
            for (int bx = 0; bx < w; bx += T)
                for (int y = by * T, yEnd = Math.Min(y + T, h); y < yEnd; y++)
                {
                    uint* row = src + (long)y * w;
                    int xEnd = Math.Min(bx + T, w);
                    switch (rotation)
                    {
                        case 90: { uint* d = dst + (long)bx * ow + (h - 1 - y); for (int x = bx; x < xEnd; x++, d += ow) *d = row[x]; break; }
                        case 270: { uint* d = dst + (long)(w - 1 - bx) * ow + y; for (int x = bx; x < xEnd; x++, d -= ow) *d = row[x]; break; }
                        default: { uint* d = dst + (long)(h - 1 - y) * ow + (w - 1 - bx); for (int x = bx; x < xEnd; x++, d--) *d = row[x]; break; }
                    }
                }
        });
    }

    /// <summary>
    /// Splits an Annex-B H.264 stream into access units (one picture each, with any SPS/PPS in front), the way the
    /// phone sends them. A picture may have several slices; a new one starts at a slice whose first macroblock is 0.
    /// </summary>
    public static IEnumerable<(byte[] Data, bool Keyframe)> SplitAccessUnits(byte[] stream)
    {
        var nals = new List<int>(); // start-code offsets
        for (int i = 0; i + 3 < stream.Length; i++)
            if (stream[i] == 0 && stream[i + 1] == 0 && stream[i + 2] == 1)
            {
                nals.Add(i > 0 && stream[i - 1] == 0 ? i - 1 : i);
                i += 2;
            }
        int auStart = -1; bool hasSlice = false, key = false;
        foreach (int at in nals)
        {
            int h = at + (stream[at + 2] == 1 ? 3 : 4);
            int type = stream[h] & 0x1F;
            bool slice = type is 1 or 5;
            // first_mb_in_slice is the first ue(v) of the slice header: a leading 1 bit means 0.
            bool startsPicture = slice ? h + 1 < stream.Length && (stream[h + 1] & 0x80) != 0 : type is 6 or 7 or 8 or 9;
            if (hasSlice && startsPicture)
            {
                yield return (stream[auStart..at], key);
                auStart = -1; hasSlice = false; key = false;
            }
            if (auStart < 0) auStart = at;
            if (slice) hasSlice = true;
            if (type == 5) key = true;
        }
        if (auStart >= 0 && hasSlice) yield return (stream[auStart..], key);
    }
}
