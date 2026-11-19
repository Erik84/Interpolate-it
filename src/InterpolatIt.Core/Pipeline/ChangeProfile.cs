namespace InterpolatIt.Core.Pipeline;

/// <summary>
/// Tells content frames from frames in which next to nothing happened, relative to what this source usually does.
/// <para>
/// Why: a window does not only present its content. A streaming client shows the same decoded picture twice when
/// a 30 fps game is sent as a 60 fps stream (the copies differ by a little re-compression noise), a toolbar
/// fades, a caret blinks. Taken as content frames these double the apparent frame rate, and interpolating
/// between a picture and its near-copy puts three output frames on the same spot and then jumps.
/// </para>
/// <para>
/// A frame is minor when little of the picture changed strongly (under 2 %) and that is also far less than is
/// usual for the frames accepted lately (under a quarter of their upper quartile). "Relative" matters: in a
/// scene where only a small object moves, every frame is small, none is small compared to the others, and all
/// are kept. A minor frame is simply skipped; the next content frame is a complete picture and contains
/// whatever it showed.
/// </para>
/// </summary>
internal sealed class ChangeProfile
{
    /// <summary>Largest share of strongly changed tiles a frame may have and still be called minor.</summary>
    public const float MinorShare = 0.02f;

    private const int HistorySize = 16, MinHistory = 8;

    /// <summary>After this many minor frames in a row the source itself has changed (the action scene became a
    /// menu): forget what was usual and start learning again.</summary>
    private const int MinorRunLimit = 32;

    private readonly float[] _history = new float[HistorySize];
    private readonly float[] _sorted = new float[HistorySize];
    private int _count, _next, _minorRun;

    /// <summary>Upper quartile of the strongly changed share of the recently accepted frames.</summary>
    public float Typical
    {
        get
        {
            if (_count == 0) return 0f;
            Array.Copy(_history, _sorted, _count);
            Array.Sort(_sorted, 0, _count);
            return _sorted[Math.Min(_count - 1, _count * 3 / 4)];
        }
    }

    /// <summary>Is a frame with this share of strongly changed tiles a minor one? Counts it if so.</summary>
    public bool IsMinor(float strongShare)
    {
        if (_count < MinHistory || strongShare >= MinorShare || strongShare >= 0.25f * Typical)
        {
            return false;
        }
        if (++_minorRun >= MinorRunLimit)
        {
            Clear();
            return false;
        }
        return true;
    }

    /// <summary>Records an accepted frame.</summary>
    public void Add(float strongShare)
    {
        _history[_next] = strongShare;
        _next = (_next + 1) % HistorySize;
        if (_count < HistorySize) _count++;
        _minorRun = 0;
    }

    public void Clear()
    {
        _count = _next = _minorRun = 0;
    }
}
