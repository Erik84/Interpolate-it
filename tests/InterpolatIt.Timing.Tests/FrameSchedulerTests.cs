using InterpolatIt.Timing;

namespace InterpolatIt.Timing.Tests;

public class FrameSchedulerTests
{
    /// <summary>The part of the plan produced after the last re-anchor (i.e. in steady state).</summary>
    private static List<PlannedFrame> Steady(Sim.Run run)
    {
        int last = run.Frames.FindLastIndex(f => f.Stamp.Discontinuity);
        int skip = last <= 0 ? 0 : run.PlanCountAfterFrame[last - 1];
        return run.Plan.Skip(skip).ToList();
    }

    private static void AssertStrictlyIncreasing(IReadOnlyList<PlannedFrame> plan)
    {
        for (int i = 1; i < plan.Count; i++)
            Assert.True(plan[i].Refresh > plan[i - 1].Refresh, $"refresh {plan[i].Refresh} after {plan[i - 1].Refresh} at {i}");
    }

    [Theory]
    [InlineData(30, 120, 2, 2)]
    [InlineData(30, 120, 4, 1)]
    [InlineData(60, 120, 2, 1)]
    [InlineData(30, 60, 2, 1)]
    public void Stepped_mode_gives_exact_timesteps_on_evenly_spaced_slots(double fps, double refresh, int factor, int m)
    {
        var run = Sim.Schedule(Sim.Captures(fps, refresh, 300), refresh, factor);
        Assert.Equal(new PacingMode(PacingKind.Stepped, factor, m), run.Scheduler.Mode);
        var plan = Steady(run);
        Assert.True(plan.Count > 200 * factor);

        double step = 1.0 / fps / factor;
        for (int i = 1; i < plan.Count; i++)
        {
            Assert.Equal(m, plan[i].Refresh - plan[i - 1].Refresh);                         // every slot filled, none doubled
            Assert.InRange(plan[i].ContentTime - plan[i - 1].ContentTime, step - 0.0004, step + 0.0004);
        }

        // One real frame followed by N-1 interpolations at exactly k/N.
        int firstReal = plan.FindIndex(f => f.Kind == PlannedKind.Real);
        for (int i = firstReal; i + factor <= plan.Count; i += factor)
        {
            Assert.Equal(PlannedKind.Real, plan[i].Kind);
            for (int k = 1; k < factor; k++)
            {
                var f = plan[i + k];
                Assert.Equal(PlannedKind.Interpolated, f.Kind);
                Assert.Equal((float)k / factor, f.T, 5);
                Assert.Equal(plan[i].FrameA, f.FrameA);                                     // between this real frame ...
                Assert.Equal(plan[i].FrameA + 1, f.FrameB);                                 // ... and the next one
            }
        }
    }

    [Theory]
    [InlineData(25, 120, 2, 2)]     // 60 fps out
    [InlineData(25, 120, 4, 1)]     // 120 fps out
    [InlineData(24, 120, 2, 2)]     // 60 fps out
    [InlineData(24, 60, 2, 1)]      // 60 fps out
    [InlineData(50, 120, 2, 1)]     // 120 fps out
    [InlineData(30, 144, 2, 2)]     // 72 fps out
    public void Continuous_mode_advances_content_time_evenly(double fps, double refresh, int factor, int m)
    {
        var run = Sim.Schedule(Sim.Captures(fps, refresh, 400), refresh, factor);
        Assert.Equal(new PacingMode(PacingKind.Continuous, factor, m), run.Scheduler.Mode);
        var plan = Steady(run);
        Assert.True(plan.Count > 200);

        double slot = m / refresh;
        double period = 1.0 / fps;
        double snap = run.Scheduler.Epsilon * period + 0.0005;      // a slot this close to a real frame shows that frame
        for (int i = 1; i < plan.Count; i++)
        {
            Assert.Equal(m, plan[i].Refresh - plan[i - 1].Refresh);
            double step = plan[i].ContentTime - plan[i - 1].ContentTime;
            Assert.InRange(step, slot - snap, slot + snap);
            if (plan[i].Kind == PlannedKind.Interpolated)
            {
                Assert.InRange(plan[i].T, (float)run.Scheduler.Epsilon, 1f - (float)run.Scheduler.Epsilon);
                Assert.Equal(plan[i].FrameA + 1, plan[i].FrameB);
            }
        }
        Assert.Contains(plan, f => f.Kind == PlannedKind.Interpolated);
        // The slots are anchored on the frames, so the real frames themselves are part of the output.
        Assert.Contains(plan, f => f.Kind == PlannedKind.Real);
    }

