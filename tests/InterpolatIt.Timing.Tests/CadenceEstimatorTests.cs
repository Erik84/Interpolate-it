using InterpolatIt.Timing;

namespace InterpolatIt.Timing.Tests;

public class CadenceEstimatorTests
{
    [Theory]
    [InlineData(23.976, 60)] [InlineData(24, 60)] [InlineData(25, 60)] [InlineData(29.97, 60)]
    [InlineData(30, 60)] [InlineData(50, 60)] [InlineData(60, 60)]
    [InlineData(23.976, 120)] [InlineData(24, 120)] [InlineData(25, 120)] [InlineData(29.97, 120)]
    [InlineData(30, 120)] [InlineData(50, 120)] [InlineData(60, 120)]
    [InlineData(24, 144)] [InlineData(25, 144)] [InlineData(30, 144)] [InlineData(50, 144)] [InlineData(60, 144)]
    public void Locks_and_measures_the_period(double fps, double refresh)
    {
        var estimator = new CadenceEstimator(1.0 / refresh);
        double[] captures = Sim.Captures(fps, refresh, 400);
        var stamps = captures.Select(estimator.Accept).ToArray();

        Assert.Equal(CadenceMode.Locked, estimator.Mode);
        int lockedAt = Array.FindIndex(stamps, s => s.Mode == CadenceMode.Locked);
        Assert.InRange(lockedAt, 1, 12);                                    // locks within the first dozen frames
        Assert.All(stamps.Skip(lockedAt), s => Assert.Equal(CadenceMode.Locked, s.Mode));

        double truePeriod = 1.0 / fps;
        Assert.InRange(estimator.Period, truePeriod * 0.998, truePeriod * 1.002);

        // Idealised stamps are evenly spaced even though the captures are quantised to the refresh grid.
        // Exception: a period that is almost a whole number of refreshes (23.976 / 29.97 fps on 60.00 or
        // 120.00 Hz) moves to the next refresh every few hundred frames; the grid follows in one step.
        int irregular = 0;
        for (int i = lockedAt + 100; i < stamps.Length; i++)
        {
            double step = stamps[i].Time - stamps[i - 1].Time;
            if (Math.Abs(step - truePeriod) > 0.0012) irregular++;
            Assert.InRange(step, truePeriod - 0.0012, truePeriod + 1.0 / refresh + 0.0012);
            Assert.Equal(stamps[i - 1].Tick + 1, stamps[i].Tick);
        }
        Assert.InRange(irregular, 0, 2);
        Assert.Equal(irregular, (int)estimator.PhaseSlips - stamps.Take(lockedAt + 100).Count(_ => false));
    }

    [Theory]
    [InlineData(2)]     // measured after a start-up stall: one interval of 2 refreshes, then a steady 4-4-4 ...
    [InlineData(3)]
    [InlineData(7)]
    public void An_odd_first_interval_does_not_keep_a_regular_source_out_of_lock(int firstIntervalInRefreshes)
    {
        const double P = 1.0 / 120;
        var estimator = new CadenceEstimator(P);
        var captures = new List<double> { 0, firstIntervalInRefreshes * P };
        for (int n = 1; n <= 40; n++) captures.Add(captures[1] + n * 4 * P);
        var stamps = captures.Select(estimator.Accept).ToArray();

        int lockedAt = Array.FindIndex(stamps, s => s.Mode == CadenceMode.Locked);
        Assert.InRange(lockedAt, 1, 12);            // ten regular intervals after the odd one, at the latest
        // (A long first interval can be read as "one frame missing" and lock even earlier, on a slightly-off
        // period that the tracking loop then pulls in.)
        Assert.InRange(estimator.Period, 4 * P * 0.995, 4 * P * 1.005);
    }

