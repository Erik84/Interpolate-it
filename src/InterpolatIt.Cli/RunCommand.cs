using InterpolatIt.Core.Native;
using InterpolatIt.Core.Pipeline;

namespace InterpolatIt.Cli;

/// <summary>Runs a session on a window and prints one status line per second.</summary>
internal static class RunCommand
{
    private static string Describe(InterpolatIt.Core.Recording.RecordingStatus? r)
        => r is null ? "nothing recorded"
            : r.Error is not null ? $"FAILED: {r.Error}"
            : $"{r.Codec} {r.Width}x{r.Height}, {r.Frames} frames in {r.Seconds:F2} s ({(r.Seconds > 0 ? r.Frames / r.Seconds : 0):F1} fps), {r.Dropped} dropped" +
              (r.Sound ? ", with sound" : r.SoundProblem is null ? ", without sound" : $", without sound ({r.SoundProblem})") +
              (r.EndedBecause is null ? "" : $", ended because {r.EndedBecause}");

    public static int Run(Args a)
    {
        var window = Program.ResolveWindow(a);
        double seconds = a.GetDouble("seconds", 0);
        bool quiet = a.Has("quiet");
        var options = new EngineOptions
        {
            TargetWindow = window.Handle,
            Factor = a.GetInt("factor", 2),
            Passthrough = a.Has("passthrough"),
            ModelId = a.Get("model") ?? "rife-v4.25",
            BackendId = a.Get("backend"),
            PacingLogPath = a.Get("log"),
            ShowHud = a.Has("hud"),
            Enhance = a.Has("enhance"),
            RecordSound = !a.Has("no-sound"),
            PresentPhase = a.GetDouble("phase", 0.5),
            SyncInterval = a.GetInt("sync", 1),
            CalibrateLag = !a.Has("no-calibrate"),
            RaiseGpuPriority = !a.Has("no-gpu-priority"),
            TraceEvents = a.Has("trace"),
            DumpFramesDirectory = a.Get("dump-frames"),
        };

        using var done = new ManualResetEventSlim();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.Set(); };
        Console.WriteLine($"target: {window}");

        InterpolationSession session;
        try
        {
            session = InterpolationSession.Start(options);
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException or FileNotFoundException or DirectoryNotFoundException)
        {
            Console.Error.WriteLine("error: " + e.Message);
            return 1;
        }

