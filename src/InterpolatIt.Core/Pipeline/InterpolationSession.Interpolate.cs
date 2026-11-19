using InterpolatIt.Core.Capture;
using InterpolatIt.Core.Gpu;
using InterpolatIt.Core.Inference;
using InterpolatIt.Core.Native;
using InterpolatIt.Core.Util;
using InterpolatIt.Timing;

namespace InterpolatIt.Core.Pipeline;

public sealed partial class InterpolationSession
{
    /// <summary>An off-grid frame that changes less of the picture than this is a UI blip, not content.</summary>
    private const float MinorChange = 0.02f;

    /// <summary>Structural similarity below this between two consecutive frames is a scene cut (Practical-RIFE's rule).</summary>
    private const float CutSsim = 0.2f;

    /// <summary>Accepted frames after which a source that has not locked is treated as free-running. Well beyond
    /// the eleven frames a steady source needs to lock: starting free and locking a moment later costs two
    /// restarts of the timeline (and, the first time, preparing a second network).</summary>
    private const int FramesBeforeFreeMode = 24;

    /// <summary>A frame that reaches the pipeline this long after it was on the glass was waiting behind a stall.
    /// (Normally a frame arrives about one refresh before its own timestamp, or a few tens of milliseconds
    /// after it while the previous frame's interpolations are still being made.)</summary>
    private const double StaleFrameAge = 0.120;

    /// <summary>Time without a conversion after which the driver's clean-up is "cold": the longer it has been idle,
    /// the slower its next call (measured at 1920x1080: 5 ms at 60 conversions per second, 10 ms at 30, 18 ms at
    /// 4 and 60 ms at 2 per second).</summary>
    private const double EnhanceIdle = 0.150;

