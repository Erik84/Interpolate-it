using InterpolatIt.Timing;

namespace InterpolatIt.Timing.Tests;

/// <summary>Synthetic sources and a small harness around estimator + scheduler.</summary>
internal static class Sim
{
    /// <summary>
    /// Capture timestamps of a source presenting frame n at phase + n / fps on a display refreshing at
    /// <paramref name="refresh"/> Hz: the compositor shows a frame at the first vblank at or after its present,
    /// so the timestamps are quantised to the refresh grid (that is what Windows.Graphics.Capture reports).
    /// </summary>
    public static double[] Captures(double fps, double refresh, int count, double phase = 0.0003)
    {
        var result = new double[count];
        double p = 1.0 / refresh;
        for (int n = 0; n < count; n++)
            result[n] = Math.Ceiling((phase + n / fps) / p - 1e-9) * p;
        return result;
    }

    /// <summary>Capture timestamps for explicit present times.</summary>
    public static double[] Quantise(IEnumerable<double> presentTimes, double refresh)
    {
        double p = 1.0 / refresh;
        return [.. presentTimes.Select(t => Math.Ceiling(t / p - 1e-9) * p)];
    }

    public sealed record Run(List<PlannedFrame> Plan, FrameScheduler Scheduler, CadenceEstimator Estimator, VBlankClock Clock,
        List<(long Id, SourceStamp Stamp, double Now)> Frames, List<int> PlanCountAfterFrame);

    /// <summary>Feeds captures through the estimator into the scheduler, the way the pipeline does.</summary>
    public static Run Schedule(double[] captures, double refresh, int factor, double captureLatency = 0.004,
        double generation = 0.006, Func<int, bool>? cutBefore = null, Action<FrameScheduler, int, double>? beforeFrame = null,
        bool useGate = true)
    {
        var clock = new VBlankClock(1.0 / refresh);
        clock.Update(0, 0.0);
        var estimator = new CadenceEstimator(1.0 / refresh);
        var scheduler = new FrameScheduler(clock, factor);
        var plan = new List<PlannedFrame>();
        var frames = new List<(long, SourceStamp, double)>();
        var counts = new List<int>();
        for (int n = 0; n < captures.Length; n++)
        {
            double c = captures[n];
            if (useGate && estimator.Classify(c) == GridClass.OffGrid)
            {
                estimator.NoteDropped();
                continue;
            }
            var stamp = estimator.Accept(c);
            double now = c + captureLatency;
            beforeFrame?.Invoke(scheduler, n, now);
            scheduler.OnSourceFrame(n, stamp, cutBefore?.Invoke(n) ?? false, now, generation, estimator.MedianInterval, plan);
            frames.Add((n, stamp, now));
            counts.Add(plan.Count);
        }
        return new Run(plan, scheduler, estimator, clock, frames, counts);
    }
}
