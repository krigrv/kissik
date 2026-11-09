using Vortice.MediaFoundation;

namespace OpenKamera.Core;

/// <summary>
/// Joins the pieces of a recording by copying their already-encoded picture and sound into one file. Nothing is
/// decoded or encoded again, so an hour-long recording is saved in seconds instead of minutes, and the picture is
/// not compressed a second time.
/// </summary>
public static class RecordingJoiner
{
    private static readonly Guid DecodeTime = new("73A954D4-09E2-4861-BEFC-94BD97C08E6E"); // MFSampleExtension_DecodeTimestamp
    private static int s_started;

    /// <summary>
    /// Copies <paramref name="parts"/> end to end into <paramref name="output"/>. Returns false (and leaves no file)
    /// when that isn't possible, for example when the pieces were made by different encoders; the caller then
    /// falls back to re-encoding.
    /// </summary>
    public static bool TryCopyJoin(IReadOnlyList<string> parts, string output)
    {
        if (parts.Count == 0) return false;
        string temp = output + ".joining";
        IMFSinkWriter? writer = null;
        try
        {
            if (Interlocked.Exchange(ref s_started, 1) == 0) MediaFactory.MFStartup(true).CheckError();
            int videoOut = -1, audioOut = -1;
            bool hasAudio = false;
            Guid videoKind = default; ulong videoSize = 0;
            long offset = 0;
            foreach (string part in parts)
            {
                using var reader = MediaFactory.MFCreateSourceReaderFromURL(part, null!);
                using var video = reader.GetNativeMediaType(SourceReaderIndex.FirstVideoStream, 0);
                IMFMediaType? audio = null;
                try { audio = reader.GetNativeMediaType(SourceReaderIndex.FirstAudioStream, 0); } catch { /* a silent recording */ }
                try
                {
                    var kind = video.GetGUID(MediaTypeAttributeKeys.Subtype);
                    ulong size = video.GetUInt64(MediaTypeAttributeKeys.FrameSize);
                    if (writer is null)
                    {
                        using var attrs = MediaFactory.MFCreateAttributes(1);
                        attrs.Set(SinkWriterAttributeKeys.DisableThrottling, 1u);
                        // The container is chosen by the name: write as .mp4, then rename over the output.
                        writer = MediaFactory.MFCreateSinkWriterFromURL(temp + ".mp4", null!, attrs);
                        // The same type in and out means "already encoded": the writer only packs it into the file.
                        videoOut = writer.AddStream(video);
                        writer.SetInputMediaType(videoOut, video, null!);
                        if (audio is not null)
                        {
                            audioOut = writer.AddStream(audio);
                            writer.SetInputMediaType(audioOut, audio, null!);
                        }
                        writer.BeginWriting();
                        (hasAudio, videoKind, videoSize) = (audio is not null, kind, size);
                    }
                    else if (kind != videoKind || size != videoSize || (audio is not null) != hasAudio)
                        return Fail(ref writer, temp); // pieces that don't match can't share one track

                    long end = Copy(reader, SourceReaderIndex.FirstVideoStream, writer, videoOut, offset);
                    if (hasAudio) Copy(reader, SourceReaderIndex.FirstAudioStream, writer, audioOut, offset);
                    if (end <= offset) return Fail(ref writer, temp); // an empty piece: let the careful path decide
                    offset = end;
                }
                finally { audio?.Dispose(); }
            }
            writer!.Finalize();
            writer.Dispose(); writer = null;
            File.Move(temp + ".mp4", output, overwrite: true);
            return true;
        }
        catch { return Fail(ref writer, temp); }
    }

    private static bool Fail(ref IMFSinkWriter? writer, string temp)
    {
        try { writer?.Dispose(); } catch { }
        writer = null;
        try { File.Delete(temp + ".mp4"); } catch { }
        return false;
    }

    /// <summary>Copies one track of one piece, moved later by <paramref name="offset"/>. Returns where it ends (100 ns units).</summary>
    private static long Copy(IMFSourceReader reader, SourceReaderIndex from, IMFSinkWriter writer, int to, long offset)
    {
        long end = offset;
        while (true)
        {
            using var sample = reader.ReadSample(from, SourceReaderControlFlag.None, out _, out var flags, out long time);
            if ((flags & (SourceReaderFlag.EndOfStream | SourceReaderFlag.Error)) != 0) break;
            if (sample is null) continue; // a gap marker: nothing to copy
            sample.SampleTime = time + offset;
            // Frames stored out of display order carry a second stamp for decoding, which moves with the first.
            try { sample.Set(DecodeTime, (ulong)((long)sample.GetUInt64(DecodeTime) + offset)); } catch { /* not present */ }
            writer.WriteSample(to, sample);
            end = Math.Max(end, time + offset + sample.SampleDuration);
        }
        return end;
    }

    /// <summary>Decodes a file and counts its pictures (used by tests to prove a joined file really plays through).</summary>
    public static int CountFrames(string path)
    {
        if (Interlocked.Exchange(ref s_started, 1) == 0) MediaFactory.MFStartup(true).CheckError();
        using var reader = MediaFactory.MFCreateSourceReaderFromURL(path, null!);
        using var want = MediaFactory.MFCreateMediaType();
        want.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        want.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
        reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, want); // puts a decoder in the chain
        int n = 0;
        while (true)
        {
            using var sample = reader.ReadSample(SourceReaderIndex.FirstVideoStream, SourceReaderControlFlag.None, out _, out var flags, out _);
            if ((flags & (SourceReaderFlag.EndOfStream | SourceReaderFlag.Error)) != 0) break;
            if (sample is not null) n++;
        }
        return n;
    }
}
