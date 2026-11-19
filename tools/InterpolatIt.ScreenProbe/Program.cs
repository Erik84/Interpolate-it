using System.Globalization;
using InterpolatIt.Core.Capture;
using InterpolatIt.Core.Gpu;
using InterpolatIt.Core.Native;
using InterpolatIt.Core.Presentation;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace InterpolatIt.ScreenProbe;

/// <summary>
/// Reads the composed desktop where the TestPattern window is and decodes, for every desktop update, which
/// pattern frame and which in-between position is on screen. It shares no code path with the engine's
/// presentation, so it is independent evidence of what the user sees: output rate, how many refreshes each
/// picture stays, whether in-between frames sit where they should, and whether static pixels stay untouched.
/// </summary>
internal static unsafe class Program
{
    // Layout of InterpolatIt.TestPattern (client coordinates).
    private const int Cell = 24, Cells = 20, StripX = 16, StripY = 16, BarWidth = 24, PatchSize = 64;

    private readonly record struct Sample(double Time, uint Accumulated, bool Valid, int Index, double T, int MidCells,
        double BarX, double BarSpread, int StaticDiff);

    private static int Main(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        var a = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--")) { Console.Error.WriteLine($"unexpected argument {args[i]}"); return 2; }
            bool hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--");
            a[args[i][2..]] = hasValue ? args[++i] : null;
        }
        double D(string k, double fallback) => a.TryGetValue(k, out string? v) && v is not null ? double.Parse(v, CultureInfo.InvariantCulture) : fallback;
        if (a.ContainsKey("help"))
        {
            Console.WriteLine(
                """
                InterpolatIt.ScreenProbe [--title TestPattern] [--seconds 6] [--csv FILE] [--pattern-log FILE]
                    assertions (non-zero exit when violated):
                    [--expect-rate FPS]        distinct pictures per second (within 3 %)
                    [--expect-hold N]          refreshes each picture stays on screen (95 % of them)
                    [--expect-interp SHARE]    share of pictures that are in-between frames (+-0.06)
                    [--factor N]               in-between pictures are expected at exact k/N positions
                    [--max-bar-error PX]       99th percentile of the moving bar's position error
                    [--static-exact]           the static patch never differs by more than 1/255
                    [--max-static LEVELS]      ... or by more than this many levels of 255
                    [--standstill-real]        the pattern pauses during the measurement: what stays on screen
                                               meanwhile must be a real frame, not an in-between picture
                    [--dump-anomaly FILE.png]  save what is on screen when the pattern suddenly looks wrong
                exit code 3: another window covered the pattern during the measurement (inconclusive)
                """);
            return 0;
        }

        string titleText = a.GetValueOrDefault("title") ?? "TestPattern";
        double seconds = D("seconds", 6);
        var window = WindowEnumerator.FindByTitle(titleText);
        if (window is null) { Console.Error.WriteLine($"no window with '{titleText}' in its title"); return 2; }

        User32.GetClientRect(window.Handle, out RECT client);
        var origin = new POINT();
        User32.ClientToScreen(window.Handle, ref origin);
        int clientW = client.Width, clientH = client.Height;
        int bandY = (int)Math.Floor(clientH * 0.30) + 30;
        double patternFps = ParseFps(window.Title);
        int barStep = Math.Max(2, (int)Math.Round(480.0 / patternFps));
        int span = Math.Max(1, clientW - BarWidth);

        double period = DisplayClock.MeasureRefreshPeriod();
        using var gpu = GpuDevice.Create();
        using var dxgiDevice = gpu.Device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        IDXGIOutput1? output = null;
        int outLeft = 0, outTop = 0;
        for (uint i = 0; adapter.EnumOutputs(i, out IDXGIOutput? o).Success; i++)
        {
            var r = o!.Description.DesktopCoordinates;
            if (origin.X >= r.Left && origin.X < r.Right && origin.Y >= r.Top && origin.Y < r.Bottom)
            {
                output = o.QueryInterface<IDXGIOutput1>();
                outLeft = r.Left; outTop = r.Top;
            }
            o.Dispose();
        }
        if (output is null) { Console.Error.WriteLine("the window is not on an output of the high-performance adapter"); return 2; }

