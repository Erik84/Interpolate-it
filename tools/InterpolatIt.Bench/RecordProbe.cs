using System.Diagnostics;
using InterpolatIt.Core.Gpu;
using InterpolatIt.Core.Native;
using InterpolatIt.Core.Recording;

namespace InterpolatIt.Bench;

/// <summary>
/// Does the recorder keep up with pictures of a given size at a given rate, and which codec does it end up with?
/// <para>
/// The pictures are handed over at the pace a display would show them, exactly as the presenter does, but
/// without a window: sizes for which the desktop has no room (a 3840x2160 window at 120 fps is the first one
/// that needs HEVC) can be tried without covering anything. Every picture differs from the one before it over
/// its whole area, which asks more of an encoder than a real window does.
/// </para>
/// </summary>
internal static class RecordProbe
{
    public static int Run(GpuDevice gpu, (int Width, int Height)[] sizes, int rate, double seconds, string outDir)
    {
        Directory.CreateDirectory(outDir);
        Console.WriteLine($"\n== recording at {rate} pictures per second for {seconds:F0} s on {gpu.Adapter.Description}");
        int failures = 0;
        foreach (var (w, h) in sizes)
        {
            var pictures = new GpuImage[8];
            for (int i = 0; i < pictures.Length; i++) pictures[i] = new GpuImage(gpu, w, h, Program.BlockTexture(w, h, i * 5));
            string path = Path.Combine(outDir, $"record-{w}x{h}-{rate}.mp4");
            try
            {
                if (!Record(gpu, pictures, w, h, rate, seconds, path)) failures++;
            }
            finally
            {
                foreach (GpuImage picture in pictures) picture.Dispose();
            }
        }
        return failures;
    }

    private static bool Record(GpuDevice gpu, GpuImage[] pictures, int w, int h, int rate, double seconds, string path)
    {
        using var recorder = Mp4Recorder.Start(gpu, path, w, h, rate);
        RecordingStatus status = recorder.Status;
        long begin = Stopwatch.GetTimestamp();
        while (!status.Active && !status.Finished && Stopwatch.GetElapsedTime(begin).TotalSeconds < 20) Thread.Sleep(5);
        double startup = Stopwatch.GetElapsedTime(begin).TotalMilliseconds;
        Console.WriteLine($"   {w}x{h}");
        if (!status.Active)
        {
            Console.WriteLine($"      did not start: {status.Error ?? "the encoder was not ready within 20 s"}");
            return false;
        }

        using var timer = new HighResolutionTimer();
        int frames = (int)Math.Round(seconds * rate);
        double start = Qpc.Now + 0.05, slowest = 0;
        for (int i = 0; i < frames && !status.Finished; i++)
        {
            double time = start + i / (double)rate;
            timer.WaitUntil(time);
            long t0 = Stopwatch.GetTimestamp();
            recorder.AddFrame(pictures[i % pictures.Length].Texture, w, h, time);
            slowest = Math.Max(slowest, Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
        }
        bool written = recorder.StopAndWait(60_000);

        long bytes = File.Exists(path) ? new FileInfo(path).Length : 0;
        bool ok = written && status.Error is null && status.Dropped == 0 && status.Frames >= frames - 1;
        Console.WriteLine($"      encoder ready after {startup:F0} ms, codec {status.Codec}");
        Console.WriteLine($"      {status.Frames} of {frames} pictures written, {status.Dropped} skipped because the encoder was behind; " +
                          $"handing one over took at most {slowest:F2} ms");
        Console.WriteLine($"      {bytes / 1e6:F1} MB ({bytes * 8 / Math.Max(0.001, frames / (double)rate) / 1e6:F0} Mbit/s)  {path}");
        if (status.Error is not null) Console.WriteLine($"      error: {status.Error}");
        if (!written) Console.WriteLine("      the file was not finished within 60 s");
        Console.WriteLine($"      => {(ok ? "OK" : "NOT OK")}");
        return ok;
    }
}
