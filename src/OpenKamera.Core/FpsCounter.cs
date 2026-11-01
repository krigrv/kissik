using System.Diagnostics;

namespace OpenKamera.Core;

/// <summary>Frames per second over a sliding one-second window. Thread-safe.</summary>
public sealed class FpsCounter
{
    private readonly Queue<long> _ticks = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _gate = new();

    public void Tick()
    {
        lock (_gate)
        {
            long now = _clock.ElapsedMilliseconds;
            _ticks.Enqueue(now);
            while (_ticks.Count > 0 && now - _ticks.Peek() > 1000) _ticks.Dequeue();
        }
    }

    public double Fps
    {
        get
        {
            lock (_gate)
            {
                long now = _clock.ElapsedMilliseconds;
                while (_ticks.Count > 0 && now - _ticks.Peek() > 1000) _ticks.Dequeue();
                return _ticks.Count;
            }
        }
    }
}