    [Theory]
    [InlineData(24, 120, 4, 5, 1)]      // "x4" of 24 fps on 120 Hz is exactly x5
    [InlineData(30, 120, 3, 4, 1)]      // "x3" of 30 fps on 120 Hz is exactly x4
    [InlineData(60, 120, 4, 2, 1)]      // "x4" of 60 fps on 120 Hz is exactly x2
    public void A_whole_number_of_output_frames_per_source_frame_is_stepped_even_if_not_the_requested_one(
        double fps, double refresh, int requested, int actual, int m)
    {
        var run = Sim.Schedule(Sim.Captures(fps, refresh, 300), refresh, requested);
        Assert.Equal(new PacingMode(PacingKind.Stepped, actual, m), run.Scheduler.Mode);
        var plan = Steady(run);
        int firstReal = plan.FindIndex(f => f.Kind == PlannedKind.Real);
        for (int i = firstReal; i + actual <= plan.Count; i += actual)
        {
            Assert.Equal(PlannedKind.Real, plan[i].Kind);
            for (int k = 1; k < actual; k++)
            {
                Assert.Equal(PlannedKind.Interpolated, plan[i + k].Kind);
                Assert.Equal((float)k / actual, plan[i + k].T, 5);
                Assert.Equal(m, plan[i + k].Refresh - plan[i + k - 1].Refresh);
            }
        }
    }

    [Theory]
    [InlineData(30, 120, 2)]
    [InlineData(30, 120, 4)]
    [InlineData(60, 120, 2)]
    [InlineData(25, 120, 2)]
    [InlineData(24, 120, 4)]
    public void Frames_are_ready_before_their_deadline_when_generation_fits(double fps, double refresh, int factor)
    {
        const double capture = 0.004, generation = 0.006;
        var run = Sim.Schedule(Sim.Captures(fps, refresh, 300), refresh, factor, capture, generation);
        // The guarantee holds once the cadence is known; the pipeline shows nothing before that.
        int anchor = run.Frames.FindLastIndex(f => f.Stamp.Discontinuity);
        int start = anchor <= 0 ? 0 : run.PlanCountAfterFrame[anchor - 1];
        Assert.InRange(anchor, 0, 12);
        for (int i = Math.Max(anchor, 0); i < run.Frames.Count; i++)
        {
            double now = run.Frames[i].Now;
            int produced = 0;
            for (int j = start; j < run.PlanCountAfterFrame[i]; j++)
            {
                var f = run.Plan[j];
                double ready = f.Kind == PlannedKind.Interpolated ? now + generation * ++produced : now;
                Assert.True(ready <= run.Scheduler.Deadline(f.Refresh) + 1e-9,
                    $"frame for refresh {f.Refresh} ready {(ready - run.Scheduler.Deadline(f.Refresh)) * 1000:F2} ms late (source frame {i})");
            }
            start = run.PlanCountAfterFrame[i];
        }
    }

    [Fact]
    public void Stepped_delay_stays_near_its_lower_bound()
    {
        const double capture = 0.004, generation = 0.006, refresh = 120, fps = 30;
        var run = Sim.Schedule(Sim.Captures(fps, refresh, 200), refresh, 2, capture, generation);
        double p = 1 / refresh, lead = run.Scheduler.Lead;
        double lower = (1 / fps) * 0.5 + capture + generation + lead;
        Assert.InRange(run.Scheduler.Delay, lower - 0.001, lower + 2 * p);
        Assert.InRange(lead, 1.49 * p, 1.51 * p);                   // lag 2, present at mid-interval
    }