    [Fact]
    public void A_source_whose_lock_keeps_falling_apart_is_left_free_for_longer_each_time()
    {
        // A game streamed over a network: steady 60 fps for a while, then a burst of frames one refresh late or
        // early, again and again. Every lock and unlock re-anchors the schedule (a visible hitch), so the
        // estimator must stop believing the steady stretches. (Single late frames do not break a lock - see the
        // next test - so the bursts here are the kind that does.)
        const double P = 1.0 / 120;
        int[] burst = [1, 1, 1, 3, 1, 1, 1, 1, 3, 1, 1, 1];      // a dozen frames that fit no 60 fps grid
        var estimator = new CadenceEstimator(P);
        double time = 0;
        int locks = 0;
        bool wasLocked = false;
        for (int cycle = 0; cycle < 60; cycle++)
        {
            for (int n = 0; n < 50 + burst.Length; n++)
            {
                time += (n < 50 ? 2 : burst[n - 50]) * P;
                bool locked = estimator.Accept(time).Mode == CadenceMode.Locked;
                if (locked && !wasLocked) locks++;
                wasLocked = locked;
            }
        }
        Assert.InRange(locks, 3, 9);                // without the back-off: one lock per cycle, 60 in all
        Assert.InRange(estimator.LockFailures, 5, 7);
    }

    [Theory]
    [InlineData(60, 0.05)]      // a frame one refresh late sits exactly halfway between two ticks here
    [InlineData(60, 0.10)]
    [InlineData(30, 0.10)]
    public void Frames_that_now_and_then_come_a_refresh_late_do_not_break_the_lock(double fps, double lateShare)
    {
        // What a stream over a network looks like: the content keeps its rhythm, single frames are delayed.
        const double P = 1.0 / 120;
        int every = (int)Math.Round(120 / fps);
        var random = new Random(11);
        var estimator = new CadenceEstimator(P);
        var stamps = new List<SourceStamp>();
        int late = 0;
        bool previousLate = false;
        for (int n = 0; n < 3000; n++)
        {
            // Not two in a row: three would be a real move of the grid, which is another test's business.
            bool isLate = n > 40 && !previousLate && random.NextDouble() < lateShare;
            previousLate = isLate;
            if (isLate) late++;
            stamps.Add(estimator.Accept((n * every + (isLate ? 1 : 0)) * P));
        }
        Assert.True(late > 100);
        int lockedAt = stamps.FindIndex(s => s.Mode == CadenceMode.Locked);
        Assert.InRange(lockedAt, 1, 12);
        Assert.All(stamps.Skip(lockedAt), s => Assert.Equal(CadenceMode.Locked, s.Mode));
        Assert.Equal(0, estimator.LockFailures);
        // The late frames get the time they should have had: the stamps stay evenly spaced.
        for (int i = lockedAt + 20; i < stamps.Count; i++)
        {
            Assert.Equal(stamps[i - 1].Tick + 1, stamps[i].Tick);
            Assert.InRange(stamps[i].Time - stamps[i - 1].Time, every * P - 0.0015, every * P + 0.0015);
        }
    }

    [Theory]
    [InlineData(60, 40)]        // on the old grid every other frame of the new rate looks like a late one
    [InlineData(60, 30)]        // ... or like a 60 fps source that drops every other frame
    [InlineData(30, 60)]
    [InlineData(40, 60)]
    public void A_change_of_rate_is_noticed_and_followed(double before, double after)
    {
        const double P = 1.0 / 120;
        var estimator = new CadenceEstimator(P);
        double time = 0;
        for (int n = 0; n < 200; n++) { time += Math.Round(120 / before) * P; estimator.Accept(time); }
        Assert.Equal(CadenceMode.Locked, estimator.Mode);

        int relockedAfter = -1;
        for (int n = 0; n < 200; n++)
        {
            time += Math.Round(120 / after) * P;
            var stamp = estimator.Accept(time);
            if (relockedAfter < 0 && stamp.Mode == CadenceMode.Locked && Math.Abs(stamp.Period * after - 1) < 0.02) relockedAfter = n;
        }
        Assert.InRange(relockedAfter, 1, 70);       // noticed within ~16 frames, 32 clean intervals to believe the new rate
        Assert.InRange(estimator.Period * after, 0.99, 1.01);
    }

