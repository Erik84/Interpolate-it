using InterpolatIt.Core.Gpu;
using InterpolatIt.Timing;

namespace InterpolatIt.Core.Pipeline;

/// <summary>A rendered frame waiting for its display refresh.</summary>
public readonly record struct QueuedFrame(long Refresh, GpuFrame Frame, PlannedKind Kind, long FrameA, long FrameB, float T);

/// <summary>
/// Frames handed from the pipeline thread to the presenter, ordered by the refresh they are planned for.
/// </summary>
public sealed class PresentQueue : IDisposable
{
    private readonly Lock _lock = new();
    private readonly List<QueuedFrame> _frames = new(32);
    private readonly AutoResetEvent _arrived = new(false);

    public int Count
    {
        get { lock (_lock) return _frames.Count; }
    }

    /// <summary>Signalled whenever a frame is enqueued (the presenter waits on it inside a refresh interval).</summary>
    public nint ArrivedHandle => _arrived.SafeWaitHandle.DangerousGetHandle();

    public void Enqueue(in QueuedFrame frame)
    {
        lock (_lock)
        {
            int i = _frames.Count;
            while (i > 0 && _frames[i - 1].Refresh > frame.Refresh) i--;
            if (i > 0 && _frames[i - 1].Refresh == frame.Refresh) _frames[i - 1] = frame;      // same slot: newest wins
            else _frames.Insert(i, frame);
        }
        _arrived.Set();
    }

    /// <summary>
    /// Takes the frame due at <paramref name="refresh"/>: the latest one planned for that refresh or earlier.
    /// Older ones are discarded and counted in <paramref name="stale"/> (a late frame is still shown, but two
    /// frames never share a refresh).
    /// <para>
    /// With <paramref name="onTimeOnly"/> nothing is taken unless the frame planned for exactly this refresh is
    /// there: early in a refresh interval it is better to wait for it than to show an overdue one in its place.
    /// </para>
    /// </summary>
    public bool TryTakeDue(long refresh, bool onTimeOnly, out QueuedFrame frame, out int stale)
    {
        frame = default;
        stale = 0;
        lock (_lock)
        {
            int due = 0;
            while (due < _frames.Count && _frames[due].Refresh <= refresh) due++;
            if (due == 0) return false;
            if (onTimeOnly && _frames[due - 1].Refresh != refresh) return false;
            frame = _frames[due - 1];
            stale = due - 1;
            _frames.RemoveRange(0, due);
            return true;
        }
    }

    /// <summary>The refresh of the earliest queued frame, or null when the queue is empty.</summary>
    public long? NextRefresh
    {
        get { lock (_lock) return _frames.Count > 0 ? _frames[0].Refresh : null; }
    }

    public void Clear()
    {
        lock (_lock) _frames.Clear();
    }

    public void Dispose() => _arrived.Dispose();
}