    [Fact]
    public void Nothing_is_interpolated_across_a_scene_cut()
    {
        var run = Sim.Schedule(Sim.Captures(30, 120, 200), 120, 4, cutBefore: n => n == 100);
        Assert.DoesNotContain(run.Plan, f => f.Kind == PlannedKind.Interpolated && f.FrameB == 100);
        Assert.Contains(run.Plan, f => f.Kind == PlannedKind.Real && f.FrameA == 100);
        Assert.Equal(3, run.Plan.Count(f => f.Kind == PlannedKind.Interpolated && f.FrameB == 101));
        AssertStrictlyIncreasing(run.Plan);

        var continuous = Sim.Schedule(Sim.Captures(25, 120, 200), 120, 2, cutBefore: n => n == 100);
        Assert.DoesNotContain(continuous.Plan, f => f.Kind == PlannedKind.Interpolated && f.FrameB == 100);
        AssertStrictlyIncreasing(continuous.Plan);
    }

    [Fact]
    public void A_dropped_source_frame_holds_then_interpolates_the_last_interval_only()
    {
        double[] captures = Sim.Captures(30, 120, 200).Where((_, n) => n != 100).ToArray();
        var run = Sim.Schedule(captures, 120, 4);
        Assert.Equal(CadenceMode.Locked, run.Estimator.Mode);
        AssertStrictlyIncreasing(run.Plan);

        // Ids are positions in `captures`: 99 is the frame before the gap, 100 the first one after it.
        var across = run.Plan.Where(f => f.Kind == PlannedKind.Interpolated && f.FrameA == 99 && f.FrameB == 100).ToList();
        Assert.Equal(3, across.Count);
        var real99 = run.Plan.Single(f => f.Kind == PlannedKind.Real && f.FrameA == 99);
        Assert.Equal(real99.Refresh + 4 + 1, across[0].Refresh);     // one source interval (4 refreshes) of hold first
    }

    [Theory]
    [InlineData(30, 120, 2)]
    [InlineData(30, 120, 4)]
    public void A_lag_change_costs_at_most_one_slot(double fps, double refresh, int factor)
    {
        var run = Sim.Schedule(Sim.Captures(fps, refresh, 300), refresh, factor,
            beforeFrame: (s, n, _) => { if (n == 150) s.OnLagChanged(1); });
        Assert.Equal(1, run.Scheduler.Lag);
        var plan = Steady(run);
        AssertStrictlyIncreasing(plan);
        int m = run.Scheduler.Mode.RefreshesPerFrame;
        double step = 1.0 / fps / factor;
        int shortened = 0, skipped = 0;
        for (int i = 1; i < plan.Count; i++)
        {
            if (plan[i].Refresh - plan[i - 1].Refresh != m) shortened++;                        // slot moved one refresh earlier
            if (plan[i].ContentTime - plan[i - 1].ContentTime > 1.5 * step) skipped++;          // its slot was taken: dropped
        }
        // Becoming visible one refresh earlier either shortens one slot (m > 1) or costs one frame (m = 1).
        Assert.Equal(1, shortened + skipped);
    }

    [Fact]
    public void Late_frames_add_a_refresh_of_delay_and_spare_slack_removes_it()
    {
        var clock = new VBlankClock(1.0 / 120);
        clock.Update(0, 0.0);
        var estimator = new CadenceEstimator(1.0 / 120);
        var scheduler = new FrameScheduler(clock, 2);
        var plan = new List<PlannedFrame>();
        double[] captures = Sim.Captures(30, 120, 60);
        for (int n = 0; n < captures.Length; n++)
            scheduler.OnSourceFrame(n, estimator.Accept(captures[n]), false, captures[n] + 0.004, 0.006, estimator.MedianInterval, plan);

        // Frames finishing at time t with (about) the given slack before their deadline.
        void Feed(double from, double to, double slack)
        {
            for (double t = from; t <= to + 1e-9; t += 0.05)
                scheduler.OnFrameReady((long)Math.Round((t + slack + scheduler.Lead) / clock.Period), t);
        }

        double now = captures[^1];
        double before = scheduler.Delay;
        Assert.Equal(0, scheduler.Control(now));                    // no measurements yet: leave it alone
        scheduler.OnMiss(now);
        scheduler.OnMiss(now + 0.1);
        Assert.Equal(+1, scheduler.Control(now + 0.2));
        Assert.Equal(0, scheduler.Control(now + 0.3));              // measurements were cleared by the slip

        // The next frame is planned one refresh later than it would have been.
        double c = captures[^1] + 1.0 / 30;
        scheduler.OnSourceFrame(captures.Length, estimator.Accept(c), false, c + 0.004, 0.006, estimator.MedianInterval, plan);
        Assert.InRange(scheduler.Delay - before, 1.0 / 120 - 1e-6, 1.0 / 120 + 0.0005);

        // Generous slack, but not yet for a whole calm period: keep the delay.
        const double calm = FrameScheduler.InitialCalmPeriod;
        double start = now + 0.5;
        Feed(start, start + calm - 0.5, 0.020);
        Assert.Equal(0, scheduler.Control(start + calm - 0.5));
        // A whole calm period of it: give the refresh back.
        Feed(start + calm - 0.45, start + calm + 0.1, 0.020);
        Assert.Equal(-1, scheduler.Control(start + calm + 0.1));
        Assert.Equal(calm, scheduler.CalmPeriod);

        // It was needed after all: take it again, and be twice as patient before the next attempt.
        double again = start + calm + 2;
        scheduler.OnMiss(again);
        scheduler.OnMiss(again + 0.1);
        Assert.Equal(+1, scheduler.Control(again + 0.2));
        Assert.Equal(2 * calm, scheduler.CalmPeriod);
        Feed(again + 0.3, again + calm + 0.5, 0.020);
        Assert.Equal(0, scheduler.Control(again + calm + 0.5));
        Feed(again + calm + 0.55, again + 2 * calm + 0.4, 0.020);
        Assert.Equal(-1, scheduler.Control(again + 2 * calm + 0.4));
    }