    /// <summary>
    /// The real pipeline. Per captured frame: copy it, downscale + analyse it, decide whether it belongs to the
    /// content's cadence, plan the output frames it makes possible, and render them (inference + synthesis) in
    /// the order they are due.
    /// </summary>
    private void RunInterpolation()
    {
        var ctx = _ctx;
        var stats = ctx.Stats;
        stats.State = EngineState.Starting;

        var registry = ModelRegistry.Load(Options.ModelsDirectory);
        InferenceBackend backend = ChooseBackend();
        // The model the user chose runs every cadence whose timesteps are exact fractions (30 fps x2: always
        // 0.5). A cadence that does not divide the display's rate (25 fps on 120 Hz) or a free-running source
        // needs timesteps anywhere between two frames, and there the model that follows the timestep most
        // faithfully is used instead: with RIFE 4.25 a flat object stays on the nearest frame below t = 0.2,
        // which showed as uneven motion and doubled pictures (25 fps x4: 109 distinct pictures per second
        // instead of 120).
        ModelInfo chosen = registry.Find(Options.ModelId, backend.Precision);
        ModelInfo anyTimestep = registry.ForAnyTimestep(chosen);
        ModelInfo model = chosen;
        stats.Backend = backend.DisplayName;
        stats.Model = model.Id;
        lock (ctx.Timing) ctx.Scheduler.Epsilon = EpsilonFor(model);
        var sessionOptions = new FlowSessionOptions
        {
            CacheDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InterpolatIt", "cache"),
        };

        using var capture = new WgcCapture(ctx.Gpu, Options.TargetWindow);
        // Sized for the scheduler's largest delay (FrameScheduler.MaxDelay, 125 ms): 15 generated frames can be
        // waiting at 120 fps output, and a 60 fps source keeps 10 real frames alive.
        using var store = new FrameStore(ctx.Gpu, realCapacity: 12, generatedCapacity: 16);
        using var analyzer = new FrameAnalyzer(ctx.Gpu);
        using var synthesizer = new Synthesizer(ctx.Gpu);
        var estimator = new CadenceEstimator(ctx.Clock.Period);
        var plan = new List<PlannedFrame>(16);
        var sessions = new Dictionary<string, FlowSession>();      // per model, for the current inference size
        FlowSession? flow = null;
        NativeBuffer? tensor = null;

        FlowSession SessionFor(ModelInfo m, int width, int height)
        {
            if (sessions.TryGetValue(m.Id, out FlowSession? existing)) return existing;
            var previous = stats.State;
            stats.State = EngineState.Starting;
            stats.Message = $"preparing {m.Id} at {width}x{height}";
            var created = FlowSession.Create(backend, m, width, height, sessionOptions);
            sessions[m.Id] = created;
            stats.Message = null;
            stats.State = previous == EngineState.Starting ? EngineState.Waiting : previous;
            return created;
        }
        WaitHandle[] handles = [capture.FrameSignal, ctx.Stop.Token.WaitHandle];

        long id = 0;
        long accepted = 0;                  // frames accepted since the cadence estimator last started from scratch
        double lastAcceptedTime = 0, lastAcceptedAt = double.NaN;
        bool wasEnabled = true, skippingStale = false, flushed = true, bypassed = false;
        double atDisplayRateSince = double.NaN, belowDisplayRateSince = double.NaN;
        (long, long) maskPair = (-1, -1);
        int dumped = 0;
        double generation = 0.012;          // expected time from "frame in hand" to "first generated frame ready"
        double inferenceMs = 0;
        int maxPixels = InferenceSizing.TierAtOrBelow(Options.MaxInferencePixels);
        int inferenceWidth = 0, inferenceHeight = 0;
        var changes = new ChangeProfile();

        // The driver's clean-up of compressed video (optional): applied to a real frame just before it is first
        // put to use, so that frames that are never shown cost nothing.
        VideoEnhancer? enhancer = null;
        GpuFrame? untouched = null;         // the frame enhanced last, as it was captured
        long untouchedId = -1;
        bool enhanceAsked = false, enhanceOn = false;
        (int Width, int Height) enhanceSize = (0, 0);
        double enhanceMs = 0, lastEnhancedAt = 0;
        double enhanceUsualMs = 8;          // one conversion on a GPU that is awake, from the check when it was prepared
        double discardBefore = 0;           // frames shown before this moment are dropped (see the stale-frame rule)

        void ResetTimeline()
        {
            lock (ctx.Timing) ctx.Scheduler.Reset();
            ctx.Queue.Clear();
        }

        // Creates the video processor for this window size and finds out whether the driver really enhances
        // (it never says so itself). A few tenths of a second.
        bool PrepareEnhancer(int width, int height)
        {
            stats.Enhancing = false;
            if (enhancer is null)
            {
                enhancer = VideoEnhancer.TryCreate(ctx.Gpu, out string? unavailable);
                if (enhancer is null)
                {
                    stats.EnhanceProblem = unavailable;
                    return false;
                }
            }
            var (workWidth, workHeight) = VideoEnhancer.WorkSize(width, height);
            if ((long)workWidth * workHeight < VideoEnhancer.MinPixels)
            {
                stats.EnhanceProblem = "the window is too small for the graphics driver's clean-up (about 640x360 at least)";
                return false;
            }
            try
            {
                enhancer.Configure(width, height, width, height);
                EnhancerCheck check = enhancer.Check();
                Trace($"clean-up at {width}x{height} via {workWidth}x{workHeight}: {(check.Engaged ? "engaged" : "NOT engaged")}, " +
                      $"{check.ChangedShare:P1} of the test picture changed, {check.Milliseconds:F1} ms");
                enhanceUsualMs = Math.Max(2, check.Milliseconds);
                if (!check.Engaged)
                {
                    stats.EnhanceProblem = "the graphics driver leaves the picture as it is: switch on RTX Video Super Resolution in the " +
                                           "NVIDIA app (System > Video), then switch the clean-up off and on again";
                    return false;
                }
            }
            catch (SharpGen.Runtime.SharpGenException e)
            {
                stats.EnhanceProblem = $"the graphics driver refused the video conversion ({e.Message.Trim()})";
                return false;
            }
            untouched?.Dispose();
            untouched = new GpuFrame(ctx.Gpu, width, height);
            stats.EnhanceWidth = workWidth;
            stats.EnhanceHeight = workHeight;
            stats.EnhanceProblem = null;
            stats.Enhancing = true;
            return true;
        }

        // Cleans up a real frame in place, once. The picture as captured is kept for the static-pixel test of the
        // pair this frame will form with the next one.
        void Enhance(GpuFrame real)
        {
            if (!enhanceOn || real.Enhanced) return;
            double t0 = Qpc.Now;
            lock (ctx.Gpu.Sync) ctx.Gpu.Context.CopyResource(untouched!.Texture, real.Texture);
            untouchedId = real.Id;
            double t1 = Qpc.Now;
            enhancer!.Process(real.Texture, real.Texture);
            real.Enhanced = true;
            double t2 = Qpc.Now;
            ctx.Gpu.WaitForGpu();
            lastEnhancedAt = Qpc.Now;
            double ms = (lastEnhancedAt - t0) * 1000;
            if (ms > 15)
                Trace($"clean-up of frame {real.Id} took {ms:F0} ms: copy {(t1 - t0) * 1000:F1}, handing over {(t2 - t1) * 1000:F1}, waiting for the GPU {(Qpc.Now - t2) * 1000:F1}");
            enhanceMs = enhanceMs == 0 ? ms : 0.9 * enhanceMs + 0.1 * ms;
            stats.EnhanceMs = enhanceMs;
        }

        try
        {
            stats.State = EngineState.Waiting;
            while (!ctx.Stop.IsCancellationRequested)
            {
                // Wake for every captured frame, and often enough to notice that the source has gone quiet.
                WaitHandle.WaitAny(handles, (int)Math.Clamp(2500 * ctx.SourcePeriod, 30, 100));
                if (capture.IsClosed) { ctx.RequestStop("the target window was closed"); break; }

                while (!ctx.Stop.IsCancellationRequested && capture.TryAcquire(out CapturedFrame? frame))
                {
                    using (frame)
                    {
                        stats.Arrived.Mark();
                        int w = frame!.ContentWidth, h = frame.ContentHeight;

                        // ---- (re)configure for the window size ---------------------------------------------
                        if (w != store.Width || h != store.Height)
                        {
                            ResetTimeline();
                            estimator.Reset();
                            accepted = 0;
                            changes.Clear();
                            ctx.ContentWidth = 0;                   // keeps the overlay cloaked meanwhile
                            store.Configure(w, h);
                            enhanceOn = false;                      // prepared for the new size when it is first needed
                            enhancer?.ForgetSources();              // its views of the frames that were just replaced
                            enhanceSize = (0, 0);
                            untouchedId = -1;
                            stats.Enhancing = false;
                            var (iw, ih) = InferenceSizing.For(w, h, maxPixels, chosen.Multiple);
                            if (iw != inferenceWidth || ih != inferenceHeight)
                            {
                                // The networks are per inference size. Which one is needed shows once the
                                // source's cadence is known, so nothing is built here.
                                foreach (var old in sessions.Values) old.Dispose();
                                sessions.Clear();
                                flow = null;
                                inferenceWidth = iw;
                                inferenceHeight = ih;
                            }
                            analyzer.Configure(iw, ih);
                            analyzer.Reset();
                            synthesizer.Configure(iw, ih);
                            tensor?.Dispose();
                            tensor = new NativeBuffer(analyzer.TensorBytes);
                            stats.Width = w; stats.Height = h;
                            stats.InferenceWidth = iw; stats.InferenceHeight = ih;
                            ctx.ContentWidth = w;
                            ctx.ContentHeight = h;
                        }

                        // ---- frames from before a stall are history ---------------------------------------
                        // When this thread was held up (a network being prepared takes seconds), the capture
                        // pool still holds the few frames it had room for. Their timestamps continue the old
                        // cadence, so they would be scheduled as if nothing had happened - seconds behind.
                        // (The cadence estimator is left alone: it sees a gap and re-locks quickly.)
                        // After a short stall of our own making (preparing the clean-up: a few tenths of a second)
                        // some of the waiting frames are younger than that, and just as much history: scheduled,
                        // they would set the delay to their lateness, and that is only given back a refresh per
                        // half minute (measured: 158 ms instead of 33 ms).
                        if (Qpc.Now - frame.Time > StaleFrameAge || frame.Time < discardBefore)
                        {
                            Trace($"stale frame dropped, {(Qpc.Now - frame.Time) * 1000:F0} ms old");
                            if (!skippingStale)
                            {
                                ResetTimeline();
                                skippingStale = true;
                            }
                            continue;
                        }
                        skippingStale = false;

                        // ---- analysis ---------------------------------------------------------------------
                        double analysisBegan = Qpc.Now;
                        GpuFrame stored = store.StoreReal(frame, id);
                        FrameStats fs = analyzer.Analyze(stored.View, w, h, w, h, tensor!.Pointer);
                        if (Qpc.Now - analysisBegan > 0.025) Trace($"copy + analysis of frame {id} took {(Qpc.Now - analysisBegan) * 1000:F0} ms");
                        if (fs.HasPrevious && fs.ChangedTiles == 0)
                        {
                            stats.CountDuplicate();
                            stored.Id = -1;                         // the slot can be reused at once
                            continue;
                        }
                        // While the overlay has stepped aside, this frame is what the screen shows.
                        if (ctx.Recorder is { } recorder && ctx.Overlay.IsCloaked) recorder.AddFrame(stored.Texture, w, h, frame.Time);

                        // A near-copy of the previous picture, or a blip in a corner (see ChangeProfile).
                        if (fs.HasPrevious && changes.IsMinor(fs.StrongFraction))
                        {
                            stats.CountMinor();
                            stored.Id = -1;
                            continue;
                        }

                        // ---- cadence gate -----------------------------------------------------------------
                        double time = frame.Time;                   // the refresh at which this frame is on the glass
                        if (estimator.Classify(time) == GridClass.OffGrid && fs.ChangedFraction < MinorChange)
                        {
                            // A caret blink or a hover highlight between two content frames. The next content
                            // frame is a complete picture and contains it, so nothing is lost by skipping it.
                            estimator.NoteDropped();
                            stats.CountOffGrid();
                            stored.Id = -1;
                            continue;
                        }
                        SourceStamp stamp = estimator.Accept(time);
                        stats.Accepted.Mark();
                        accepted++;
                        flushed = false;
                        lastAcceptedAt = Qpc.Now;
                        if (fs.HasPrevious)
                        {
                            changes.Add(fs.StrongFraction);
                            stats.StrongChange = 0.95 * stats.StrongChange + 0.05 * fs.StrongFraction;
                        }
                        stats.LockFailures = estimator.LockFailures;
                        if (Options.TraceEvents)
                        {
                            Trace($"frame {id}: +{(time - lastAcceptedTime) * 1000:F2} ms ({(time - lastAcceptedTime) / ctx.Clock.Period:F2} refreshes), " +
                                  $"age {(Qpc.Now - time) * 1000:F1} ms, {stamp.Mode}{(stamp.Discontinuity ? " discontinuity" : "")}, " +
                                  $"changed {fs.ChangedFraction:P1} strongly {fs.StrongFraction:P1}, accepted {accepted}");
                            lastAcceptedTime = time;
                        }
                        bool cut = fs.HasPrevious && fs.Ssim < CutSsim;
                        if (cut) stats.CountCut();

                        bool locked = stamp.Mode == CadenceMode.Locked;
                        double period = locked ? stamp.Period : estimator.MedianInterval;
                        if (period > 0) ctx.SourcePeriod = period;
                        stats.CadenceLocked = locked;
                        stats.SourceFps = period > 0 ? 1.0 / period : 0;

                        // ---- the on/off switch -------------------------------------------------------------
                        bool enabled = ctx.Enabled;
                        if (!enabled)
                        {
                            // Off: keep following the cadence (cheap) so that switching on is immediate, but do
                            // not upload, infer or queue anything.
                            if (wasEnabled) ResetTimeline();
                            wasEnabled = false;
                            stats.State = EngineState.Bypass;
                            stats.TargetFps = 0;
                            id++;
                            continue;
                        }
                        if (!wasEnabled) ResetTimeline();
                        wasEnabled = true;

                        // Nothing is shown before the cadence is known: a schedule built on a guess would have
                        // to be thrown away (visibly) a moment later.
                        // (Nor while there is no period at all: right after a gap in the source the estimator has
                        // no interval yet, and a plan made with "not a number" asked for the other network.)
                        bool cadenceKnown = (locked || accepted >= FramesBeforeFreeMode) && period > 0;

                        // ---- the network for this cadence, built on first need ------------------------------
                        if (flow is null)
                        {
                            if (!cadenceKnown) { id++; continue; }
                            double refresh;
                            lock (ctx.Timing) refresh = ctx.Clock.Period;
                            model = PacingPolicy.Select(period, refresh, Options.Factor, locked).Kind == PacingKind.Continuous ? anyTimestep : chosen;
                            Trace($"cadence known ({(locked ? $"locked at {1 / period:F2} fps" : "free")}): preparing {model.Id}");
                            flow = SessionFor(model, inferenceWidth, inferenceHeight);      // seconds, the first time
                            flow.SubmitFrame(tensor.Pointer);
                            stats.Model = model.Id;
                            lock (ctx.Timing) ctx.Scheduler.Epsilon = EpsilonFor(model);
                            // That was a stall. A locked source re-locks within a few frames and must not be
                            // taken for a free one meanwhile (that would call for the other network);
                            // a free source simply carries on.
                            ResetTimeline();
                            if (locked) accepted = 0;
                            id++;
                            continue;
                        }
                        flow.SubmitFrame(tensor.Pointer);           // this frame is now the newer one of the pair
                        if (!cadenceKnown) { id++; continue; }

                        // ---- the clean-up switch -------------------------------------------------------------
                        bool enhanceWanted = ctx.Enhance;
                        if (enhanceWanted != enhanceAsked || (enhanceWanted && enhanceSize != (w, h)))
                        {
                            enhanceAsked = enhanceWanted;
                            enhanceSize = (w, h);
                            enhanceOn = false;
                            untouchedId = -1;
                            stats.EnhanceMs = enhanceMs = 0;
                            if (!enhanceWanted)
                            {
                                // Frames already cleaned up stay as they are; the next pair blends from one to the other.
                                stats.Enhancing = false;
                                stats.EnhanceProblem = null;
                            }
                            else
                            {
                                enhanceOn = PrepareEnhancer(w, h);
                                Trace(enhanceOn ? "clean-up on" : $"clean-up not available: {stats.EnhanceProblem}");
                                // That was a stall of a few tenths of a second: what was planned is history, and
                                // so are the frames that were captured meanwhile.
                                lastEnhancedAt = discardBefore = Qpc.Now;
                                ResetTimeline();
                                if (locked) accepted = 0;
                                id++;
                                continue;
                            }
                        }

                        // ---- a clean-up that has been idle is slow ---------------------------------------------
                        // After a pause in the source (or the moment between preparing and the first frame) the
                        // first conversions take 30-60 ms instead of 5. Scheduled, they come late and push the delay
                        // up for minutes. So the cold start is taken here, on a frame that is then not scheduled.
                        if (enhanceOn && Qpc.Now - lastEnhancedAt > Math.Max(EnhanceIdle, 3 * ctx.SourcePeriod))
                        {
                            // (A GPU that has clocked down needs some load to come back: a few conversions, until
                            // one of them is about as quick as usual.)
                            double began = Qpc.Now;
                            for (int round = 0; round < 6; round++)
                            {
                                double roundBegan = Qpc.Now;
                                enhancer!.Process(stored.Texture, untouched!.Texture);
                                ctx.Gpu.WaitForGpu();
                                if (round > 0 && (Qpc.Now - roundBegan) * 1000 < 1.5 * enhanceUsualMs) break;
                            }
                            untouchedId = -1;
                            Trace($"clean-up was idle for {(began - lastEnhancedAt) * 1000:F0} ms: warmed up in {(Qpc.Now - began) * 1000:F0} ms");
                            lastEnhancedAt = discardBefore = Qpc.Now;
                            ResetTimeline();
                            id++;
                            continue;
                        }

                        // ---- plan -----------------------------------------------------------------------------
                        double now = Qpc.Now;
                        plan.Clear();
                        PacingMode mode;
                        double refreshPeriod;
                        lock (ctx.Timing)
                        {
                            long before = ctx.Scheduler.ReanchorCount;
                            ctx.Scheduler.OnSourceFrame(id, stamp, cut, now, generation, estimator.MedianInterval, plan);
                            if (ctx.Scheduler.ReanchorCount != before) stats.CountReanchor();
                            mode = ctx.Scheduler.Mode;
                            refreshPeriod = ctx.Clock.Period;
                            stats.DelayMs = ctx.Scheduler.Delay * 1000;
                        }
                        double outputRate = PacingPolicy.OutputRate(mode, refreshPeriod);
                        stats.TargetFps = outputRate;
                        stats.Pacing = mode.Kind == PacingKind.Stepped
                            ? $"x{mode.Factor} exact"
                            : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{outputRate * period:F2}x continuous");

                        // ---- the model that suits this pacing ---------------------------------------------------
                        ModelInfo wanted = mode.Kind == PacingKind.Continuous ? anyTimestep : chosen;
                        if (wanted != model)
                        {
                            // The scheduler has just re-anchored for the new pacing, so nothing between the
                            // previous frame and this one is interpolated: this frame is simply the first of
                            // the other network's next pair.
                            bool ready = sessions.ContainsKey(wanted.Id);
                            Trace($"pacing {stats.Pacing}: switching from {model.Id} to {wanted.Id}{(ready ? "" : " (to be prepared)")}");
                            model = wanted;
                            flow = SessionFor(model, inferenceWidth, inferenceHeight);
                            flow.SubmitFrame(tensor.Pointer);
                            stats.Model = model.Id;
                            lock (ctx.Timing) ctx.Scheduler.Epsilon = EpsilonFor(model);
                            if (!ready)
                            {
                                // Building it took seconds: what was planned is history. Start over.
                                ResetTimeline();
                                if (locked) accepted = 0;
                                id++;
                                continue;
                            }
                        }

                        // ---- a source that already runs at the output rate needs no in-between frames -----------
                        // Stepping aside (and coming back) makes the picture jump by the playback delay, so it is
                        // only done for a source that stays there: a second at the display's rate to step aside,
                        // half a second below it to come back.
                        bool atDisplayRate = period > 0 && 1.0 / period >= 0.9 * outputRate;
                        if (atDisplayRate)
                        {
                            belowDisplayRateSince = double.NaN;
                            if (double.IsNaN(atDisplayRateSince)) atDisplayRateSince = now;
                            if (now - atDisplayRateSince >= 1.0) bypassed = true;
                        }
                        else
                        {
                            atDisplayRateSince = double.NaN;
                            if (double.IsNaN(belowDisplayRateSince)) belowDisplayRateSince = now;
                            if (now - belowDisplayRateSince >= 0.5) bypassed = false;
                        }
                        if (bypassed)
                        {
                            stats.State = EngineState.Bypass;
                            stats.Message = "the source already runs at the display's rate";
                            ctx.Queue.Clear();
                            id++;
                            continue;
                        }
                        stats.Message = null;

                        // ---- render the plan in the order it is due -----------------------------------------
                        bool first = true;
                        foreach (PlannedFrame p in plan)
                        {
                            if (p.Kind == PlannedKind.Real)
                            {
                                GpuFrame? real = store.FindReal(p.FrameA);
                                if (real is not null)
                                {
                                    Enhance(real);
                                    ctx.Queue.Enqueue(new QueuedFrame(p.Refresh, real, PlannedKind.Real, p.FrameA, p.FrameA, 0f));
                                }
                                continue;
                            }
                            GpuFrame? a = store.FindReal(p.FrameA), b = store.FindReal(p.FrameB);
                            if (a is null || b is null) continue;
                            double deadline;
                            lock (ctx.Timing) deadline = ctx.Scheduler.Deadline(p.Refresh);
                            if (Qpc.Now > deadline + refreshPeriod)
                            {
                                // Hopelessly late (the system stalled): generating it would only delay the rest.
                                stats.CountMiss();
                                lock (ctx.Timing) ctx.Scheduler.OnMiss(Qpc.Now);
                                continue;
                            }

                            if (maskPair != (p.FrameA, p.FrameB))
                            {
                                // Once per pair: which pixels are the same in both real frames (and stay as they are).
                                // That is asked of the frames as they were captured: the clean-up changes every
                                // pixel a little, and not identically from one frame to the next.
                                var before = a.Enhanced && untouchedId == a.Id ? untouched!.View : a.View;
                                synthesizer.BuildPairMask(before, b.View, w, h);
                                maskPair = (p.FrameA, p.FrameB);
                            }
                            Enhance(a);
                            Enhance(b);
                            double t0 = Qpc.Now;
                            flow.Run(Math.Clamp(p.T, (float)model.MinTimestep, 1f - (float)model.MinTimestep));
                            double t1 = Qpc.Now;
                            synthesizer.UploadField(flow.Flow, flow.MaskLogit);
                            GpuFrame target = store.NextGenerated();
                            synthesizer.Render(a.View, b.View, w, h, w, h, target.Target);
                            ctx.Gpu.WaitForGpu();                   // the frame really exists before it is queued
                            double ready = Qpc.Now;
                            if (t1 - t0 > 0.015) Trace($"inference for pair {p.FrameA}/{p.FrameB} took {(t1 - t0) * 1000:F0} ms");
                            if (ready - t1 > 0.015) Trace($"mask, clean-up and synthesis for pair {p.FrameA}/{p.FrameB} took {(ready - t1) * 1000:F0} ms");
                            if (Options.DumpFramesDirectory is string dumpTo && dumped < 3 && stats.Generated.Total > 300 + 40 * dumped)
                            {
                                // Diagnostics: the two real frames, what was made of them, and how large the flow is.
                                Directory.CreateDirectory(dumpTo);
                                string name = Path.Combine(dumpTo, $"dump{dumped}-pair{p.FrameA}-{p.FrameB}-t{p.T:F2}");
                                Png.WriteBgra(name + "-a.png", ctx.Gpu.ReadBack(a.Texture, w, h), w, h, w * 4);
                                Png.WriteBgra(name + "-b.png", ctx.Gpu.ReadBack(b.Texture, w, h), w, h, w * 4);
                                Png.WriteBgra(name + "-out.png", ctx.Gpu.ReadBack(target.Texture, w, h), w, h, w * 4);
                                if (enhanceOn && untouchedId == b.Id)
                                    Png.WriteBgra(name + "-b-as-captured.png", ctx.Gpu.ReadBack(untouched!.Texture, w, h), w, h, w * 4);
                                unsafe
                                {
                                    var flowValues = new ReadOnlySpan<ushort>((void*)flow.Flow, 4 * flow.Width * flow.Height);
                                    float largest = 0;
                                    foreach (ushort bits in flowValues) largest = MathF.Max(largest, MathF.Abs((float)BitConverter.UInt16BitsToHalf(bits)));
                                    Console.Error.WriteLine($"dump {dumped}: pair {p.FrameA}/{p.FrameB} t={p.T:F2} largest flow {largest:F1} px, texture a {a.Texture.NativePointer:X} b {b.Texture.NativePointer:X} -> {name}-*.png");
                                }
                                dumped++;
                            }
                            ctx.Queue.Enqueue(new QueuedFrame(p.Refresh, target, PlannedKind.Interpolated, p.FrameA, p.FrameB, p.T));
                            stats.Generated.Mark(ready);
                            lock (ctx.Timing) ctx.Scheduler.OnFrameReady(p.Refresh, ready);

                            inferenceMs = inferenceMs == 0 ? (t1 - t0) * 1000 : 0.9 * inferenceMs + 0.1 * (t1 - t0) * 1000;
                            stats.InferenceMs = inferenceMs;
                            if (first)
                            {
                                // Track the slow end of "frame in hand -> first generated frame ready".
                                double g = ready - now;
                                generation = g > generation ? g : 0.98 * generation + 0.02 * g;
                                first = false;
                            }
                        }
                        id++;
                    }
                }

                // ---- the source has gone quiet ----------------------------------------------------------
                // A paused video or a stalled stream: the last picture planned is usually an in-between one.
                // Put the newest real frame on screen, so that what stays there is what the window shows.
                if (!flushed && ctx.Enabled && !bypassed && Qpc.Now - lastAcceptedAt > Math.Max(2.5 * ctx.SourcePeriod, 0.030))
                {
                    flushed = true;
                    PlannedFrame last;
                    bool pending;
                    lock (ctx.Timing) pending = ctx.Scheduler.Flush(Qpc.Now, out last);
                    if (pending && store.FindReal(last.FrameA) is GpuFrame newest)
                    {
                        Enhance(newest);
                        ctx.Queue.Enqueue(new QueuedFrame(last.Refresh, newest, PlannedKind.Real, last.FrameA, last.FrameA, 0f));
                    }
                }
                _log?.Flush();
            }
        }
        finally
        {
            foreach (var s in sessions.Values) s.Dispose();
            tensor?.Dispose();
            untouched?.Dispose();
            enhancer?.Dispose();
        }
    }

