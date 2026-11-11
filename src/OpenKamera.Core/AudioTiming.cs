namespace OpenKamera.Core;

/// <summary>
/// Keeps a recording's sound in step with its picture. Sound blocks arrive stamped with the recording clock, but a
/// sound track has to be written as one unbroken run. Laying blocks purely end to end (as before) meant that any
/// break in the sound, such as the audio engine restarting after a microphone change, pulled everything after it
/// earlier by the length of the break, for the rest of that piece of the recording.
///
/// This places each block where the clock says it belongs: a real gap is filled with silence of the same length,
/// sound that arrives for a moment already covered is left out, and small timing jitter is ignored so normal
/// sound stays seamless.
/// </summary>
public sealed class AudioTimeline
{
    /// <summary>How far a block's stamp may sit from the end of the track before it counts as a gap or an overlap.</summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromMilliseconds(60);

    private bool _started;

    /// <summary>Where the sound written so far ends.</summary>
    public TimeSpan End { get; private set; }

    /// <summary>
    /// Decides what to do with a block stamped <paramref name="at"/>. Returns false when it should be left out.
    /// Otherwise write <paramref name="silenceBefore"/> of silence first (often zero), then the block, calling
    /// <see cref="Advance"/> for each piece written.
    /// </summary>
    public bool Place(TimeSpan at, out TimeSpan silenceBefore)
    {
        silenceBefore = TimeSpan.Zero;
        if (at < TimeSpan.Zero) at = TimeSpan.Zero;
        var delta = at - End;
        if (!_started)
        {
            // The first block starts exactly where its stamp says, so both tracks begin together.
            _started = true;
            if (delta > TimeSpan.Zero) { silenceBefore = delta; return true; }
        }
        if (delta > Tolerance) { silenceBefore = delta; return true; }
        return delta >= -Tolerance;
    }

    /// <summary>Call after writing sound or silence of this length.</summary>
    public void Advance(TimeSpan written) => End += written;
}

/// <summary>
/// Delays sound by a fixed time, for lining voice up with lips: the picture takes longer to get through the camera
/// and the effects than sound does through the microphone, so undelayed sound runs slightly ahead.
/// </summary>
public sealed class AudioDelay
{
    private float[] _ring = [];
    private int _at;

    /// <summary>The delay in samples (all channels counted). Changing it clears what is held.</summary>
    public int Samples
    {
        get => _ring.Length;
        set { if (value != _ring.Length) { _ring = new float[Math.Max(0, value)]; _at = 0; } }
    }

    public static int SamplesFor(double milliseconds) =>
        (int)Math.Round(Math.Clamp(milliseconds, 0, 1000) / 1000 * AudioEngine.SampleRate) * AudioEngine.Channels;

    /// <summary>Returns the block delayed (the same array when there is no delay). The first output is silence.</summary>
    public float[] Process(float[] block)
    {
        var ring = _ring;
        if (ring.Length == 0) return block;
        var result = new float[block.Length];
        for (int i = 0; i < block.Length; i++)
        {
            if (_at >= ring.Length) _at = 0; // the delay was changed from another thread mid-block
            result[i] = ring[_at];
            ring[_at] = block[i];
            if (++_at == ring.Length) _at = 0;
        }
        return result;
    }
}
