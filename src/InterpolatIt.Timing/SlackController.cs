namespace InterpolatIt.Timing;

/// <summary>
/// Tracks slack (deadline minus the time a frame was ready) and late frames over sliding windows. The playback
/// delay is steered from these measurements instead of from a sum of assumed latencies: too little slack adds a
/// refresh of delay at once, a long stretch of spare slack removes one.
/// </summary>
public sealed class SlackController
{
    private readonly Queue<(double Time, double Slack)> _slack = new();
    private readonly Queue<double> _misses = new();
    private double _firstSample = double.NaN;
    private double _minSinceClear = double.PositiveInfinity;

    public const double ShortWindow = 1.0;
    public const double LongWindow = 10.0;

    public void AddSlack(double now, double slack)
    {
        if (double.IsNaN(_firstSample)) _firstSample = now;
        if (slack < _minSinceClear) _minSinceClear = slack;
        _slack.Enqueue((now, slack));
        Trim(now);
    }

    /// <summary>Smallest slack since the last <see cref="Clear"/>; +infinity without samples.</summary>
    public double MinSlackSinceClear => _minSinceClear;

    /// <summary>Seconds of samples since the last <see cref="Clear"/>.</summary>
    public double HistorySeconds(double now) => double.IsNaN(_firstSample) ? 0 : now - _firstSample;

    public void AddMiss(double now)
    {
        _misses.Enqueue(now);
        Trim(now);
    }

    private void Trim(double now)
    {
        while (_slack.Count > 0 && _slack.Peek().Time < now - LongWindow) _slack.Dequeue();
        while (_misses.Count > 0 && _misses.Peek() < now - ShortWindow) _misses.Dequeue();
    }

    public int MissesInShortWindow(double now)
    {
        Trim(now);
        return _misses.Count;
    }

    /// <summary>Smallest slack in the last <paramref name="window"/> seconds; +infinity without samples.</summary>
    public double MinSlack(double now, double window)
    {
        Trim(now);
        double min = double.PositiveInfinity;
        foreach (var (time, slack) in _slack)
            if (time >= now - window && slack < min) min = slack;
        return min;
    }

    /// <summary>True once samples cover the whole long window (so "spare slack for 10 s" is a real observation).</summary>
    public bool HasLongHistory(double now) => !double.IsNaN(_firstSample) && now - _firstSample >= LongWindow;

    public void Clear()
    {
        _slack.Clear();
        _misses.Clear();
        _firstSample = double.NaN;
        _minSinceClear = double.PositiveInfinity;
    }
}
