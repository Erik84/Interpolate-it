using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using InterpolatIt.Core.Gpu;
using InterpolatIt.Core.Inference;
using InterpolatIt.Core.Util;
using Microsoft.ML.OnnxRuntime;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace InterpolatIt.Bench;

/// <summary>
/// Milestone M1: proves the inference path end to end and measures it.
///   1. lists / acquires execution providers (Windows ML catalog) and the resulting backends;
///   2. for every backend x model runs the golden cases through the real GPU pipeline
///      (downscale+analyse -> ONNX Runtime -> field upload -> synthesis shader) and compares with the references
///      written by tools/export (network outputs and the composed picture);
///   3. prints session-creation time (cold / warm) and per-stage latency percentiles, plus an inference-only
///      table over the resolution tiers.
/// Exit code is non-zero when a quality gate fails.
/// </summary>
internal static class Program
{
    private sealed record Options(string[] Backends, string[] Models, int Iterations, bool Install, bool Strict,
        bool Tiers, string OutDir, string? ModelsDir, bool Diag, bool LegacyDml);

    private static readonly (int W, int H)[] TierSizes = [(640, 384), (832, 448), (1280, 704), (1920, 1088)];

    private const double MinPsnrVsReference = 40.0;     // composed picture vs the Python reference pipeline
    private const double MaxFlowP999 = 1.0;             // px, network output vs fp32 PyTorch

    private static async Task<int> Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        var opt = Parse(args);
        Directory.CreateDirectory(opt.OutDir);

        if (VsrInput is not null)
        {
            using var device = GpuDevice.Create();
            return VsrProbe.Run(device, VsrInput, VsrScale, opt.OutDir);
        }
        if (RecordSizes.Length > 0)
        {
            using var device = GpuDevice.Create();
            return RecordProbe.Run(device, RecordSizes, RecordRate, RecordSeconds, opt.OutDir);
        }
        if (EnhanceSizes.Length > 0 || EnhancePicture is not null)
        {
            using var device = GpuDevice.Create();
            if (RaisePriority) Console.WriteLine($"GPU scheduling: {device.RaiseSchedulingPriority()}");
            return EnhanceProbe.Run(device, EnhanceSizes, EnhanceScale, EnhancePicture, opt.OutDir, EnhanceSeconds, EnhanceRate, EnhanceWatch);
        }
        if (AudioSeconds > 0) return AudioProbe.Run(AudioSeconds);
        if (HudOut is not null)
        {
            // The fps readout exactly as a session draws it, on a plain background instead of a window.
            using var device = GpuDevice.Create();
            using var hud = new InterpolatIt.Core.Presentation.Hud(device);
            using var picture = new GpuImage(device, 420, 150, null, renderTarget: true);
            using (device.UseContext())
            {
                device.Context.ClearRenderTargetView(picture.Target!, new Vortice.Mathematics.Color4(0.25f, 0.35f, 0.5f, 1f));
                hud.SetText(InterpolatIt.Core.Presentation.Hud.FpsLines(59.94, 120, 119.6), scale: 2);
                hud.Draw(picture.Texture, 420, 150, 16, 48);
            }
            Png.WriteBgra(HudOut, picture.ReadBack(), 420, 150, 420 * 4);
            Console.WriteLine($"fps readout written to {HudOut}");
            return 0;
        }

        var registry = ModelRegistry.Load(opt.ModelsDir);
        using var gpu = GpuDevice.Create();
        Console.WriteLine($"ONNX Runtime {OrtEnv.Instance().GetVersionString()}   models: {registry.Directory}");
        Console.WriteLine($"Direct3D adapter: {gpu.Adapter.Description} (vendor {gpu.Adapter.VendorId:X4}), GPU sync via {(gpu.UsesFence ? "fence" : "event query")}");

        Console.WriteLine("\n== Windows ML execution-provider catalog");
        var catalog = EpCatalog.ListCatalogProviders();
        if (catalog.Count == 0) Console.WriteLine("   (catalog not available or empty)");
        foreach (var p in catalog)
            Console.WriteLine($"   {p.Name,-36} {p.ReadyState,-10} {p.Certification,-12} {p.LibraryPath}");

        if (opt.Install)
        {
            foreach (var p in catalog.Where(p => p.Name.Contains("NvTensorRtRtx", StringComparison.OrdinalIgnoreCase)
                                                 || p.Name.Contains("AMDGPU", StringComparison.OrdinalIgnoreCase)))
            {
                Console.Write($"   ensuring {p.Name} ... ");
                var sw = Stopwatch.StartNew();
                double last = -1;
                var (ok, detail) = await EpCatalog.EnsureProviderAsync(p.Name, v =>
                {
                    if (v - last >= 25) { last = v; Console.Write($"{v:F0}% "); }   // progress is reported as 0..100
                });
                Console.WriteLine($"{(ok ? "ok" : "FAILED")} in {sw.Elapsed.TotalSeconds:F1} s: {detail}");
            }
        }

        Console.WriteLine("\n== Backends (ONNX Runtime EP devices)");
        var backends = EpCatalog.GetBackends();
        foreach (var b in backends)
        {
            string meta = string.Join("; ", b.Device.HardwareDevice.Metadata.Entries.Select(kv => $"{kv.Key}={kv.Value}"));
            Console.WriteLine($"   {b.Id,-9} {b.EpName,-34} {b.DeviceName}   [{b.Device.HardwareDevice.Type} {b.VendorId:X4}:{b.DeviceId:X4}] {meta}");
        }