    /// <summary>
    /// A slot this close to a real frame shows that frame instead of an interpolation: just inside the range in
    /// which the model follows the timestep (below it the network returns the frame anyway, and an inference is
    /// saved). The position error is at most this fraction of the motion between two frames.
    /// </summary>
    private static double EpsilonFor(ModelInfo model) => Math.Clamp(model.MinTimestep - 0.005, 0.02, 0.2);

    private readonly double _traceStart = Qpc.Now;

    private void Trace(string message)
    {
        if (Options.TraceEvents) Console.Error.WriteLine($"[{Qpc.Now - _traceStart,8:F3}] {message}");
    }

    private InferenceBackend ChooseBackend()
    {
        // Vendor providers come from the Windows ML catalog; getting one ready can mean a download on first use.
        foreach (var provider in EpCatalog.ListCatalogProviders())
        {
            bool wanted = provider.Name.Contains("NvTensorRtRtx", StringComparison.OrdinalIgnoreCase)
                          || provider.Name.Contains("AMDGPU", StringComparison.OrdinalIgnoreCase);
            if (!wanted) continue;
            _ctx.Stats.Message = provider.ReadyState == "Ready" ? null : $"getting {provider.Name} ready";
            var (ok, detail) = EpCatalog.EnsureProviderAsync(provider.Name).GetAwaiter().GetResult();
            if (!ok) Console.Error.WriteLine($"warning: {provider.Name} is not available: {detail}");
        }
        _ctx.Stats.Message = null;

        var backends = EpCatalog.GetBackends();
        if (backends.Count == 0) throw new InvalidOperationException("ONNX Runtime reports no execution provider.");
        if (Options.BackendId is string wantedId)
            return backends.FirstOrDefault(b => b.Id.Equals(wantedId, StringComparison.OrdinalIgnoreCase))
                   ?? throw new InvalidOperationException($"Backend '{wantedId}' is not available. Available: {string.Join(", ", backends.Select(b => b.Id).Distinct())}.");
        return backends[0];
    }
}
