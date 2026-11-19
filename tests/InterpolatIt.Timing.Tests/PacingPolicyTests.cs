using InterpolatIt.Timing;

namespace InterpolatIt.Timing.Tests;

public class PacingPolicyTests
{
    [Theory]
    // source fps, display Hz, requested factor -> kind, refreshes per output frame, output frames per source frame
    [InlineData(30, 120, 2, PacingKind.Stepped, 2, 2)]          // 60 fps, exact x2
    [InlineData(30, 120, 4, PacingKind.Stepped, 1, 4)]          // 120 fps, exact x4
    [InlineData(60, 120, 2, PacingKind.Stepped, 1, 2)]          // 120 fps, exact x2
    [InlineData(30, 60, 2, PacingKind.Stepped, 1, 2)]
    [InlineData(29.97, 119.88, 4, PacingKind.Stepped, 1, 4)]    // NTSC rates match exactly
    [InlineData(29.97, 120, 4, PacingKind.Stepped, 1, 4)]       // one slot slip every 8 s: still stepped
    [InlineData(30, 120, 3, PacingKind.Stepped, 1, 4)]          // 90 fps does not fit 120 Hz: 120 fps, which is exactly x4
    [InlineData(24, 120, 4, PacingKind.Stepped, 1, 5)]          // 96 -> 120 fps, which is exactly x5
    [InlineData(60, 120, 4, PacingKind.Stepped, 1, 2)]          // the display cannot show 240 fps: exactly x2
    [InlineData(24, 144, 4, PacingKind.Stepped, 1, 6)]          // 96 fps is midway between 72 and 144: the faster one, exactly x6
    [InlineData(25, 120, 2, PacingKind.Continuous, 2, 2)]       // 50 -> 60 fps (2.4 per source frame)
    [InlineData(25, 120, 4, PacingKind.Continuous, 1, 4)]       // 100 -> 120 fps (4.8)
    [InlineData(24, 120, 2, PacingKind.Continuous, 2, 2)]       // 48 -> 60 fps (2.5; the tie goes to the faster rate)
    [InlineData(50, 120, 2, PacingKind.Continuous, 1, 2)]       // 100 -> 120 fps (2.4)
    [InlineData(24, 60, 2, PacingKind.Continuous, 1, 2)]        // 48 -> 60 fps (2.5)
    [InlineData(30, 144, 2, PacingKind.Continuous, 2, 2)]       // 60 -> 72 fps (2.4)
    public void Selects_the_nearest_display_synchronous_rate(double fps, double refresh, int requested, PacingKind kind, int m, int factor)
    {
        var mode = PacingPolicy.Select(1.0 / fps, 1.0 / refresh, requested, locked: true);
        Assert.Equal(kind, mode.Kind);
        Assert.Equal(m, mode.RefreshesPerFrame);
        Assert.Equal(factor, mode.Factor);
    }

    [Theory]
    [InlineData(119.92)]
    [InlineData(120.00)]
    [InlineData(120.08)]
    public void A_tie_goes_to_the_faster_rate_whichever_side_the_measured_refresh_rate_falls_on(double measuredRefresh)
    {
        // 24 fps "x2": 48 fps is as far from 40 as from 60. A display measured a fraction off 120 Hz must not
        // tip that to the slower side.
        var mode = PacingPolicy.Select(1.0 / 24, 1.0 / measuredRefresh, 2, locked: true);
        Assert.Equal(2, mode.RefreshesPerFrame);
        Assert.Equal(PacingKind.Continuous, mode.Kind);
    }

    [Fact]
    public void A_given_slot_spacing_is_kept()
    {
        var mode = PacingPolicy.Select(1.0 / 24, 1.0 / 120, 2, locked: true, refreshesPerFrame: 3);
        Assert.Equal(new PacingMode(PacingKind.Continuous, 2, 3), mode);
        // ... and still recognised as exact when it is: 30 fps shown every 2nd refresh is exactly x2.
        Assert.Equal(new PacingMode(PacingKind.Stepped, 2, 2), PacingPolicy.Select(1.0 / 30, 1.0 / 120, 4, locked: true, refreshesPerFrame: 2));
    }

    [Fact]
    public void A_free_source_is_always_continuous()
    {
        var mode = PacingPolicy.Select(1.0 / 30, 1.0 / 120, 2, locked: false);
        Assert.Equal(PacingKind.Continuous, mode.Kind);
        Assert.Equal(2, mode.RefreshesPerFrame);
    }

    [Fact]
    public void An_unknown_period_falls_back_to_every_refresh()
    {
        var mode = PacingPolicy.Select(double.NaN, 1.0 / 120, 2, locked: false);
        Assert.Equal(new PacingMode(PacingKind.Continuous, 2, 1), mode);
    }
}