        var selected = backends
            .Where(b => opt.Backends.Contains("all") || opt.Backends.Contains(b.Id))
            .Where(b => b.Kind == BackendKind.Cpu || (b.IsGpu && b.VendorId == gpu.Adapter.VendorId))
            .GroupBy(b => b.Id).Select(g => g.First()).ToList();
        var modelIds = opt.Models.Contains("all") ? registry.Ids.ToArray() : opt.Models;

        var golden = LoadGolden(registry.Directory);
        var sessionOptions = new FlowSessionOptions
        {
            CacheDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InterpolatIt", "cache"),
            StrictGpu = opt.Strict,
            LegacyDmlRegistration = opt.LegacyDml,
        };

        if (opt.Diag)
        {
            foreach (var backend in selected)
                foreach (string modelId in modelIds)
                    Diagnose(backend, registry.Find(modelId, backend.Precision), sessionOptions, opt);
            Probe(backends, registry, golden);
            return 0;
        }

        if (WindowSizes.Length > 0)
        {
            foreach (var backend in selected.Where(b => b.IsGpu))
                foreach (string modelId in modelIds)
                    foreach (var (w, h) in WindowSizes)
                        MeasureWindow(gpu, backend, registry.Find(modelId, backend.Precision), w, h, sessionOptions, opt);
            return 0;
        }

        var results = new List<object>();
        var failures = new List<string>();
        foreach (var backend in selected)
        {
            foreach (string modelId in modelIds)
            {
                ModelInfo model;
                try { model = registry.Find(modelId, backend.Precision); }
                catch (KeyNotFoundException e) { Console.WriteLine($"\n== {backend.Id} / {modelId}: skipped ({e.Message})"); continue; }

                Console.WriteLine($"\n== {backend.Id} / {model}");
                try
                {
                    RunGolden(gpu, backend, model, golden, sessionOptions, opt, results, failures);
                    if (backend.IsGpu)
                    {
                        // A GPU provider that cannot run an operator hands it to the CPU without any error; that
                        // is how GridSample at opset 20 cost 740 ms per inference. Make it a hard failure.
                        double cpuMs = Diagnose(backend, model, sessionOptions, opt, quiet: true);
                        Console.WriteLine($"   node placement: {(cpuMs <= 0 ? "all on " + backend.EpName : $"{cpuMs:F2} ms/inference on the CPU provider  FAIL")}");
                        if (cpuMs > 0) failures.Add($"{backend.Id}/{model.Id}: {cpuMs:F2} ms/inference ran on the CPU provider");
                    }
                    if (opt.Tiers) RunTiers(backend, model, sessionOptions, opt, results);
                }
                catch (Exception e) when (e is OnnxRuntimeException or InvalidOperationException or ArgumentException)
                {
                    string msg = e.Message.Split('\n')[0];
                    Console.WriteLine($"   FAILED: {e.GetType().Name}: {msg}");
                    failures.Add($"{backend.Id}/{model.Id}: {msg}");
                }
            }
        }