    [Theory]
    [InlineData(60, 1)]     // measured: after a start-up stall the first interval was 1 refresh, then 2-2-2 ...
    [InlineData(60, 3)]
    [InlineData(30, 2)]
    [InlineData(30, 6)]
    public void After_a_pause_an_odd_first_interval_never_gives_a_lock_on_a_wrong_period(double fps, int oddInterval)
    {
        const double P = 1.0 / 120;
        int every = (int)Math.Round(120 / fps);
        var estimator = new CadenceEstimator(P);
        double time = 0;
        for (int n = 0; n < 40; n++) { time += every * P; estimator.Accept(time); }
        Assert.Equal(CadenceMode.Locked, estimator.Mode);

        time += 1.5;                                        // a stall: frames stop for a while
        estimator.Accept(time);
        int relockedAfter = -1;
        for (int n = 0; n < 40; n++)
        {
            time += (n == 0 ? oddInterval : every) * P;
            var stamp = estimator.Accept(time);
            if (stamp.Mode != CadenceMode.Locked) continue;
            Assert.InRange(stamp.Period, every * P * 0.97, every * P * 1.03);    // never a period that is not the source's
            if (relockedAfter < 0) relockedAfter = n;
        }
        Assert.InRange(relockedAfter, 1, 12);
        Assert.Equal(0, estimator.LockFailures);
    }

    [Fact]
    public void A_single_change_of_rate_is_followed_promptly()
    {
        const double P = 1.0 / 120;
        var estimator = new CadenceEstimator(P);
        double time = 0;
        for (int n = 0; n < 100; n++) { time += 4 * P; estimator.Accept(time); }       // 30 fps
        Assert.Equal(CadenceMode.Locked, estimator.Mode);
        int lockedAfter = -1;
        for (int n = 0; n < 100; n++)                                                    // ... becomes 60 fps
        {
            time += 2 * P;
            var stamp = estimator.Accept(time);
            if (lockedAfter < 0 && stamp.Mode == CadenceMode.Locked && Math.Abs(stamp.Period - 2 * P) < 0.1 * P) lockedAfter = n;
        }
        Assert.InRange(lockedAfter, 1, 45);
        Assert.InRange(estimator.Period, 2 * P * 0.99, 2 * P * 1.01);
    }

    [Fact]
    public void A_period_just_off_a_whole_number_of_refreshes_stays_locked_across_refresh_slips()
    {
        // 29.97 fps on exactly 120 Hz: 4.004 refreshes per frame, so one refresh is gained every ~250 frames.
        var estimator = new CadenceEstimator(1.0 / 120);
        var stamps = Sim.Captures(29.97, 120, 1200).Select(estimator.Accept).ToArray();
        int lockedAt = Array.FindIndex(stamps, s => s.Mode == CadenceMode.Locked);
        Assert.All(stamps.Skip(lockedAt), s => Assert.Equal(CadenceMode.Locked, s.Mode));
        Assert.All(stamps.Skip(lockedAt + 1), s => Assert.False(s.Discontinuity));
        Assert.InRange(estimator.PhaseSlips, 3, 6);
    }

    [Fact]
    public void A_dropped_frame_advances_the_tick_by_two_and_keeps_the_lock()
    {
        var estimator = new CadenceEstimator(1.0 / 120);
        double[] captures = Sim.Captures(30, 120, 80);
        SourceStamp previous = default, afterGap = default;
        for (int n = 0; n < captures.Length; n++)
        {
            if (n == 50) continue;                      // frame 50 never arrives
            var stamp = estimator.Accept(captures[n]);
            if (n == 49) previous = stamp;
            if (n == 51) afterGap = stamp;
        }
        Assert.Equal(CadenceMode.Locked, afterGap.Mode);
        Assert.False(afterGap.Discontinuity);
        Assert.Equal(previous.Tick + 2, afterGap.Tick);
        Assert.InRange(afterGap.Time - previous.Time, 2.0 / 30 - 0.001, 2.0 / 30 + 0.001);
        Assert.Equal(CadenceMode.Locked, estimator.Mode);
    }

    [Fact]
    public void A_dropped_frame_during_the_search_still_locks_on_the_true_period()
    {
        var estimator = new CadenceEstimator(1.0 / 120);
        double[] captures = Sim.Captures(30, 120, 40);
        for (int n = 0; n < captures.Length; n++)
            if (n != 5) estimator.Accept(captures[n]);
        Assert.Equal(CadenceMode.Locked, estimator.Mode);
        Assert.InRange(estimator.Period, 1.0 / 30 * 0.995, 1.0 / 30 * 1.005);
    }

