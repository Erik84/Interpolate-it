namespace InterpolatIt.Timing;

/// <summary>
/// Chooses the output cadence. Output frames are shown every m-th display refresh, where m makes the output
/// rate the display-synchronous rate nearest to (source rate x factor): 25 fps "x2" on 120 Hz becomes 60 fps.
/// </summary>
public static class PacingPolicy
{
    /// <summary>More than this many slot slips per second between source x factor and the output rate means the
    /// two are not the same rate, and timesteps must be computed per slot.</summary>
    public const double MaxSteppedSlipsPerSecond = 0.25;

    /// <param name="sourcePeriod">Seconds between source frames (locked period, or the median gap when free).</param>
    /// <param name="refreshPeriod">Seconds per display refresh.</param>
    /// <param name="factor">Requested multiplier N.</param>
    /// <param name="locked">Whether the source cadence is locked.</param>
    /// <param name="refreshesPerFrame">Slot spacing to keep (hysteresis); 0 = choose the nearest.</param>
    public static PacingMode Select(double sourcePeriod, double refreshPeriod, int factor, bool locked, int refreshesPerFrame = 0)
    {
        if (factor < 1) throw new ArgumentOutOfRangeException(nameof(factor));
        if (!(sourcePeriod > 0) || !(refreshPeriod > 0))
            return new PacingMode(PacingKind.Continuous, factor, Math.Max(1, refreshesPerFrame));

        int m = refreshesPerFrame > 0 ? refreshesPerFrame : RefreshesPerFrame(sourcePeriod, refreshPeriod, factor);
        if (locked)
        {
            // How many output frames per source frame does that cadence really give? When it is a whole number
            // the timesteps are exact fractions k/K and can be counted - also when K is not the factor that was
            // asked for: 24 fps "x4" on 120 Hz is exactly x5, 30 fps "x3" is exactly x4, 60 fps "x4" is x2.
            double perSource = sourcePeriod / (m * refreshPeriod);
            int whole = (int)Math.Round(perSource);
            if (whole >= 1)
            {
                double slipsPerSecond = Math.Abs(1.0 / (m * refreshPeriod) - whole / sourcePeriod);
                if (slipsPerSecond < MaxSteppedSlipsPerSecond)
                    return new PacingMode(PacingKind.Stepped, whole, m);
            }
        }
        return new PacingMode(PacingKind.Continuous, factor, m);
    }

    /// <summary>A measured value this close to a tie between two spacings still counts as the tie.</summary>
    public const double TieMargin = 0.02;

    /// <summary>m = round(T / (N * P)), at least 1; a tie goes to the faster output. Periods are measured, so a
    /// tie is a small band rather than a point: 24 fps "x2" on a display measured at 120.08 Hz is 2.502.</summary>
    public static int RefreshesPerFrame(double sourcePeriod, double refreshPeriod, int factor)
        => Math.Max(1, (int)Math.Floor(ExactRefreshesPerFrame(sourcePeriod, refreshPeriod, factor) + 0.5 - TieMargin));

    public static double ExactRefreshesPerFrame(double sourcePeriod, double refreshPeriod, int factor)
        => sourcePeriod / (factor * refreshPeriod);

    /// <summary>Output frames per second for a mode on a display with the given refresh period.</summary>
    public static double OutputRate(PacingMode mode, double refreshPeriod) => 1.0 / (mode.RefreshesPerFrame * refreshPeriod);
}