        string json = Path.Combine(opt.OutDir, "results.json");
        File.WriteAllText(json, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"\nresults: {json}");
        if (failures.Count > 0)
        {
            Console.WriteLine("\nFAILED:");
            foreach (string f in failures) Console.WriteLine("  - " + f);
            return 1;
        }
        Console.WriteLine("OK");
        return 0;
    }

    // ------------------------------------------------------------------ golden cases + pipeline latency

    private sealed record GoldenCase(string Name, string Model, float T, int Width, int Height, int InferenceWidth,
        int InferenceHeight, string Frame0, string Frame1, string GroundTruth, string Reference, string Flow, string MaskLogit);

    private static List<GoldenCase> LoadGolden(string modelsDir)
    {
        string root = Path.Combine(modelsDir, "golden");
        string index = Path.Combine(root, "golden.json");
        var list = new List<GoldenCase>();
        if (!File.Exists(index)) return list;
        using var doc = JsonDocument.Parse(File.ReadAllText(index));
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            string P(string key) => Path.Combine(root, c.GetProperty(key).GetString()!.Replace('/', Path.DirectorySeparatorChar));
            list.Add(new GoldenCase(c.GetProperty("name").GetString()!, c.GetProperty("model").GetString()!,
                c.GetProperty("t").GetSingle(), c.GetProperty("width").GetInt32(), c.GetProperty("height").GetInt32(),
                c.GetProperty("inference_width").GetInt32(), c.GetProperty("inference_height").GetInt32(),
                P("frame0"), P("frame1"), P("ground_truth"), P("reference"), P("flow"), P("mask_logit")));
        }
        return list;
    }

    private static void RunGolden(GpuDevice gpu, InferenceBackend backend, ModelInfo model, List<GoldenCase> golden,
        FlowSessionOptions sessionOptions, Options opt, List<object> results, List<string> failures)
    {
        var cases = golden.Where(c => c.Model == model.Id).ToList();
        if (cases.Count == 0) { Console.WriteLine("   no golden cases for this model"); return; }

        using var analyzer = new FrameAnalyzer(gpu);
        using var synthesizer = new Synthesizer(gpu);
        FlowSession? session = null;
        try
        {
            foreach (var c in cases)
            {
                if (session is null || session.Width != c.InferenceWidth || session.Height != c.InferenceHeight)
                {
                    session?.Dispose();
                    session = FlowSession.Create(backend, model, c.InferenceWidth, c.InferenceHeight, sessionOptions);
                    double cold = session.CreateMilliseconds;
                    session.Dispose();
                    session = FlowSession.Create(backend, model, c.InferenceWidth, c.InferenceHeight, sessionOptions);
                    Console.WriteLine($"   session {c.InferenceWidth}x{c.InferenceHeight}: created in {cold:F0} ms, again in {session.CreateMilliseconds:F0} ms");
                    results.Add(new { kind = "session", backend = backend.Id, model = model.Id, width = c.InferenceWidth, height = c.InferenceHeight, coldMs = cold, warmMs = session.CreateMilliseconds });
                }
                analyzer.Configure(c.InferenceWidth, c.InferenceHeight);
                analyzer.Reset();
                synthesizer.Configure(c.InferenceWidth, c.InferenceHeight);

                byte[] f0 = Png.ReadBgra(c.Frame0, out int w, out int h);
                byte[] f1 = Png.ReadBgra(c.Frame1, out _, out _);
                using var tex0 = new GpuImage(gpu, w, h, f0);
                using var tex1 = new GpuImage(gpu, w, h, f1);
                using var target = new GpuImage(gpu, w, h, null, renderTarget: true);

                analyzer.Analyze(tex0.View, w, h, w, h, session.NewFrameInput);
                session.SubmitFrame();
                var stats = analyzer.Analyze(tex1.View, w, h, w, h, session.NewFrameInput);
                session.SubmitFrame();
                session.Run(c.T);

                var (flowP999, flowMax) = CompareHalf(session.Flow, File.ReadAllBytes(c.Flow));
                var (maskP999, _) = CompareHalf(session.MaskLogit, File.ReadAllBytes(c.MaskLogit));

                synthesizer.UploadField(session.Flow, session.MaskLogit);
                synthesizer.Render(tex0.View, tex1.View, w, h, w, h, target.Target!);
                byte[] output = target.ReadBack();
                string png = Path.Combine(opt.OutDir, $"{backend.Id}-{model.Id}-{c.Name}.png");
                Png.WriteBgra(png, output, w, h, w * 4);

                double psnrRef = Psnr(output, Png.ReadBgra(c.Reference, out _, out _));
                double psnrGt = Psnr(output, Png.ReadBgra(c.GroundTruth, out _, out _));
                bool ok = psnrRef >= MinPsnrVsReference && flowP999 <= MaxFlowP999;
                Console.WriteLine($"   golden {c.Name,-8} {w}x{h} via {c.InferenceWidth}x{c.InferenceHeight} t={c.T}: " +
                                  $"flow p99.9 {flowP999:F3} px (max {flowMax:F2}), logit p99.9 {maskP999:F3}, " +
                                  $"picture vs reference {psnrRef:F1} dB, vs true frame {psnrGt:F1} dB, " +
                                  $"changed tiles {stats.ChangedFraction:P0}, ssim {stats.Ssim:F3}  {(ok ? "ok" : "FAIL")}");
                results.Add(new { kind = "golden", backend = backend.Id, model = model.Id, @case = c.Name, flowP999, flowMax, maskP999, psnrRef, psnrGt, ok, png });
                if (!ok) failures.Add($"{backend.Id}/{model.Id}/{c.Name}: picture {psnrRef:F1} dB vs reference, flow p99.9 {flowP999:F3} px");

                if (c == cases[^1])
                    MeasurePipeline(gpu, backend, model, session, analyzer, synthesizer, tex0, tex1, target, w, h, opt, results);
            }
        }
        finally
        {
            session?.Dispose();
        }
    }

    private static void MeasurePipeline(GpuDevice gpu, InferenceBackend backend, ModelInfo model, FlowSession session,
        FrameAnalyzer analyzer, Synthesizer synthesizer, GpuImage tex0, GpuImage tex1, GpuImage target, int w, int h,
        Options opt, List<object> results)
    {
        int warmup = Math.Min(20, opt.Iterations);
        var analyze = new List<double>(); var infer = new List<double>(); var synth = new List<double>(); var total = new List<double>();
        long budget = Stopwatch.Frequency * 20;      // stop a slow backend after 20 s
        long begin = Stopwatch.GetTimestamp();
        for (int i = 0; i < opt.Iterations + warmup; i++)
        {
            var src = (i & 1) == 0 ? tex0 : tex1;
            long t0 = Stopwatch.GetTimestamp();
            analyzer.Analyze(src.View, w, h, w, h, session.NewFrameInput);
            long t1 = Stopwatch.GetTimestamp();
            session.SubmitFrame();
            session.Run(0.5f);
            long t2 = Stopwatch.GetTimestamp();
            synthesizer.UploadField(session.Flow, session.MaskLogit);
            synthesizer.Render(tex0.View, tex1.View, w, h, w, h, target.Target!);
            gpu.WaitForGpu();
            long t3 = Stopwatch.GetTimestamp();
            if (i >= warmup)
            {
                analyze.Add(Ms(t1 - t0)); infer.Add(Ms(t2 - t1)); synth.Add(Ms(t3 - t2)); total.Add(Ms(t3 - t0));
            }
            if (Stopwatch.GetTimestamp() - begin > budget && total.Count >= 10) break;
        }
        Console.WriteLine($"   pipeline {w}x{h} via {session.Width}x{session.Height}, {total.Count} frames (ms, p50 / p95 / p99):");
        Console.WriteLine($"      downscale+analyse+readback  {Pct(analyze)}");
        Console.WriteLine($"      inference (incl. transfers) {Pct(infer)}");
        Console.WriteLine($"      field upload + synthesis    {Pct(synth)}");
        Console.WriteLine($"      total per generated frame   {Pct(total)}   -> {1000.0 / Percentile(total, 0.5):F0} frames/s");
        results.Add(new
        {
            kind = "pipeline", backend = backend.Id, model = model.Id, width = w, height = h,
            inferenceWidth = session.Width, inferenceHeight = session.Height, frames = total.Count,
            analyzeP50 = Percentile(analyze, 0.5), analyzeP95 = Percentile(analyze, 0.95),
            inferP50 = Percentile(infer, 0.5), inferP95 = Percentile(infer, 0.95), inferP99 = Percentile(infer, 0.99),
            synthP50 = Percentile(synth, 0.5), synthP95 = Percentile(synth, 0.95),
            totalP50 = Percentile(total, 0.5), totalP95 = Percentile(total, 0.95), totalP99 = Percentile(total, 0.99),
        });
    }

    // ------------------------------------------------------------------ what a window of a given size costs

    private static (int Width, int Height)[] WindowSizes = [];
    private static string? VsrInput;        // --vsr-probe FILE.png: test the driver's own video enhancement on this picture
    private static int VsrScale = 2;
    private static (int Width, int Height)[] RecordSizes = [];     // --record-probe WxH[,WxH...]: can the recorder keep up
    private static int RecordRate = 120;
    private static double RecordSeconds = 5;
    private static string? HudOut;          // --hud-probe FILE.png: draw the fps readout into a picture
    private static (int Width, int Height)[] EnhanceSizes = [];    // --enhance-probe WxH[,WxH...]: the engine's enhancer and its self-check
    private static int EnhanceScale = 1;
    private static string? EnhancePicture;  // --enhance-picture FILE.png: run that picture through it and save close-ups
    private static double EnhanceSeconds;   // --enhance-seconds S [--enhance-rate 60]: also at the pipeline's pace, for the slow end
    private static double EnhanceRate = 60;
    private static bool RaisePriority;      // --gpu-priority: ask for the scheduling priority a session asks for
    private static bool EnhanceWatch;       // --enhance-watch: does the same input keep giving the same output
    private static double AudioSeconds;     // --audio-probe [S]: capture this process's own (inaudibly soft) sound

    /// <summary>
    /// Times every stage of the pipeline for a window of the given size, without a window: the inference
    /// resolution is capped, but downscaling, the pair mask, the synthesis and the copies all work on the full
    /// picture and grow with it.
    /// </summary>
    private static void MeasureWindow(GpuDevice gpu, InferenceBackend backend, ModelInfo model, int w, int h,
        FlowSessionOptions sessionOptions, Options opt)
    {
        var (iw, ih) = InferenceSizing.For(w, h, 1280 * 704, model.Multiple);
        using var session = FlowSession.Create(backend, model, iw, ih, sessionOptions);
        using var analyzer = new FrameAnalyzer(gpu);
        using var synthesizer = new Synthesizer(gpu);
        analyzer.Configure(iw, ih);
        synthesizer.Configure(iw, ih);
        using var tex0 = new GpuImage(gpu, w, h, BlockTexture(w, h, 0));
        using var tex1 = new GpuImage(gpu, w, h, BlockTexture(w, h, 14));
        using var target = new GpuImage(gpu, w, h, null, renderTarget: true);
        using var copy = new GpuImage(gpu, w, h, null);

        var analyse = new List<double>(); var infer = new List<double>(); var mask = new List<double>();
        var synth = new List<double>(); var copies = new List<double>();
        const int warmup = 15;
        long begin = Stopwatch.GetTimestamp(), budget = Stopwatch.Frequency * 15;
        for (int i = 0; i < opt.Iterations + warmup; i++)
        {
            var src = (i & 1) == 0 ? tex0 : tex1;
            long t0 = Stopwatch.GetTimestamp();
            analyzer.Analyze(src.View, w, h, w, h, session.NewFrameInput);
            long t1 = Stopwatch.GetTimestamp();
            session.SubmitFrame();
            session.Run(0.5f);
            long t2 = Stopwatch.GetTimestamp();
            synthesizer.BuildPairMask(tex0.View, tex1.View, w, h);
            gpu.WaitForGpu();
            long t3 = Stopwatch.GetTimestamp();
            synthesizer.UploadField(session.Flow, session.MaskLogit);
            synthesizer.Render(tex0.View, tex1.View, w, h, w, h, target.Target!);
            gpu.WaitForGpu();
            long t4 = Stopwatch.GetTimestamp();
            using (gpu.UseContext()) gpu.Context.CopyResource(copy.Texture, src.Texture);
            gpu.WaitForGpu();
            long t5 = Stopwatch.GetTimestamp();
            if (i >= warmup)
            {
                analyse.Add(Ms(t1 - t0)); infer.Add(Ms(t2 - t1)); mask.Add(Ms(t3 - t2)); synth.Add(Ms(t4 - t3)); copies.Add(Ms(t5 - t4));
            }
            if (Stopwatch.GetTimestamp() - begin > budget && analyse.Count >= 20) break;
        }

        double a = Percentile(analyse, 0.5), n = Percentile(infer, 0.5), m = Percentile(mask, 0.5), s = Percentile(synth, 0.5), c = Percentile(copies, 0.5);
        Console.WriteLine($"\n== {backend.Id} / {model.Id}: window {w}x{h} ({w * (double)h / 1e6:F1} MP) via {iw}x{ih}, {analyse.Count} rounds (ms, p50 / p95 / p99)");
        Console.WriteLine($"   per source frame     downscale + analyse       {Pct(analyse)}");
        Console.WriteLine($"                        pair mask (static snap)   {Pct(mask)}");
        Console.WriteLine($"   per generated frame  inference                 {Pct(infer)}");
        Console.WriteLine($"                        field upload + synthesis  {Pct(synth)}");
        Console.WriteLine($"   per copy (capture -> store, frame -> screen)   {Pct(copies)}");
        foreach (var (fps, factor) in new[] { (30, 2), (30, 4), (60, 2) })
        {
            // One source period: analyse, mask and the copy of the captured frame once; inference + synthesis per
            // generated frame; one copy per frame put on screen.
            double work = a + m + c + (factor - 1) * (n + s) + factor * c;
            Console.WriteLine($"   {fps} fps x{factor}: {work,5:F1} ms of GPU work per source frame of {1000.0 / fps:F1} ms  ({work * fps / 10:F0} % busy)");
        }
    }

    /// <summary>A blocky multi-colour texture, shifted horizontally: something for the flow network to follow.</summary>
    internal static byte[] BlockTexture(int w, int h, int shift)
    {
        var data = new byte[(long)w * h * 4];
        Parallel.For(0, h, y =>
        {
            int row = y * w * 4, by = y / 12;
            for (int x = 0; x < w; x++)
            {
                uint v = (uint)((x + shift) / 12 * 73856093) ^ (uint)(by * 19349663);
                v ^= v >> 13; v *= 0x5bd1e995; v ^= v >> 15;
                data[row + 4 * x] = (byte)v;
                data[row + 4 * x + 1] = (byte)(v >> 8);
                data[row + 4 * x + 2] = (byte)(v >> 16);
                data[row + 4 * x + 3] = 255;
            }
        });
        return data;
    }

    // ------------------------------------------------------------------ inference-only table over the tiers

    private static void RunTiers(InferenceBackend backend, ModelInfo model, FlowSessionOptions sessionOptions,
        Options opt, List<object> results)
    {
        Console.WriteLine("   inference only (ms):     size      MP    create   p50    p95    p99   inferences/s");
        foreach (var (w, h) in TierSizes)
        {
            using var session = FlowSession.Create(backend, model, w, h, sessionOptions);
            var times = new List<double>();
            long budget = Stopwatch.Frequency * 8;
            long begin = Stopwatch.GetTimestamp();
            int warmup = 10;
            for (int i = 0; i < opt.Iterations + warmup; i++)
            {
                long t0 = Stopwatch.GetTimestamp();
                session.SubmitFrame();      // one upload per source frame, as in x2 mode
                session.Run(0.5f);
                if (i >= warmup) times.Add(Ms(Stopwatch.GetTimestamp() - t0));
                if (Stopwatch.GetTimestamp() - begin > budget && times.Count >= 5) break;
            }
            double p50 = Percentile(times, 0.5), p95 = Percentile(times, 0.95), p99 = Percentile(times, 0.99);
            Console.WriteLine($"                         {w,5}x{h,-5} {w * h / 1e6,4:F2} {session.CreateMilliseconds,8:F0} {p50,6:F2} {p95,6:F2} {p99,6:F2} {1000.0 / p50,8:F0}");
            results.Add(new { kind = "tier", backend = backend.Id, model = model.Id, width = w, height = h, createMs = session.CreateMilliseconds, p50, p95, p99, runs = times.Count });
        }
    }

    // ------------------------------------------------------------------ node placement diagnostics

    /// <summary>
    /// Profiles a few runs and reports which execution provider actually ran the nodes.
    /// Returns the milliseconds per inference spent in nodes that ran on the CPU provider.
    /// </summary>
    private static double Diagnose(InferenceBackend backend, ModelInfo model, FlowSessionOptions baseOptions, Options opt, bool quiet = false)
    {
        if (!quiet) Console.WriteLine($"\n== diag {backend.Id} / {model}");
        var options = new FlowSessionOptions
        {
            CacheDirectory = baseOptions.CacheDirectory, StrictGpu = baseOptions.StrictGpu,
            LegacyDmlRegistration = baseOptions.LegacyDmlRegistration,
            ProfilePrefix = Path.Combine(opt.OutDir, $"profile-{backend.Id}-{model.Id}"),
        };
        string trace;
        using (var session = FlowSession.Create(backend, model, 1280, 704, options))
        {
            for (int i = 0; i < 3; i++) session.Run(0.5f);
            trace = session.EndProfiling();
        }
        using var doc = JsonDocument.Parse(File.ReadAllText(trace));
        var perProvider = new Dictionary<string, (int Nodes, long Micros)>();
        var perOp = new Dictionary<string, long>();
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            if (!e.TryGetProperty("name", out var name) || !name.GetString()!.EndsWith("_kernel_time")) continue;
            if (!e.TryGetProperty("args", out var a)) continue;
            string provider = a.TryGetProperty("provider", out var pv) ? pv.GetString() ?? "?" : "?";
            string op = a.TryGetProperty("op_name", out var on) ? on.GetString() ?? "?" : "?";
            long dur = e.GetProperty("dur").GetInt64();
            var cur = perProvider.GetValueOrDefault(provider);
            perProvider[provider] = (cur.Nodes + 1, cur.Micros + dur);
            perOp[$"{provider}/{op}"] = perOp.GetValueOrDefault($"{provider}/{op}") + dur;
        }
        double cpuMs = perProvider.Where(kv => kv.Key.StartsWith("CPU", StringComparison.OrdinalIgnoreCase))
            .Sum(kv => kv.Value.Micros) / 4000.0;       // 4 runs: the warm-up inside Create plus 3 here
        try { File.Delete(trace); } catch (IOException) { }
        if (quiet) return backend.Kind == BackendKind.Cpu ? 0 : cpuMs;

        foreach (var (provider, v) in perProvider.OrderByDescending(kv => kv.Value.Micros))
            Console.WriteLine($"   {provider,-34} {v.Nodes / 4.0,7:F0} node runs/inference   {v.Micros / 4000.0,9:F2} ms/inference");
        foreach (var (op, micros) in perOp.OrderByDescending(kv => kv.Value).Take(8))
            Console.WriteLine($"      {op,-48} {micros / 4000.0,9:F2} ms/inference");
        return cpuMs;
    }

    /// <summary>
    /// Runs the fp16 network on CPU-prepared golden inputs with different session set-ups and reports the error
    /// against the reference flow, to separate provider problems from binding or optimisation problems.
    /// </summary>
    private static unsafe void Probe(IReadOnlyList<InferenceBackend> backends, ModelRegistry registry, List<GoldenCase> golden)
    {
        var c = golden.First(g => g.Name == "x1" && g.Model == "rife-v4.25");
        var model = registry.Find(c.Model, "fp16");
        int w = c.InferenceWidth, h = c.InferenceHeight;
        // ORT's C# API has its own Float16 struct (System.Half is rejected); it is a ushort bit pattern.
        Float16[] img0 = ToPlanar(Png.ReadBgra(c.Frame0, out _, out _), w, h);
        Float16[] img1 = ToPlanar(Png.ReadBgra(c.Frame1, out _, out _), w, h);
        Float16[] ts = [new Float16(BitConverter.HalfToUInt16Bits((Half)c.T))];
        byte[] expected = File.ReadAllBytes(c.Flow);
        var dml = backends.FirstOrDefault(b => b.Kind == BackendKind.DirectML);
        var cpu = backends.First(b => b.Kind == BackendKind.Cpu);
        Console.WriteLine($"\n== probe {model} on golden x1 (flow p99.9 error in px; < 1 is correct)");

        void Try(string label, InferenceBackend? backend, GraphOptimizationLevel level, bool binding, bool singleThread)
        {
            if (backend is null) return;
            try
            {
                using var so = new SessionOptions { GraphOptimizationLevel = level, LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR };
                so.AddFreeDimensionOverrideByName("height", h);
                so.AddFreeDimensionOverrideByName("width", w);
                if (backend.Kind == BackendKind.DirectML) { so.EnableMemoryPattern = false; so.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL; }
                if (singleThread) { so.IntraOpNumThreads = 1; so.AddSessionConfigEntry("session.intra_op.allow_spinning", "0"); }
                so.AppendExecutionProvider(OrtEnv.Instance(), [backend.Device], null);
                using var session = new InferenceSession(model.Path, so);
                using var run = new RunOptions();
                using var v0 = OrtValue.CreateTensorValueFromMemory(img0, [1, 3, h, w]);
                using var v1 = OrtValue.CreateTensorValueFromMemory(img1, [1, 3, h, w]);
                using var vt = OrtValue.CreateTensorValueFromMemory(ts, [1, 1, 1, 1]);
                double p999, max;
                if (binding)
                {
                    var flow = new Float16[4 * w * h];
                    var mask = new Float16[w * h];
                    using var vf = OrtValue.CreateTensorValueFromMemory(flow, [1, 4, h, w]);
                    using var vm = OrtValue.CreateTensorValueFromMemory(mask, [1, 1, h, w]);
                    using var io = session.CreateIoBinding();
                    io.BindInput("img0", v0); io.BindInput("img1", v1); io.BindInput("timestep", vt);
                    io.BindOutput("flow", vf); io.BindOutput("mask_logit", vm);
                    session.RunWithBinding(run, io);
                    io.SynchronizeBoundOutputs();
                    fixed (Float16* p = flow) (p999, max) = CompareHalf((nint)p, expected);
                }
                else
                {
                    using var outputs = session.Run(run, ["img0", "img1", "timestep"], [v0, v1, vt], ["flow", "mask_logit"]);
                    var span = outputs[0].GetTensorDataAsSpan<Float16>();
                    fixed (Float16* p = span) (p999, max) = CompareHalf((nint)p, expected);
                }
                Console.WriteLine($"   {label,-58} p99.9 {p999,8:F3}  max {max,8:F2}");
            }
            catch (Exception e)
            {
                Console.WriteLine($"   {label,-58} {e.GetType().Name}: {e.Message.Split('\n')[0]}");
            }
        }

        Try("dml  ENABLE_ALL   plain Run", dml, GraphOptimizationLevel.ORT_ENABLE_ALL, false, false);
        Try("dml  ENABLE_ALL   io binding", dml, GraphOptimizationLevel.ORT_ENABLE_ALL, true, false);
        Try("dml  ENABLE_ALL   io binding, 1 thread no spin", dml, GraphOptimizationLevel.ORT_ENABLE_ALL, true, true);
        Try("dml  EXTENDED     plain Run", dml, GraphOptimizationLevel.ORT_ENABLE_EXTENDED, false, false);
        Try("dml  BASIC        plain Run", dml, GraphOptimizationLevel.ORT_ENABLE_BASIC, false, false);
        Try("dml  DISABLE_ALL  plain Run", dml, GraphOptimizationLevel.ORT_DISABLE_ALL, false, false);
        Try("cpu  ENABLE_ALL   plain Run (fp16 model)", cpu, GraphOptimizationLevel.ORT_ENABLE_ALL, false, false);
    }

    /// <summary>BGRA8 pixels -> planar RGB fp16 in [0,1] (what the analyser produces at ratio 1).</summary>
    private static Float16[] ToPlanar(byte[] bgra, int w, int h)
    {
        var planar = new Float16[3 * w * h];
        int n = w * h;
        static Float16 F(byte v) => new(BitConverter.HalfToUInt16Bits((Half)(v / 255f)));
        for (int i = 0; i < n; i++)
        {
            planar[i] = F(bgra[i * 4 + 2]);
            planar[n + i] = F(bgra[i * 4 + 1]);
            planar[2 * n + i] = F(bgra[i * 4]);
        }
        return planar;
    }

    // ------------------------------------------------------------------ helpers

    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    private static double Percentile(List<double> values, double q)
    {
        if (values.Count == 0) return double.NaN;
        var sorted = values.Order().ToArray();
        return sorted[Math.Clamp((int)Math.Ceiling(q * sorted.Length) - 1, 0, sorted.Length - 1)];
    }

    private static string Pct(List<double> v) => $"{Percentile(v, 0.5),6:F2} / {Percentile(v, 0.95),6:F2} / {Percentile(v, 0.99),6:F2}";

    /// <summary>PSNR over the RGB channels of two BGRA images.</summary>
    private static double Psnr(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return 0;
        double sum = 0;
        long n = 0;
        for (int i = 0; i < a.Length; i += 4)
        {
            for (int ch = 0; ch < 3; ch++)
            {
                int d = a[i + ch] - b[i + ch];
                sum += d * d;
            }
            n += 3;
        }
        return sum == 0 ? 99.0 : 10.0 * Math.Log10(255.0 * 255.0 * n / sum);
    }

    /// <summary>99.9th percentile and maximum of |a - b| for two fp16 arrays.</summary>
    private static unsafe (double P999, double Max) CompareHalf(nint actual, byte[] expected)
    {
        int n = expected.Length / 2;
        var diff = new float[n];
        fixed (byte* e = expected)
        {
            ushort* a = (ushort*)actual, b = (ushort*)e;
            for (int i = 0; i < n; i++)
                diff[i] = MathF.Abs((float)BitConverter.UInt16BitsToHalf(a[i]) - (float)BitConverter.UInt16BitsToHalf(b[i]));
        }
        Array.Sort(diff);
        return (diff[Math.Clamp((int)(0.999 * n), 0, n - 1)], diff[n - 1]);
    }

    private static Options Parse(string[] args)
    {
        string[] backends = ["all"], models = ["all"];
        int iterations = 300;
        bool install = true, strict = false, tiers = true, diag = false, legacyDml = false;
        string outDir = Path.Combine(Environment.CurrentDirectory, "artifacts", "bench");
        string? modelsDir = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--window":
                    WindowSizes = [.. args[++i].Split(',').Select(v => v.Split('x')).Select(v => (int.Parse(v[0]), int.Parse(v[1])))];
                    break;
                case "--vsr-probe": VsrInput = args[++i]; break;
                case "--scale": VsrScale = int.Parse(args[++i]); break;
                case "--record-probe":
                    RecordSizes = [.. args[++i].Split(',').Select(v => v.Split('x')).Select(v => (int.Parse(v[0]), int.Parse(v[1])))];
                    break;
                case "--hud-probe": HudOut = args[++i]; break;
                case "--enhance-probe":
                    EnhanceSizes = [.. args[++i].Split(',').Select(v => v.Split('x')).Select(v => (int.Parse(v[0]), int.Parse(v[1])))];
                    break;
                case "--enhance-scale": EnhanceScale = int.Parse(args[++i]); break;
                case "--enhance-picture": EnhancePicture = args[++i]; break;
                case "--enhance-seconds": EnhanceSeconds = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--enhance-rate": EnhanceRate = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--gpu-priority": RaisePriority = true; break;
                case "--enhance-watch": EnhanceWatch = true; break;
                case "--audio-probe":
                    AudioSeconds = i + 1 < args.Length && double.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out double s) ? s : 3;
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) i++;
                    break;
                case "--fps": RecordRate = int.Parse(args[++i]); break;
                case "--seconds": RecordSeconds = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--all": backends = ["all"]; models = ["all"]; break;
                case "--diag": diag = true; break;
                case "--legacy-dml": legacyDml = true; break;
                case "--backends": backends = args[++i].Split(','); break;
                case "--models": models = args[++i].Split(','); break;
                case "--iters": iterations = int.Parse(args[++i]); break;
                case "--no-install": install = false; break;
                case "--strict": strict = true; break;
                case "--no-tiers": tiers = false; break;
                case "--out": outDir = args[++i]; break;
                case "--models-dir": modelsDir = args[++i]; break;
                case "-h" or "--help":
                    Console.WriteLine("InterpolatIt.Bench [--backends nv,dml,cpu|all] [--models rife-v4.25,rife-v4.6|all] [--iters N]\n" +
                                      "                   [--no-install] [--strict] [--no-tiers] [--out DIR] [--models-dir DIR]\n" +
                                      "                   [--diag] (node placement per provider)  [--legacy-dml]\n" +
                                      "                   [--window WxH[,WxH...]]  cost of each stage for windows of these sizes, nothing else\n" +
                                      "                   [--vsr-probe FILE.png [--scale 2]]  does the driver's video enhancement engage, and what does it cost\n" +
                                      "                   [--record-probe WxH[,WxH...] [--fps 120] [--seconds 5]]  does the MP4 recorder keep up at these sizes\n" +
                                      "                   [--hud-probe FILE.png]  the fps readout as a picture\n" +
                                      "                   [--enhance-probe WxH[,WxH...] | --enhance-picture FILE.png] [--enhance-scale 1]  the engine's clean-up step:\n" +
                                      "                                           does the driver engage, through which size, cost per frame, close-ups with a picture\n" +
                                      "                       [--enhance-seconds S [--enhance-rate 60]]  ... and at a steady pace, for the slow end\n" +
                                      "                       [--enhance-watch]   ... and after rests; with --enhance-seconds: does the same input keep giving the same output\n" +
                                      "                       [--gpu-priority]    with the GPU scheduling priority a session asks for\n" +
                                      "                   [--audio-probe [S]]  capture the sound of this process (it plays a tone too soft to hear)");
                    Environment.Exit(0);
                    break;
                default: throw new ArgumentException($"unknown argument {args[i]}");
            }
        }
        return new Options(backends, models, iterations, install, strict, tiers, outDir, modelsDir, diag, legacyDml);
    }
}