    [Fact]
    public void Frames_between_two_expected_frames_are_off_grid()
    {
        var estimator = new CadenceEstimator(1.0 / 120);
        double[] captures = Sim.Captures(30, 120, 40);
        foreach (double c in captures) estimator.Accept(c);
        double last = captures[^1], period = 1.0 / 30;

        Assert.Equal(GridClass.OnGrid, estimator.Classify(last + period));
        Assert.Equal(GridClass.OnGrid, estimator.Classify(last + 2 * period));          // after a dropped frame
        Assert.Equal(GridClass.OffGrid, estimator.Classify(last + period / 2));         // e.g. a caret blink
        Assert.Equal(GridClass.OffGrid, estimator.Classify(last + 1.0 / 120));          // one refresh later
        Assert.Equal(GridClass.Unknown, new CadenceEstimator(1.0 / 120).Classify(1.0));
    }

    [Fact]
    public void Irregular_frame_times_do_not_lock()
    {
        // A game whose frame time wanders between 12 and 20 ms, captured on a 120 Hz desktop.
        var random = new Random(1234);
        var presents = new List<double>();
        double t = 0.0003;
        for (int i = 0; i < 3000; i++)
        {
            presents.Add(t);
            t += 0.012 + random.NextDouble() * 0.008;
        }
        var estimator = new CadenceEstimator(1.0 / 120);
        int locked = 0, reanchors = 0;
        double previous = double.NegativeInfinity;
        foreach (double c in Sim.Quantise(presents, 120))
        {
            if (c <= previous) continue;                // two presents inside one refresh: only one is captured
            previous = c;
            var stamp = estimator.Accept(c);
            if (stamp.Mode == CadenceMode.Locked) locked++;
            if (stamp.Discontinuity) reanchors++;
        }
        // What matters is that such a source does not flap: every lock and unlock re-anchors the schedule.
        // White-noise frame times are the worst case (real games drift more smoothly); a brief false lock is
        // tolerated at most about once per ten seconds, i.e. one re-anchor per five seconds of this 48 s run.
        double seconds = presents[^1];
        Assert.True(locked < 0.05 * presents.Count && reanchors <= seconds / 5,
            $"locked on {locked} of {presents.Count} frames, {reanchors} discontinuities in {seconds:F0} s");
    }

    [Fact]
    public void A_pause_is_a_discontinuity_and_resuming_relocks_quickly()
    {
        var estimator = new CadenceEstimator(1.0 / 120);
        double[] before = Sim.Captures(30, 120, 60);
        foreach (double c in before) estimator.Accept(c);
        Assert.Equal(CadenceMode.Locked, estimator.Mode);

        double[] after = Sim.Captures(30, 120, 30, phase: before[^1] + 2.0);            // two seconds paused
        var stamps = after.Select(estimator.Accept).ToArray();
        Assert.True(stamps[0].Discontinuity);
        Assert.Equal(CadenceMode.Free, stamps[0].Mode);
        int relockedAt = Array.FindIndex(stamps, s => s.Mode == CadenceMode.Locked);
        Assert.InRange(relockedAt, 1, 4);
        Assert.InRange(estimator.Period, 1.0 / 30 * 0.99, 1.0 / 30 * 1.01);
    }

    [Fact]
    public void A_rate_change_is_noticed_through_dropped_off_grid_frames()
    {
        // 30 fps, then the same content at 60 fps. With the gate, every second 60 fps frame is off-grid and is
        // dropped; the estimator must notice that this has become the rule and search again.
        var estimator = new CadenceEstimator(1.0 / 120);
        double[] slow = Sim.Captures(30, 120, 60);
        double[] fast = Sim.Captures(60, 120, 200, phase: slow[^1] + 1.0 / 30);
        int accepted = 0;
        foreach (double c in slow.Concat(fast))
        {
            if (estimator.Classify(c) == GridClass.OffGrid) { estimator.NoteDropped(); continue; }
            estimator.Accept(c);
            accepted++;
        }
        Assert.Equal(CadenceMode.Locked, estimator.Mode);
        Assert.InRange(estimator.Period, 1.0 / 60 * 0.99, 1.0 / 60 * 1.01);
        Assert.True(accepted > 60 + 150, $"only {accepted} frames accepted");
    }
}