    [Fact]
    public void A_tie_between_two_output_rates_goes_to_the_faster_one_from_either_side()
    {
        // A free-running source at x2 on 120 Hz: 60 fps -> 120 fps output; 30 fps -> 60 fps output; and 40 fps,
        // which is as far from 60 as from 120, must end up at 120 whether it is reached from above or from below.
        var clock = new VBlankClock(1.0 / 120);
        clock.Update(0, 0.0);
        var scheduler = new FrameScheduler(clock, 2);
        var plan = new List<PlannedFrame>();
        double time = 0;
        long id = 0;
        void Feed(double fps, int frames)
        {
            for (int n = 0; n < frames; n++, id++)
            {
                time += 1.0 / fps;
                scheduler.OnSourceFrame(id, new SourceStamp(time, id, CadenceMode.Free, double.NaN, id == 0), false, time + 0.004, 0.006, 1.0 / fps, plan);
            }
        }
        Feed(60, 30);
        Assert.Equal(1, scheduler.Mode.RefreshesPerFrame);
        Feed(40, 30);
        Assert.Equal(1, scheduler.Mode.RefreshesPerFrame);      // from above: stays at 120 fps
        Feed(30, 30);
        Assert.Equal(2, scheduler.Mode.RefreshesPerFrame);      // 30 fps x2 really is 60 fps
        Feed(40, 30);
        Assert.Equal(1, scheduler.Mode.RefreshesPerFrame);      // from below: back to 120 fps
    }

    [Fact]
    public void When_the_source_goes_quiet_the_newest_real_frame_is_put_on_screen()
    {
        // 25 fps at 60 fps output: the last planned picture is usually an in-between one.
        var run = Sim.Schedule(Sim.Captures(25, 120, 83), 120, 2);
        Assert.Equal(PacingKind.Continuous, run.Scheduler.Mode.Kind);
        var (lastId, lastStamp, lastNow) = run.Frames[^1];
        var before = run.Plan[^1];
        Assert.False(before.Kind == PlannedKind.Real && before.FrameA == lastId);

        Assert.True(run.Scheduler.Flush(lastNow + 0.060, out PlannedFrame flushed));
        Assert.Equal(PlannedKind.Real, flushed.Kind);
        Assert.Equal(lastId, flushed.FrameA);
        Assert.True(flushed.Refresh > before.Refresh);
        Assert.True(run.Scheduler.Deadline(flushed.Refresh) >= lastNow + 0.060);    // it can still be presented in time
        Assert.False(run.Scheduler.Flush(lastNow + 0.070, out _));                  // once is enough

        // An exact cadence always ends on the real frame: nothing to do.
        var stepped = Sim.Schedule(Sim.Captures(30, 120, 60), 120, 2);
        Assert.False(stepped.Scheduler.Flush(stepped.Frames[^1].Now + 0.060, out _));
    }