/// <summary>A BGRA8 texture with the views the bench needs; optionally a render target with CPU read-back.</summary>
internal sealed unsafe class GpuImage : IDisposable
{
    private readonly GpuDevice _gpu;
    private readonly ID3D11Texture2D _texture;
    private readonly ID3D11Texture2D? _staging;
    private readonly int _width, _height;

    public ID3D11ShaderResourceView View { get; }
    public ID3D11RenderTargetView? Target { get; }
    public ID3D11Texture2D Texture => _texture;

    public GpuImage(GpuDevice gpu, int width, int height, byte[]? bgra, bool renderTarget = false)
    {
        _gpu = gpu;
        _width = width;
        _height = height;
        var desc = new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)width, (uint)height, 1, 1,
            renderTarget ? BindFlags.ShaderResource | BindFlags.RenderTarget : BindFlags.ShaderResource);
        if (bgra is not null)
        {
            fixed (byte* p = bgra)
                _texture = gpu.Device.CreateTexture2D(in desc, new SubresourceData((nint)p, (uint)(width * 4), 0));
        }
        else
        {
            _texture = gpu.Device.CreateTexture2D(in desc);
        }
        View = gpu.Device.CreateShaderResourceView(_texture);
        if (renderTarget)
        {
            Target = gpu.Device.CreateRenderTargetView(_texture);
            var s = desc;
            s.BindFlags = BindFlags.None;
            s.Usage = ResourceUsage.Staging;
            s.CPUAccessFlags = CpuAccessFlags.Read;
            _staging = gpu.Device.CreateTexture2D(in s);
        }
    }

    public byte[] ReadBack()
    {
        if (_staging is null) throw new InvalidOperationException("not a render target");
        using (_gpu.UseContext()) _gpu.Context.CopyResource(_staging, _texture);
        _gpu.WaitForGpu();
        byte[] pixels = new byte[_width * _height * 4];
        using (_gpu.UseContext())
        {
            var map = _gpu.Context.Map(_staging, 0, MapMode.Read);
            fixed (byte* dst = pixels)
            {
                for (int y = 0; y < _height; y++)
                    Buffer.MemoryCopy((byte*)map.DataPointer + (long)y * map.RowPitch, dst + (long)y * _width * 4, _width * 4, _width * 4);
            }
            _gpu.Context.Unmap(_staging, 0);
        }
        return pixels;
    }

    public void Dispose()
    {
        Target?.Dispose();
        View.Dispose();
        _staging?.Dispose();
        _texture.Dispose();
    }
}
