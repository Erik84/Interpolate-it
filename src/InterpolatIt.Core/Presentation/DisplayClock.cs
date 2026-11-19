using InterpolatIt.Core.Native;

namespace InterpolatIt.Core.Presentation;

/// <summary>Helpers around the compositor clock (DWM's frame tick, aligned to the display's vblank).</summary>
public static class DisplayClock
{
    /// <summary>
    /// Waits for the next compositor tick. Returns false when the wait failed or timed out (display off,
    /// session locked), in which case callers should back off instead of spinning.
    /// </summary>
    public static bool WaitForTick(uint timeoutMs = 100)
        => DComp.DCompositionWaitForCompositorClock(0, 0, timeoutMs) == 0 /* WAIT_OBJECT_0 */;

    /// <summary>Measures the refresh period from the spacing of compositor ticks (about a third of a second).</summary>
    public static double MeasureRefreshPeriod(int ticks = 40)
    {
        var times = new List<double>(ticks + 1);
        for (int i = 0; i <= ticks; i++)
        {
            if (!WaitForTick()) break;
            times.Add(Qpc.Now);
        }
        if (times.Count < 8) return 1.0 / 60;
        // Median of the tick spacings rejects the odd late wake-up; then refine with the long baseline.
        var deltas = new double[times.Count - 1];
        for (int i = 1; i < times.Count; i++) deltas[i - 1] = times[i] - times[i - 1];
        Array.Sort(deltas);
        double median = deltas[deltas.Length / 2];
        double span = times[^1] - times[0];
        int periods = (int)Math.Round(span / median);
        return periods > 0 ? span / periods : median;
    }
}
