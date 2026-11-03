using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.MediaFoundation;

namespace OpenKamera.Core;

/// <summary>
/// A looping video behind you (rain on a window, a city at night). Decoded with Windows Media Foundation (hardware
/// decoders, nothing extra to install) on its own thread, paced to the video's own timing, and scaled to the
/// output size. <see cref="Latest"/> always holds the newest frame; the frame thread just reads it.
/// </summary>
public sealed class VideoBackground : IDisposable
{
    private static int _mfStarted;
    private readonly Thread _thread;
    private volatile bool _stop;
    private volatile byte[]? _latest;
    private readonly int _outW, _outH;

    public string Path { get; }
    public string? Error { get; private set; }
    /// <summary>Newest frame, BGRA at the output size (cover-fitted), or null before the first frame.</summary>
    public byte[]? Latest => _latest;

    public VideoBackground(string path, int outWidth, int outHeight)
    {
        Path = path; _outW = outWidth; _outH = outHeight;
        if (Interlocked.Exchange(ref _mfStarted, 1) == 0) MediaFactory.MFStartup();
        _thread = new Thread(Run) { IsBackground = true, Name = "Video background", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    private void Run()
    {
        try
        {
            using var attr = MediaFactory.MFCreateAttributes(1);
            attr.Set(SourceReaderAttributeKeys.EnableVideoProcessing, 1u); // let MF convert to RGB32 for us
            using var reader = MediaFactory.MFCreateSourceReaderFromURL(Path, attr);
            using (var want = MediaFactory.MFCreateMediaType())
            {
                want.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                want.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32);
                reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, want);
            }
            int w, h;
            using (var got = reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream))
            {
                ulong size = got.GetUInt64(MediaTypeAttributeKeys.FrameSize);
                w = (int)(size >> 32); h = (int)(size & 0xFFFFFFFF);
            }
            var clock = Stopwatch.StartNew();
            long loopStart = 0; // 100 ns units of the video, at the moment the clock started for this loop
            var raw = new byte[w * h * 4];
            while (!_stop)
            {
                using var sample = reader.ReadSample(SourceReaderIndex.FirstVideoStream, SourceReaderControlFlag.None,
                                                     out _, out var flags, out long ts);
                if ((flags & SourceReaderFlag.EndOfStream) != 0)
                {
                    reader.SetCurrentPosition(0); clock.Restart(); loopStart = 0; continue; // loop forever
                }
                if (sample is null) continue;
                // Pace to the video's own timestamps so it plays at normal speed whatever the output fps.
                long due = (ts - loopStart) / 10_000 - clock.ElapsedMilliseconds;
                if (due > 0) Thread.Sleep((int)Math.Min(due, 200));
                using var buffer = sample.ConvertToContiguousBuffer();
                buffer.Lock(out IntPtr ptr, out _, out int len);
                try { Marshal.Copy(ptr, raw, 0, Math.Min(len, raw.Length)); }
                finally { buffer.Unlock(); }
                for (int i = 3; i < raw.Length; i += 4) raw[i] = 255; // RGB32's fourth byte is padding, not alpha
                _latest = OutputFit.Cover(raw, w, h, _outW, _outH).Pixels;
            }
        }
        catch (Exception ex) { Error = ex.Message; }
    }

    public void Dispose() { _stop = true; _thread.Join(1000); }
}
