using System.Runtime.InteropServices;
using Windows.Graphics.Imaging;

namespace OpenKamera.Core;

/// <summary>
/// Camera YUV (NV12, YUY2) to BGRA, BT.601 limited range, nearest chroma. The GPU does this in the frame path
/// (GpuEffects.hlsl Nv12 / Yuy2); this is the same maths on the CPU, for tests and for reading raw planes.
/// </summary>
public static class YuvConvert
{
    public static bool IsYuv(SoftwareBitmap b) => b.BitmapPixelFormat is BitmapPixelFormat.Nv12 or BitmapPixelFormat.Yuy2;

    public static uint Pixel(int y, int u, int v)
    {
        int c = 298 * (y - 16), d = u - 128, e = v - 128;
        uint b = (uint)Math.Clamp((c + 516 * d + 128) >> 8, 0, 255);
        uint g = (uint)Math.Clamp((c - 100 * d - 208 * e + 128) >> 8, 0, 255);
        uint r = (uint)Math.Clamp((c + 409 * e + 128) >> 8, 0, 255);
        return b | g << 8 | r << 16 | 0xFF000000;
    }

    /// <summary>NV12 laid out as <paramref name="stride"/>-byte rows of Y, then the interleaved UV rows at <paramref name="uvOffset"/>.</summary>
    public static byte[] Nv12ToBgra(ReadOnlySpan<byte> src, int w, int h, int stride, int uvOffset)
    {
        var dst = new byte[w * h * 4];
        var px = MemoryMarshal.Cast<byte, uint>(dst.AsSpan());
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int uv = uvOffset + (y >> 1) * stride + (x & ~1);
                px[y * w + x] = Pixel(src[y * stride + x], src[uv], src[uv + 1]);
            }
        return dst;
    }

    public static byte[] Yuy2ToBgra(ReadOnlySpan<byte> src, int w, int h, int stride)
    {
        var dst = new byte[w * h * 4];
        var px = MemoryMarshal.Cast<byte, uint>(dst.AsSpan());
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int b = y * stride + (x >> 1) * 4;
                px[y * w + x] = Pixel(src[b + (x & 1) * 2], src[b + 1], src[b + 3]);
            }
        return dst;
    }

    /// <summary>
    /// Hands the bitmap's raw bytes (no copy) to <paramref name="use"/>: pointer to the first Y byte, byte count
    /// covering every plane, row stride, and the UV plane's offset from the pointer (NV12; 0 for YUY2).
    /// </summary>
    public static unsafe T Read<T>(SoftwareBitmap b, Func<nint, int, int, int, T> use)
    {
        using var buf = b.LockBuffer(BitmapBufferAccessMode.Read);
        var yp = buf.GetPlaneDescription(0);
        int h = b.PixelHeight, uvOffset = 0, bytes;
        if (b.BitmapPixelFormat == BitmapPixelFormat.Nv12)
        {
            var uv = buf.GetPlaneDescription(1);
            uvOffset = uv.StartIndex - yp.StartIndex;
            bytes = uvOffset + uv.Stride * ((h + 1) / 2);
        }
        else bytes = yp.Stride * h;
        using var reference = buf.CreateReference();
        WinRT.CastExtensions.As<IMemoryBufferByteAccess>(reference).GetBuffer(out byte* data, out uint capacity);
        if (yp.StartIndex + bytes > capacity) throw new InvalidOperationException("Frame buffer is smaller than its planes.");
        return use((nint)(data + yp.StartIndex), bytes, yp.Stride, uvOffset);
    }

    [ComImport, Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private unsafe interface IMemoryBufferByteAccess { void GetBuffer(out byte* buffer, out uint capacity); }
}