    [Fact]
    public void The_delay_is_never_pushed_beyond_its_limit()
    {
        var clock = new VBlankClock(1.0 / 120);
        clock.Update(0, 0.0);
        var estimator = new CadenceEstimator(1.0 / 120);
        var scheduler = new FrameScheduler(clock, 2);
        var plan = new List<PlannedFrame>();
        double[] captures = Sim.Captures(30, 120, 400);
        int slips = 0;
        for (int n = 0; n < captures.Length; n++)
        {
            scheduler.OnSourceFrame(n, estimator.Accept(captures[n]), false, captures[n] + 0.004, 0.006, estimator.MedianInterval, plan);
            if (n <= 20) continue;
            scheduler.OnMiss(captures[n]);                          // frames keep coming late, whatever the delay
            scheduler.OnMiss(captures[n] + 0.001);
            slips += scheduler.Control(captures[n] + 0.002);
        }
        Assert.InRange(slips, 3, 15);
        Assert.True(scheduler.Delay <= scheduler.MaxDelay + 1e-6, $"delay {scheduler.Delay * 1000:F1} ms");
    }

    [Fact]
    public void The_slot_spacing_does_not_flap_when_the_measured_periods_wobble_around_a_tie()
    {
        // 24 fps "x2" on 120 Hz is 2.5 refreshes per output frame: exactly between 60 and 40 fps output. The
        // measured source period wobbles here by more than the tie band, so only the hysteresis keeps it steady.
        var clock = new VBlankClock(1.0 / 120);
        clock.Update(0, 0.0);
        var scheduler = new FrameScheduler(clock, 2);
        var plan = new List<PlannedFrame>();
        for (int n = 0; n < 240; n++)
        {
            double period = 1.0 / 24 * (1 + (n % 2 == 0 ? -0.012 : 0.012));    // 2.47 / 2.53 refreshes per output frame
            var stamp = new SourceStamp(n / 24.0, n, CadenceMode.Locked, period, n == 0);
            scheduler.OnSourceFrame(n, stamp, false, n / 24.0 + 0.004, 0.006, 1.0 / 24, plan);
        }
        Assert.Equal(2, scheduler.Mode.RefreshesPerFrame);
        Assert.Equal(1, scheduler.ReanchorCount);
    }

    [Fact]
    public void A_free_source_gets_a_monotone_plan_with_valid_timesteps()
    {
        var random = new Random(7);
        var presents = new List<double>();
        double t = 0.0003;
        for (int i = 0; i < 1500; i++) { presents.Add(t); t += 0.012 + random.NextDouble() * 0.008; }
        double[] captures = Sim.Quantise(presents, 120).Distinct().ToArray();

        var run = Sim.Schedule(captures, 120, 2);
        Assert.Equal(PacingKind.Continuous, run.Scheduler.Mode.Kind);
        AssertStrictlyIncreasing(run.Plan);
        Assert.True(run.Scheduler.ReanchorCount < 30, $"{run.Scheduler.ReanchorCount} re-anchors");
        for (int i = 1; i < run.Plan.Count; i++)
        {
            var f = run.Plan[i];
            if (f.Kind == PlannedKind.Interpolated) Assert.InRange(f.T, 0.0f, 1.0f);
        }
        // Content time never runs backwards between re-anchors.
        var steady = Steady(run);
        for (int i = 1; i < steady.Count; i++)
            Assert.True(steady[i].ContentTime >= steady[i - 1].ContentTime - 1e-9);
    }

    [Fact]
    public void The_vblank_clock_measures_the_refresh_period_and_converts_both_ways()
    {
        var clock = new VBlankClock(1.0 / 120);
        double actual = 1.0 / 119.88;
        for (long v = 0; v <= 600; v += 3) clock.Update(1000 + v, 5.0 + v * actual);
        Assert.InRange(clock.Period, actual * 0.9999, actual * 1.0001);
        Assert.Equal(1700, clock.CountAt(clock.TimeOf(1700) + actual * 0.4));
        Assert.Equal(1701, clock.FirstAtOrAfter(clock.TimeOf(1700) + actual * 0.4));
        Assert.Equal(1700, clock.FirstAtOrAfter(clock.TimeOf(1700)));
    }
}
