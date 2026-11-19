using System.Diagnostics;
using InterpolatIt.Core.Gpu;
using InterpolatIt.Core.Util;

namespace InterpolatIt.Bench;

/// <summary>
/// The engine's clean-up step (<see cref="VideoEnhancer"/>) on its own, without a window: for pictures of the
/// given sizes, does the driver really enhance them (the enhancer's own check), through which size do they go,
/// and what does one frame cost? With a picture file it also writes close-ups of what goes in and what comes out.
/// </summary>
internal static class EnhanceProbe
{
    public static int Run(GpuDevice gpu, (int Width, int Height)[] sizes, int scale, string? pictureFile, string outDir,
        double pacedSeconds = 0, double pacedRate = 60, bool watchOutput = false)
    {
        using var enhancer = VideoEnhancer.TryCreate(gpu, out string? unavailable);
        if (enhancer is null)
        {
            Console.WriteLine($"clean-up by the driver: {unavailable}");
            return 1;
        }

        byte[]? picture = null;
        if (pictureFile is not null)
        {
            picture = Png.ReadBgra(pictureFile, out int pw, out int ph);
            sizes = [(pw, ph)];
            Directory.CreateDirectory(outDir);
        }

        Console.WriteLine($"\n== clean-up by the driver on {gpu.Adapter.Description}, output x{scale}");
        foreach (var (w, h) in sizes)
        {
            int outW = w * scale, outH = h * scale;
            enhancer.Configure(w, h, outW, outH);
            long t0 = Stopwatch.GetTimestamp();
            EnhancerCheck check = enhancer.Check();
            double checkMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;

            // A whole frame as the pipeline does it: copy in, (shrink,) enhance, copy out.
            using var source = new GpuImage(gpu, w, h, picture ?? VideoEnhancer.TestPicture(w, h), renderTarget: true);
            using var result = new GpuImage(gpu, outW, outH, null, renderTarget: true);
            var times = new List<double>();
            for (int i = 0; i < 40; i++)
            {
                long t1 = Stopwatch.GetTimestamp();
                enhancer.Process(source.Texture, result.Texture);
                gpu.WaitForGpu();
                if (i >= 10) times.Add(Stopwatch.GetElapsedTime(t1).TotalMilliseconds);
            }
            times.Sort();

            string via = enhancer.InputWidth == w && enhancer.InputHeight == h ? "as it is" : $"via {enhancer.InputWidth}x{enhancer.InputHeight}";
            Console.WriteLine($"   {w,4}x{h,-4} {via,-14}: {(check.Engaged ? "ENGAGED    " : "not engaged")} {check.ChangedShare,6:P1} of the test picture changed, " +
                              $"{times[times.Count / 2],5:F2} ms per frame (p95 {times[(int)(times.Count * 0.95)]:F2}), check {checkMs:F0} ms");

            if (watchOutput)
            {
                // How does the driver come back from an idle period? Conversions back to back after a rest.
                foreach (int rest in new[] { 100, 300, 1000, 3000 })
                {
                    Thread.Sleep(rest);
                    var ramp = new List<string>();
                    long began = Stopwatch.GetTimestamp();
                    for (int i = 0; i < 16; i++)
                    {
                        long t1 = Stopwatch.GetTimestamp();
                        enhancer.Process(source.Texture, result.Texture);
                        gpu.WaitForGpu();
                        ramp.Add($"{Stopwatch.GetElapsedTime(t1).TotalMilliseconds:F0}");
                    }
                    Console.WriteLine($"             after {rest,4} ms of rest, back to back (ms): {string.Join(" ", ramp)}   = {Stopwatch.GetElapsedTime(began).TotalMilliseconds:F0} ms in all");
                }
            }

            if (pacedSeconds > 0)
            {
                // The way the pipeline uses it: one frame per source period, the GPU idle (or busy with other
                // things) in between. What matters is the slow end: one frame that takes 40 ms is a visible hitch.
                using var timer = new InterpolatIt.Core.Native.HighResolutionTimer();
                var paced = new List<double>();
                var slow = new List<string>();
                var outputs = new List<string>();
                ulong lastOutput = 0;
                double start = InterpolatIt.Core.Native.Qpc.Now + 0.05;
                int frames = (int)(pacedSeconds * pacedRate);
                for (int i = 0; i < frames; i++)
                {
                    timer.WaitUntil(start + i / pacedRate);
                    long t1 = Stopwatch.GetTimestamp();
                    enhancer.Process(source.Texture, result.Texture);
                    gpu.WaitForGpu();
                    double ms = Stopwatch.GetElapsedTime(t1).TotalMilliseconds;
                    paced.Add(ms);
                    if (ms > 15 && slow.Count < 12) slow.Add($"{ms:F0} ms at {i / pacedRate:F1} s");
                    if (watchOutput)
                    {
                        // The same picture goes in every time. Does the same picture come out every time? (A driver
                        // that changes its quality level under load would show here.)
                        byte[] pixels = result.ReadBack();
                        ulong hash = 14695981039346656037UL;
                        for (int k = 0; k < pixels.Length; k += 4 * 13) hash = (hash ^ pixels[k] ^ ((ulong)pixels[k + 1] << 8) ^ ((ulong)pixels[k + 2] << 16)) * 1099511628211UL;
                        if (hash != lastOutput && outputs.Count < 24) outputs.Add($"{i / pacedRate:F1} s ({ms:F0} ms, {hash % 10000:D4})");
                        lastOutput = hash;
                    }
                }
                if (watchOutput)
                    Console.WriteLine($"             the output for the same input changed {outputs.Count - 1} time(s): first seen at {string.Join(", ", outputs)}");
                var sorted = paced.Order().ToList();
                Console.WriteLine($"             {frames} frames at {pacedRate:F0} per second: p50 {sorted[frames / 2]:F2}  p95 {sorted[(int)(frames * 0.95)]:F2}  " +
                                  $"p99 {sorted[(int)(frames * 0.99)]:F2}  max {sorted[^1]:F1} ms; over 10 ms: {paced.Count(v => v > 10)}, over 15 ms: {paced.Count(v => v > 15)}, over 25 ms: {paced.Count(v => v > 25)}");
                if (slow.Count > 0) Console.WriteLine($"             slow ones: {string.Join(", ", slow)}");
            }

            if (picture is not null)
            {
                string name = Path.Combine(outDir, $"enhance-{w}x{h}");
                SaveCrop(picture, w, h, name + "-in-crop.png");
                SaveCrop(result.ReadBack(), outW, outH, name + "-out-crop.png");
                Console.WriteLine($"   close-ups: {name}-in-crop.png, {name}-out-crop.png");
            }
        }
        return 0;
    }

    /// <summary>The middle 960x540 of a picture, at full resolution, for a close look.</summary>
    private static void SaveCrop(byte[] bgra, int width, int height, string path)
    {
        int cw = Math.Min(960, width), ch = Math.Min(540, height), x0 = (width - cw) / 2, y0 = (height - ch) / 2;
        var crop = new byte[cw * ch * 4];
        for (int y = 0; y < ch; y++)
            Buffer.BlockCopy(bgra, ((y0 + y) * width + x0) * 4, crop, y * cw * 4, cw * 4);
        Png.WriteBgra(path, crop, cw, ch, cw * 4);
    }
}