        int stagingW = Math.Max(clientW, Cell * Cells);
        var stagingDesc = new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)stagingW, 2 + PatchSize, 1, 1,
            BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read);
        using var staging = gpu.Device.CreateTexture2D(in stagingDesc);
        using var duplication = output.DuplicateOutput(gpu.Device);
        output.Dispose();

        int x0 = origin.X - outLeft, y0 = origin.Y - outTop;
        var stripBox = new Vortice.Mathematics.Box(x0 + StripX, y0 + StripY + Cell / 2, 0, x0 + StripX + Cell * Cells, y0 + StripY + Cell / 2 + 1, 1);
        var bandBox = new Vortice.Mathematics.Box(x0, y0 + bandY, 0, x0 + clientW, y0 + bandY + 1, 1);
        var patchBox = new Vortice.Mathematics.Box(x0 + 48, y0 + clientH - 190, 0, x0 + 48 + PatchSize, y0 + clientH - 190 + PatchSize, 1);

        // --dump-anomaly FILE.png: when the static patch suddenly looks completely different (something covers the
        // pattern, or the overlay shows a wrong picture), save what is on screen there and say which window it is.
        string? dumpPath = a.GetValueOrDefault("dump-anomaly");
        ID3D11Texture2D? view = null, viewStaging = null;
        var viewBox = new Vortice.Mathematics.Box(x0, y0, 0, x0 + clientW, y0 + clientH, 1);
        if (dumpPath is not null)
        {
            view = gpu.Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)clientW, (uint)clientH, 1, 1, BindFlags.ShaderResource));
            viewStaging = gpu.Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)clientW, (uint)clientH, 1, 1,
                BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
        }
        bool dumped = false;
        string? coveredBy = null;
        double coveredAt = 0;
        POINT[] watchPoints =
        [
            new POINT { X = origin.X + StripX + Cell * Cells / 2, Y = origin.Y + StripY + Cell / 2 },      // the code strip
            new POINT { X = origin.X + clientW / 2, Y = origin.Y + bandY },                                // the band with the bar
            new POINT { X = origin.X + 48 + PatchSize / 2, Y = origin.Y + clientH - 190 + PatchSize / 2 }, // the static patch
        ];

        var samples = new List<Sample>(4096);
        byte[]? reference = null;
        byte[] patch = new byte[PatchSize * PatchSize * 4];
        double start = Qpc.Now;
        int errors = 0;
        while (Qpc.Now - start < seconds)
        {
            var hr = duplication.AcquireNextFrame(100, out OutduplFrameInfo info, out IDXGIResource? resource);
            if (hr.Failure)
            {
                if (++errors > 200) { Console.Error.WriteLine($"desktop duplication keeps failing: {hr}"); return 2; }
                continue;
            }
            try
            {
                if (info.LastPresentTime == 0) continue;        // pointer movement only
                using var texture = resource!.QueryInterface<ID3D11Texture2D>();
                gpu.Context.CopySubresourceRegion(staging, 0, 0, 0, 0, texture, 0, stripBox);
                gpu.Context.CopySubresourceRegion(staging, 0, 0, 1, 0, texture, 0, bandBox);
                gpu.Context.CopySubresourceRegion(staging, 0, 0, 2, 0, texture, 0, patchBox);
                if (view is not null && !dumped) gpu.Context.CopySubresourceRegion(view, 0, 0, 0, 0, texture, 0, viewBox);
            }
            finally
            {
                resource?.Dispose();
                duplication.ReleaseFrame();
            }

            var map = gpu.Context.Map(staging, 0, MapMode.Read);
            try
            {
                byte* row0 = (byte*)map.DataPointer, row1 = row0 + map.RowPitch;
                for (int y = 0; y < PatchSize; y++)
                    new ReadOnlySpan<byte>(row0 + (2 + y) * (long)map.RowPitch, PatchSize * 4).CopyTo(patch.AsSpan(y * PatchSize * 4));
                reference ??= (byte[])patch.Clone();
                int staticDiff = 0;
                for (int i = 0; i < patch.Length; i++)
                    if ((i & 3) != 3) staticDiff = Math.Max(staticDiff, Math.Abs(patch[i] - reference[i]));

                DecodeStrip(row0, out bool valid, out int index, out double t, out int mids);
                DecodeBar(row1, clientW, out double barX, out double spread);
                samples.Add(new Sample(Qpc.FromTicks(info.LastPresentTime), info.AccumulatedFrames, valid, index, t, mids, barX, spread, staticDiff));

                // Is the pattern still what is on screen there? Another window on top of it (someone is using
                // this desktop) makes every number below meaningless; say so instead of reporting failures.
                // (The engine's overlay is transparent to hit-testing, so it never counts as "another window".)
                if (coveredBy is null && (samples.Count % 24 == 0 || staticDiff > 100 || !valid))
                {
                    foreach (var point in watchPoints)
                    {
                        nint top = User32.GetAncestor(User32.WindowFromPoint(point), User32.GA_ROOT);
                        if (top == window.Handle) continue;
                        coveredBy = "another window";       // which one is the user's business, not this log's
                        coveredAt = Qpc.Now - start;
                        break;
                    }
                }
                // A picture of the anomaly - only when it is the pattern (under the overlay) that looks wrong. When
                // another window covers it, that window's content is none of this tool's business.
                if (view is not null && !dumped && coveredBy is null && (staticDiff > 100 || !valid) && samples.Count > 10)
                {
                    dumped = true;
                    DumpAnomaly(gpu, view, viewStaging!, clientW, clientH, dumpPath!, window.Handle,
                        new POINT { X = origin.X + clientW / 2, Y = origin.Y + clientH / 2 }, Qpc.Now - start, staticDiff, valid);
                }
            }
            finally
            {
                gpu.Context.Unmap(staging, 0);
            }
        }
        view?.Dispose();
        viewStaging?.Dispose();
        if (coveredBy is not null)
        {
            Console.WriteLine($"INCONCLUSIVE: after {coveredAt:F1} s {coveredBy} was on top of the test pattern; nothing was measured.");
            return 3;
        }

        if (samples.Count < 20) { Console.WriteLine($"only {samples.Count} desktop updates were seen"); return 1; }
        var (fitted, refreshes, phaseSpread) = FitRefreshGrid(samples, period);
        period = fitted;

        if (a.GetValueOrDefault("csv") is string csvPath)
        {
            using var csv = new StreamWriter(csvPath);
            csv.WriteLine("time_s,refresh,accumulated,valid,index,t,mid_cells,bar_x,bar_spread,static_diff,qpc_s");
            for (int i = 0; i < samples.Count; i++)
            {
                var s = samples[i];
                csv.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{s.Time - samples[0].Time:F6},{refreshes[i]},{s.Accumulated},{(s.Valid ? 1 : 0)},{s.Index},{s.T:F3},{s.MidCells},{s.BarX:F2},{s.BarSpread:F2},{s.StaticDiff},{s.Time:F6}"));
            }
        }
        if (phaseSpread > 0.8)
            Console.WriteLine($"warning: desktop updates are spread over {phaseSpread:P0} of the refresh interval; refresh numbers may be off by one");
        return Analyse(samples, refreshes, period, patternFps, barStep, span, a, D);
    }

    private static void DumpAnomaly(GpuDevice gpu, ID3D11Texture2D view, ID3D11Texture2D staging, int width, int height,
        string path, nint pattern, POINT centre, double elapsed, int staticDiff, bool valid)
    {
        gpu.Context.CopyResource(staging, view);
        var map = gpu.Context.Map(staging, 0, MapMode.Read);
        try
        {
            InterpolatIt.Core.Util.Png.WriteBgra(path, new ReadOnlySpan<byte>((void*)map.DataPointer, (int)map.RowPitch * height), width, height, (int)map.RowPitch);
        }
        finally
        {
            gpu.Context.Unmap(staging, 0);
        }
        User32.GetWindowRect(pattern, out RECT now);
        Console.WriteLine($"anomaly: at {elapsed:F2} s the static patch differs by {staticDiff}/255 (strip {(valid ? "readable" : "unreadable")}); saved {path}");
        Console.WriteLine($"anomaly: pattern window rect now ({now.Left},{now.Top}) {now.Width}x{now.Height}");
    }

    /// <summary>
    /// On which display refresh was each desktop update shown? The compositor presents some time after a vblank -
    /// usually a millisecond or two, later when the GPU is busy or clocked down - and the picture is on the glass
    /// from the next one. Rounding a present time to the nearest refresh therefore goes wrong as soon as the
    /// compositor runs late, and a refresh period that is off by one part in a thousand shifts the last sample
    /// of a six-second run by most of a refresh.
    /// <para>
    /// So the grid is fitted to the samples: the period is the one that lines the present times up best (their
    /// phases within the refresh interval are most concentrated), and the grid's origin is put just before the
    /// earliest phase that occurs, so that late presents stay in the interval they belong to.
    /// </para>
    /// Returns the period, a refresh number per sample (0-based) and the part of the interval the phases cover.
    /// </summary>
    private static (double Period, long[] Refreshes, double PhaseSpread) FitRefreshGrid(List<Sample> samples, double period0)
    {
        double t0 = samples[0].Time;
        var times = samples.Select(s => s.Time - t0).ToArray();

        double best = period0, bestScore = -1;
        foreach (double range in new[] { 4e-3, 1.5e-4, 6e-6 })      // relative search range, coarse to fine
        {
            double centre = best;
            for (int k = -160; k <= 160; k++)
            {
                double p = centre * (1 + range * k / 160);
                double sx = 0, sy = 0;
                foreach (double t in times)
                {
                    double angle = 2 * Math.PI * (t / p);
                    sx += Math.Cos(angle); sy += Math.Sin(angle);
                }
                double score = sx * sx + sy * sy;
                if (score > bestScore) { bestScore = score; best = p; }
            }
        }

        // Phases within the interval, and the largest stretch in which none falls: the presents start right after it.
        var phases = times.Select(t => t / best - Math.Floor(t / best)).Order().ToArray();
        double gap = phases[0] + 1 - phases[^1], firstPhase = phases[0];
        for (int i = 1; i < phases.Length; i++)
            if (phases[i] - phases[i - 1] > gap) { gap = phases[i] - phases[i - 1]; firstPhase = phases[i]; }
        double origin = (firstPhase - Math.Min(0.05, gap / 2)) * best;

        var refreshes = new long[times.Length];
        long first = (long)Math.Floor((times[0] - origin) / best);
        for (int i = 0; i < times.Length; i++)
            refreshes[i] = (long)Math.Floor((times[i] - origin) / best) - first;
        return (best, refreshes, 1 - gap);
    }

    private static double ParseFps(string title)
    {
        var parts = title.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 1; i < parts.Length; i++)
            if (parts[i] == "fps" && double.TryParse(parts[i - 1], NumberStyles.Float, CultureInfo.InvariantCulture, out double fps))
                return fps;
        return 30;
    }

    private static double Luma(byte* bgra) => (bgra[0] + bgra[1] + bgra[2]) / (3.0 * 255.0);

    /// <summary>
    /// Cells: white, black, 16 Gray-code bits, black, white. One grey cell means an in-between picture of two
    /// consecutive frames; its brightness is the timestep.
    /// </summary>
    private static void DecodeStrip(byte* row, out bool valid, out int index, out double t, out int mids)
    {
        Span<double> v = stackalloc double[Cells];
        for (int i = 0; i < Cells; i++)
        {
            int x = i * Cell + Cell / 2;
            v[i] = (Luma(row + (x - 5) * 4) + Luma(row + x * 4) + Luma(row + (x + 5) * 4)) / 3.0;
        }
        index = -1; t = 0; mids = 0;
        valid = v[0] > 0.8 && v[1] < 0.2 && v[18] < 0.2 && v[19] > 0.8;
        if (!valid) return;

        int gray = 0, midBit = -1;
        double midValue = 0;
        for (int bit = 0; bit < 16; bit++)
        {
            double b = v[2 + bit];
            if (b > 0.80) gray |= 1 << bit;
            else if (b >= 0.20) { mids++; midBit = bit; midValue = b; }
        }
        if (mids == 0) { index = FromGray(gray); return; }
        // More than one cell in between (a generated picture whose cells were warped a little): the pattern is
        // there, but this is no real frame and it has no index. The bar still tells where the content is.
        if (mids > 1) return;

        int n0 = FromGray(gray), n1 = FromGray(gray | (1 << midBit));
        int lower = Math.Min(n0, n1), upper = Math.Max(n0, n1);
        if (upper - lower != 1) return;
        index = lower;
        bool setInUpper = ((upper ^ (upper >> 1)) & (1 << midBit)) != 0;
        t = setInUpper ? midValue : 1 - midValue;
    }

    private static int FromGray(int gray)
    {
        int n = gray;
        for (int shift = 1; shift < 16; shift <<= 1) n ^= n >> shift;
        return n & 0xFFFF;
    }

    /// <summary>
    /// Brightness-weighted centre of the white bar and how spread out it is. Only near-white counts: the bar
    /// moves inside a textured patch that stays below 60 % brightness.
    /// </summary>
    private static void DecodeBar(byte* row, int width, out double centre, out double spread)
    {
        double sum = 0, sumX = 0, sumXX = 0;
        for (int x = 0; x < width; x++)
        {
            double w = Math.Max(0, Luma(row + x * 4) - 0.75);
            sum += w; sumX += w * x; sumXX += w * x * x;
        }
        if (sum < 0.5) { centre = double.NaN; spread = double.NaN; return; }
        centre = sumX / sum + 0.5;
        spread = Math.Sqrt(Math.Max(0, sumXX / sum - (sumX / sum) * (sumX / sum)));
    }

    private static int Analyse(List<Sample> samples, long[] refreshes, double period, double patternFps, int barStep, int span,
        Dictionary<string, string?> a, Func<string, double, double> D)
    {
        double t0 = samples[0].Time, duration = samples[^1].Time - t0;
        long missed = samples.Sum(s => Math.Max(0, (long)s.Accumulated - 1));
        int invalid = samples.Count(s => !s.Valid || double.IsNaN(s.BarX));
        int factor = (int)D("factor", 0);
        double wrapFrames = (double)span / barStep;

        // The moving bar is the clock: it advances `barStep` pixels per source frame, so its position is the
        // content time (in source frames, modulo the wrap) of whatever picture is on screen, real or generated.
        // A new picture is one whose bar has moved. (The Gray-code strip identifies real frames only: a
        // motion-based interpolator does not turn a flipping cell into a clean 50 % grey.)
        // Position = distance the bar has travelled since the first picture, in source frames (the wrap at the
        // right edge is unrolled). A picture is a real frame when the strip decodes cleanly and the bar sits
        // exactly where that frame index puts it; everything else was generated.
        var runs = new List<(double Position, long Start, double Spread, int Index, bool Real, double Time)>();
        var firstSeen = new Dictionary<int, double>();          // real frame index -> time it first reached the screen
        double lastBar = double.NaN, travelled = 0;
        for (int sampleIndex = 0; sampleIndex < samples.Count; sampleIndex++)
        {
            var s = samples[sampleIndex];
            if (!s.Valid || double.IsNaN(s.BarX)) continue;
            if (!double.IsNaN(lastBar) && Math.Abs(s.BarX - lastBar) < 0.5) continue;
            if (!double.IsNaN(lastBar))
            {
                double moved = s.BarX - lastBar;
                if (moved < -0.5 * span) moved += span;         // wrapped around
                travelled += moved;
            }
            lastBar = s.BarX;
            long refresh = refreshes[sampleIndex];
            double left = s.BarX - BarWidth / 2.0;
            bool real = s.MidCells == 0 && s.Index >= 0 && s.BarSpread <= BarWidth * 0.5
                        && Math.Abs(left - (long)s.Index * barStep % span) < 0.6;
            runs.Add((travelled / barStep, refresh, s.BarSpread, s.Index, real, s.Time));
            if (real) firstSeen.TryAdd(s.Index, s.Time);
        }

        // Delay between the pattern presenting a frame and that frame being on screen. Covered by the overlay it
        // is longer than for the bare window, which also proves that the probe is looking at the overlay.
        string? latency = null;
        if (a.GetValueOrDefault("pattern-log") is string patternLog && File.Exists(patternLog))
        {
            var delays = new List<double>();
            using var fs = new FileStream(patternLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            while (reader.ReadLine() is string line)
            {
                var p = line.Split(',');
                if (p.Length >= 3 && p[0] == "frame" && int.TryParse(p[1], out int idx)
                    && double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double presented)
                    && firstSeen.TryGetValue(idx & 0xFFFF, out double seen))
                    delays.Add(seen - presented);
            }
            if (delays.Count > 4)
            {
                delays.Sort();
                latency = $"latency: pattern present -> on screen median {delays[delays.Count / 2] * 1000:F1} ms " +
                          $"({delays[delays.Count / 2] / period:F1} refreshes), p95 {delays[(int)(0.95 * delays.Count)] * 1000:F1} ms, {delays.Count} frames";
            }
        }
        if (runs.Count < 6) { Console.WriteLine($"only {runs.Count} distinct pictures decoded ({invalid} undecodable samples)"); return 1; }

        var holds = new SortedDictionary<long, int>();
        var steps = new SortedDictionary<double, int>();
        var advances = new List<double>();                      // content advance per picture, in pixels
        var latticeErrors = new List<double>();                 // distance from the exact k/N position, in pixels
        int interpolated = 0, backwards = 0, smeared = 0, longHolds = 0, longHoldsGenerated = 0;
        double lastReal = double.NaN;                           // travelled position of the latest real frame
        for (int i = 0; i < runs.Count - 1; i++)
        {
            var r = runs[i];
            bool blurred = r.Spread > BarWidth * 0.75;          // two bars or a streak: a blend across the wrap
            if (r.Real) lastReal = r.Position;
            if (i == 0) continue;                               // the first and last run are cut off by the measurement
            long hold = runs[i + 1].Start - r.Start;
            holds[hold] = holds.GetValueOrDefault(hold) + 1;
            if (hold * period >= 0.25)
            {
                longHolds++;
                if (!r.Real) longHoldsGenerated++;      // the source stood still and an in-between picture was left up
            }
            if (!r.Real) interpolated++;
            if (blurred) { smeared++; lastReal = double.NaN; continue; }

            // Timestep of a generated picture: how far the bar is past the last real frame, in source frames.
            double t = r.Real ? 0 : double.IsNaN(lastReal) ? double.NaN : r.Position - lastReal;
            if (!double.IsNaN(t))
            {
                double tk = Math.Round(Math.Clamp(t, 0, 1.5) * 20) / 20;
                steps[tk] = steps.GetValueOrDefault(tk) + 1;
                if (factor > 0 && !r.Real) latticeErrors.Add(Math.Abs(t * factor - Math.Round(t * factor)) / factor * barStep);
            }

            double advance = r.Position - runs[i - 1].Position;
            if (runs[i - 1].Spread > BarWidth * 0.75) continue;
            if (advance < -0.02) backwards++;
            advances.Add(advance * barStep);
        }
        int wraps = (int)Math.Floor(runs[^1].Position / wrapFrames);
        int counted = runs.Count - 2;
        double rate = counted / ((runs[^1].Start - runs[1].Start) * period);
        double interpShare = (double)interpolated / counted;

        advances.Sort();
        double medianAdvance = advances.Count > 0 ? advances[advances.Count / 2] : double.NaN;
        var deviations = advances.Select(x => Math.Abs(x - medianAdvance)).Order().ToList();
        double advanceP99 = deviations.Count > 0 ? deviations[Math.Min(deviations.Count - 1, (int)(0.99 * deviations.Count))] : double.NaN;
        latticeErrors.Sort();
        double barP99 = latticeErrors.Count > 0 ? latticeErrors[Math.Min(latticeErrors.Count - 1, (int)(0.99 * latticeErrors.Count))] : double.NaN;
        double barMean = latticeErrors.Count > 0 ? latticeErrors.Average() : double.NaN;
        int staticMax = samples.Max(s => s.StaticDiff);

        // Evenness of the motion as the eye gets it: where the bar is against when it is shown. For every
        // picture a straight line is fitted through its neighbours (position against refresh number) and the
        // picture's own distance from that line is taken. It covers both kinds of error at once - a picture
        // with the bar in the wrong place, and a right picture on the wrong refresh.
        const int Neighbours = 12;
        var evenness = new List<double>();
        for (int i = 1; i < runs.Count - 1; i++)
        {
            if (runs[i].Spread > BarWidth * 0.75) continue;
            double sx = 0, sy = 0, sxx = 0, sxy = 0;
            int n = 0;
            for (int j = Math.Max(1, i - Neighbours); j <= Math.Min(runs.Count - 2, i + Neighbours); j++)
            {
                if (j == i || runs[j].Spread > BarWidth * 0.75) continue;
                double x = runs[j].Start - runs[i].Start, y = (runs[j].Position - runs[i].Position) * barStep;
                sx += x; sy += y; sxx += x * x; sxy += x * y; n++;
            }
            double det = n * sxx - sx * sx;
            if (n < 8 || det <= 0) continue;
            double intercept = (sy * sxx - sx * sxy) / det;     // the fitted line at this picture's own refresh
            evenness.Add(Math.Abs(intercept));
        }
        evenness.Sort();
        double evenP99 = evenness.Count > 0 ? evenness[Math.Min(evenness.Count - 1, (int)(0.99 * evenness.Count))] : double.NaN;
        double evenMean = evenness.Count > 0 ? evenness.Average() : double.NaN;

        Console.WriteLine($"probe  : {samples.Count} desktop updates in {duration:F2} s ({samples.Count / duration:F1}/s), {missed} missed, {invalid} undecodable; display {1 / period:F2} Hz, pattern {patternFps:F2} fps");
        Console.WriteLine($"output : {rate:F2} distinct pictures/s; {counted - interpolated} real + {interpolated} in-between ({interpShare:P0}); content went backwards {backwards}x; {wraps} wraps, {smeared} smeared");
        Console.WriteLine("hold (refreshes): " + string.Join("  ", holds.Select(kv => $"{kv.Key}x:{kv.Value}")));
        if (longHolds > 0)
            Console.WriteLine($"standstill: {longHolds} picture(s) stayed a quarter of a second or longer, {longHoldsGenerated} of them generated (in-between) pictures");
        Console.WriteLine("timestep        : " + string.Join("  ", steps.Select(kv => $"{kv.Key:F2}:{kv.Value}")));
        Console.WriteLine($"motion : the bar advances {medianAdvance:F2} px per picture (median; {barStep} px per source frame); p99 deviation {advanceP99:F2} px" +
                          (factor > 0 ? $"; distance from exact k/{factor} positions: mean {barMean:F2} px, p99 {barP99:F2} px" : ""));
        Console.WriteLine($"evenness: bar position against time, distance from the local straight line: mean {evenMean:F2} px, p99 {evenP99:F2} px");
        Console.WriteLine($"static : largest difference in the static patch {staticMax}/255");
        if (latency is not null) Console.WriteLine(latency);

        var failures = new List<string>();
        if (a.ContainsKey("expect-rate"))
        {
            double expected = D("expect-rate", 0);
            if (Math.Abs(rate - expected) > 0.03 * expected) failures.Add($"rate {rate:F2}/s, expected {expected:F2}");
        }
        if (a.ContainsKey("expect-hold"))
        {
            long expected = (long)D("expect-hold", 0);
            double share = (double)holds.GetValueOrDefault(expected) / counted;
            if (share < 0.95) failures.Add($"only {share:P1} of the pictures stayed exactly {expected} refreshes");
        }
        if (a.ContainsKey("expect-interp"))
        {
            double expected = D("expect-interp", 0);
            if (Math.Abs(interpShare - expected) > 0.06) failures.Add($"in-between share {interpShare:P1}, expected {expected:P0}");
        }
        if (a.ContainsKey("max-bar-error"))
        {
            // With --factor: distance from the exact k/N positions. Without (the timesteps differ from picture to
            // picture): evenness of the motion, bar position against time.
            double limit = D("max-bar-error", 0), measured = factor > 0 ? barP99 : evenP99;
            if (!(measured <= limit))
                failures.Add($"{(factor > 0 ? "bar position error" : "motion evenness error")} p99 {measured:F2} px exceeds {limit:F2} px");
        }
        if (a.ContainsKey("static-exact") && staticMax > 1) failures.Add($"static patch changed by {staticMax}/255");
        if (a.ContainsKey("max-static"))
        {
            double limit = D("max-static", 0);
            if (staticMax > limit) failures.Add($"static patch changed by {staticMax}/255 (limit {limit:F0})");
        }
        // (After a standstill one step back is expected: the real window shows the first new frames while the
        //  overlay is still stepping in, and the overlay then continues a playback delay behind them.)
        if (backwards > (int)D("allow-backwards", 0)) failures.Add($"content time went backwards {backwards} times");
        if (a.ContainsKey("standstill-real"))
        {
            if (longHolds == 0) failures.Add("no standstill was seen (the pattern was expected to pause)");
            else if (longHoldsGenerated > 0) failures.Add($"{longHoldsGenerated} standstill(s) showed an in-between picture instead of the last real frame");
        }

        if (failures.Count > 0)
        {
            Console.WriteLine("FAILED:");
            foreach (string f in failures) Console.WriteLine("  - " + f);
            return 1;
        }
        Console.WriteLine("OK");
        return 0;
    }
}