        using (session)
        {
            session.Stopped += _ => done.Set();
            double start = Qpc.Now;
            var s = session.Stats;
            Console.WriteLine($"gpu   : scheduling {s.GpuPriority}");
            bool inputChecked = false;
            double toggleEvery = a.GetDouble("toggle-every", 0), nextToggle = toggleEvery;
            double hudEvery = a.GetDouble("hud-every", 0), nextHud = hudEvery;
            double enhanceEvery = a.GetDouble("enhance-every", 0), nextEnhance = enhanceEvery;
            bool enhanceReported = false;
            string? recordPath = a.Get("record");
            double recordAfter = a.GetDouble("record-after", 5), recordFor = a.GetDouble("record-for", 0);
            bool recording = false;
            while (!done.Wait(1000))
            {
                double elapsed = Qpc.Now - start;
                if (recordPath is not null && !recording && elapsed >= recordAfter && s.Width > 0)
                {
                    var status = session.StartRecording(recordPath);
                    recording = true;
                    Console.WriteLine($"record: started {status.Width}x{status.Height} -> {status.Path}");
                }
                if (recording && recordFor > 0 && elapsed >= recordAfter + recordFor && session.RecordingState is { Finished: false })
                    Console.WriteLine($"record: stopped, {Describe(session.StopRecording())}");
                if (toggleEvery > 0 && elapsed >= nextToggle)
                {
                    session.Enabled = !session.Enabled;         // what the on/off hotkey does
                    nextToggle += toggleEvery;
                    Console.WriteLine($"toggle: interpolation {(session.Enabled ? "ON" : "OFF")}");
                }
                if (hudEvery > 0 && elapsed >= nextHud)
                {
                    session.HudVisible = !session.HudVisible;   // what the readout hotkey does
                    nextHud += hudEvery;
                    Console.WriteLine($"toggle: fps readout {(session.HudVisible ? "shown" : "hidden")}");
                }
                if (enhanceEvery > 0 && elapsed >= nextEnhance)
                {
                    session.Enhance = !session.Enhance;         // what the clean-up switch does
                    nextEnhance += enhanceEvery;
                    enhanceReported = false;
                    Console.WriteLine($"toggle: clean-up {(session.Enhance ? "ON" : "OFF")}");
                }
                if (session.Enhance && !enhanceReported && (s.Enhancing || s.EnhanceProblem is not null))
                {
                    // Whether the driver really does it shows a moment after it was asked for.
                    enhanceReported = true;
                    Console.WriteLine(s.Enhancing
                        ? $"clean-up: active, the driver works at {s.EnhanceWidth}x{s.EnhanceHeight}"
                        : $"clean-up: NOT active - {s.EnhanceProblem}");
                }
                if (!inputChecked && elapsed >= 3 && s.State is EngineState.Interpolating or EngineState.Bypass && s.ActualFps > 0)
                {
                    // Which window gets a mouse click in the middle of the covered area? It must be the target
                    // (or one of its children), not the overlay that is drawn over it.
                    inputChecked = true;
                    var b = window.Bounds;
                    if (InterpolatIt.Core.Native.Dwm.TryGetExtendedFrameBounds(window.Handle, out RECT now)) b = now;
                    nint hit = User32.WindowFromPoint(new POINT { X = (b.Left + b.Right) / 2, Y = (b.Top + b.Bottom) / 2 });
                    nint root = User32.GetAncestor(hit, User32.GA_ROOT);
                    Console.WriteLine(root == window.Handle
                        ? "input : a click on the covered area goes to the target window (click-through ok)"
                        : "input : a click on the covered area would go to another window, NOT the target (is something on top of it?)");
                }
                if (!quiet)
                    Console.WriteLine($"{elapsed,5:F0}s {s.State,-13} source {s.SourceFps,6:F2} fps  target {s.TargetFps,6:F2}  actual {s.ActualFps,6:F2}  " +
                                      $"arriving {s.Arrived.Rate(),6:F1}  delay {s.DelayMs,5:F1} ms  lag {s.Lag}  late {s.LatePresents}  stale {s.StaleDropped}  " +
                                      $"miss {s.Misses}  dup {s.Duplicates}  {s.Pacing}" +
                                      (s.InferenceMs > 0 ? $"  infer {s.InferenceMs:F1} ms @ {s.InferenceWidth}x{s.InferenceHeight}" : "") +
                                      (s.Enhancing ? $"  clean-up {s.EnhanceMs:F1} ms" : ""));
                if (seconds > 0 && elapsed >= seconds) break;
            }
            if (recording && session.RecordingState is { Finished: false })
                Console.WriteLine($"record: stopped, {Describe(session.StopRecording())}");
            Console.WriteLine($"summary: presented {s.Presented.Total}, arrived {s.Arrived.Total}, accepted {s.Accepted.Total}, generated {s.Generated.Total}, " +
                              $"late {s.LatePresents}, stale {s.StaleDropped}, misses {s.Misses}, duplicates {s.Duplicates}, off-grid {s.OffGridDropped}, " +
                              $"cuts {s.SceneCuts}, re-anchors {s.Reanchors}, minor {s.MinorDropped}, overlay repairs {s.OverlayRepairs}{(s.LastOverlayProblem is null ? "" : $" ({s.LastOverlayProblem})")}, lag {s.Lag}");
            if (session.Enhance)
                Console.WriteLine(s.Enhancing
                    ? $"clean-up: active at the end, {s.EnhanceMs:F1} ms per real frame at {s.EnhanceWidth}x{s.EnhanceHeight}"
                    : $"clean-up: not active at the end ({s.EnhanceProblem ?? "nothing was being shown"})");
            if (session.StopReason is string reason) Console.WriteLine("stopped: " + reason);
            return s.State == EngineState.Failed ? 1 : 0;
        }
    }
}
