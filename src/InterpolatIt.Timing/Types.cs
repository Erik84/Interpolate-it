namespace InterpolatIt.Timing;

// All times are seconds on one monotonic clock (QPC converted to double); refreshes are counted display vblanks.

public enum CadenceMode
{
    /// <summary>No stable frame period found: raw capture timestamps are used (games with varying frame time).</summary>
    Free,

    /// <summary>The source has a stable period: frames get idealised, evenly spaced timestamps.</summary>
    Locked,
}

public enum GridClass
{
    /// <summary>The cadence is not locked, so there is no grid to compare with.</summary>
    Unknown,

    /// <summary>The frame arrived where a frame of the locked cadence is expected.</summary>
    OnGrid,

    /// <summary>The frame arrived between two expected frames (e.g. a caret blink over a 30 fps video).</summary>
    OffGrid,
}

/// <summary>Timing of one accepted source frame.</summary>
/// <param name="Time">Idealised (locked) or raw (free) timestamp of the frame.</param>
/// <param name="Tick">Frame counter on the source timeline; advances by more than one across dropped frames.</param>
/// <param name="Period">Locked source period, NaN when free.</param>
/// <param name="Discontinuity">First frame, pause/seek, or a change of cadence: the scheduler must re-anchor.</param>
public readonly record struct SourceStamp(double Time, long Tick, CadenceMode Mode, double Period, bool Discontinuity);

public enum PacingKind
{
    /// <summary>Source rate x factor equals the output rate: exact timesteps k/N, counted, never recomputed from time.</summary>
    Stepped,

    /// <summary>Otherwise: every output slot shows the content time it stands for, with an arbitrary timestep.</summary>
    Continuous,
}

/// <param name="Factor">Stepped: the exact number of output frames per source frame (may differ from the
/// requested multiplier when another whole number fits the display). Continuous: the requested multiplier.</param>
/// <param name="RefreshesPerFrame">m: an output frame is shown every m-th display refresh.</param>
public readonly record struct PacingMode(PacingKind Kind, int Factor, int RefreshesPerFrame);

public enum PlannedKind { Real, Interpolated }

/// <summary>One output frame assigned to the display refresh on which it must become visible.</summary>
/// <param name="Refresh">Refresh count (vblank index) the frame is planned for.</param>
/// <param name="FrameA">Older source frame id (the frame itself when <see cref="PlannedKind.Real"/>).</param>
/// <param name="FrameB">Newer source frame id (equal to <paramref name="FrameA"/> when real).</param>
/// <param name="T">Timestep in (0, 1) between A and B; 0 for a real frame.</param>
/// <param name="ContentTime">Source-timeline time this output frame represents (for logging and tests).</param>
public readonly record struct PlannedFrame(long Refresh, PlannedKind Kind, long FrameA, long FrameB, float T, double ContentTime);
