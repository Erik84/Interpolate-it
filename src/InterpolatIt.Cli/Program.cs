using System.Globalization;
using InterpolatIt.Core.Capture;
using InterpolatIt.Core.Gpu;
using InterpolatIt.Core.Native;
using InterpolatIt.Core.Presentation;

namespace InterpolatIt.Cli;

/// <summary>Headless host of the engine for terminal-driven verification.</summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        User32.SetProcessDpiAwarenessContext(User32.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        Kernel32.OptOutOfPowerThrottling();
        if (args.Length == 0) return Usage();
        try
        {
            var a = new Args(args.Skip(1).ToArray());
            return args[0] switch
            {
                "list-windows" => ListWindows(),
                "find-free-area" => FindFreeArea(a),
                "trace" => TraceCommand.Run(a),
                "run" => RunCommand.Run(a),
                _ => Usage(),
            };
        }
        catch (ArgumentException e)
        {
            Console.Error.WriteLine("error: " + e.Message);
            return 2;
        }
    }

    private static int Usage()
    {
        Console.WriteLine(
            """
            InterpolatIt.Cli <command> [options]

              list-windows                         windows that can be captured
              find-free-area --size WxH [--why [--at X,Y]]
                                                   a spot on the screen no window is using (prints "X,Y";
                                                   exit code 1 when there is none) - for the test tools.
                                                   --why: which programs' windows are in the way (no titles)
              trace  --title TEXT | --hwnd 0xHEX   record what the capture delivers
                     [--seconds 10] [--csv FILE]
              run    --title TEXT | --hwnd 0xHEX   cover the window with the overlay
                     [--passthrough]               delay line without AI (capture -> overlay)
                     [--factor 2|3|4] [--model ID] [--backend nv|dml|cpu]
                     [--seconds S] [--log FILE] [--quiet]
                     [--hud]                       show the detected/target/actual fps readout
                     [--enhance]                   clean up compressed video (the graphics driver's enhancement)
                     [--toggle-every S]            switch interpolation off/on every S seconds
                     [--hud-every S]               show/hide the readout every S seconds
                     [--enhance-every S]           switch the clean-up on/off every S seconds
                     [--record FILE [--record-after S] [--record-for S] [--no-sound]]   MP4 of what the window
                                                   shows, with the sound of its application
                     [--trace] [--dump-frames DIR]                         diagnostics
                     [--phase 0..1] [--sync 0|1] [--no-calibrate]   presentation timing experiments
            """);
        return 2;
    }

    private static int ListWindows()
    {
        foreach (var w in WindowEnumerator.GetCapturableWindows())
            Console.WriteLine($"0x{w.Handle:X8}  pid {w.ProcessId,6}  {w.Bounds.Width,5}x{w.Bounds.Height,-5} {w.ProcessName,-18} {w.Title}");
        return 0;
    }

    /// <summary>
    /// Finds a place for a test window that does not sit on top of anything the user has open: this desktop is
    /// in use while tests run, a window appearing over someone's work is a nuisance, and a window that gets
    /// covered cannot be measured. Only window rectangles are looked at.
    /// </summary>
    private static unsafe int FindFreeArea(Args a)
    {
        string[] size = (a.Get("size") ?? "1296x760").Split('x');
        int width = int.Parse(size[0], CultureInfo.InvariantCulture) + 16, height = int.Parse(size[1], CultureInfo.InvariantCulture) + 16;

        var handles = new List<nint>();
        var gch = System.Runtime.InteropServices.GCHandle.Alloc(handles);
        try { User32.EnumWindows(&CollectWindow, System.Runtime.InteropServices.GCHandle.ToIntPtr(gch)); }
        finally { gch.Free(); }

        var occupied = new List<RECT>();
        var kinds = new List<(RECT Rect, string Kind)>();
        foreach (nint hwnd in handles)
        {
            if (!User32.IsWindowVisible(hwnd) || User32.IsIconic(hwnd) || Dwm.IsCloaked(hwnd)) continue;
            uint ex = User32.GetExStyle(hwnd);
            if ((ex & User32.WS_EX_TRANSPARENT) != 0 && (ex & User32.WS_EX_LAYERED) != 0) continue;   // click-through overlays
            string cls = User32.GetClassName(hwnd);
            if (cls is "Progman" or "WorkerW") continue;                                               // the desktop itself
            if (!Dwm.TryGetExtendedFrameBounds(hwnd, out RECT r) && !User32.GetWindowRect(hwnd, out r)) continue;
            if (r.Width < 8 || r.Height < 8) continue;
            occupied.Add(r);
            if (a.Has("why")) kinds.Add((r, KindOf(hwnd)));
        }
        if (a.Has("why"))
        {
            // What is in the way, largest first: which program and what kind of window - never its title.
            foreach (var (r, kind) in kinds.OrderByDescending(k => (long)k.Rect.Width * k.Rect.Height).Take(12))
                Console.Error.WriteLine($"  {r.Width}x{r.Height} at {r.Left},{r.Top}  {kind}");
            if (a.Get("at") is string at)
            {
                string[] xy = at.Split(',');
                var point = new POINT { X = int.Parse(xy[0], CultureInfo.InvariantCulture), Y = int.Parse(xy[1], CultureInfo.InvariantCulture) };
                nint top = User32.GetAncestor(User32.WindowFromPoint(point), User32.GA_ROOT);
                Console.Error.WriteLine($"  at {at}, on top: {(top == 0 ? "nothing" : KindOf(top))}");
            }
        }

        nint monitor = User32.MonitorFromWindow(User32.GetShellWindow(), User32.MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFOEXW { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEXW>() };
        if (monitor == 0 || !User32.GetMonitorInfoW(monitor, ref info)) return 1;
        RECT work = info.Work;

        // Right to left, top to bottom: the far corner of a wide screen is the likeliest to be empty.
        for (int y = work.Top; y + height <= work.Bottom; y += 40)
        {
            for (int x = work.Right - width; x >= work.Left; x -= 40)
            {
                bool free = true;
                foreach (RECT r in occupied)
                {
                    if (x < r.Right && x + width > r.Left && y < r.Bottom && y + height > r.Top) { free = false; break; }
                }
                if (!free) continue;
                Console.WriteLine($"{x + 8},{y + 8}");
                return 0;
            }
        }
        return 1;
    }

    /// <summary>The program a window belongs to, its window class and how it is layered (for --why).</summary>
    private static string KindOf(nint hwnd)
    {
        User32.GetWindowThreadProcessId(hwnd, out uint processId);
        string program;
        try { program = System.Diagnostics.Process.GetProcessById((int)processId).ProcessName; }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { program = "?"; }
        uint ex = User32.GetExStyle(hwnd);
        string flags = ((ex & User32.WS_EX_TOPMOST) != 0 ? " always-on-top" : "") + ((ex & User32.WS_EX_LAYERED) != 0 ? " layered" : "")
                       + ((ex & User32.WS_EX_TRANSPARENT) != 0 ? " click-through" : "") + ((ex & User32.WS_EX_TOOLWINDOW) != 0 ? " tool" : "");
        return $"{program}, class {User32.GetClassName(hwnd)}{flags}";
    }

    [System.Runtime.InteropServices.UnmanagedCallersOnly]
    private static int CollectWindow(nint hwnd, nint lParam)
    {
        ((List<nint>)System.Runtime.InteropServices.GCHandle.FromIntPtr(lParam).Target!).Add(hwnd);
        return 1;
    }

    /// <summary>Resolves --hwnd / --title to a window, with a helpful error.</summary>
    public static WindowInfo ResolveWindow(Args a)
    {
        if (a.Get("hwnd") is string h)
        {
            nint hwnd = (nint)Convert.ToInt64(h.Replace("0x", "", StringComparison.OrdinalIgnoreCase), 16);
            return WindowEnumerator.Describe(hwnd) ?? throw new ArgumentException($"window {h} does not exist");
        }
        string title = a.Get("title") ?? throw new ArgumentException("--title or --hwnd is required");
        return WindowEnumerator.FindByTitle(title)
               ?? throw new ArgumentException($"no capturable window with '{title}' in its title (see list-windows)");
    }
}

/// <summary>Minimal "--name value" / "--flag" parser.</summary>
internal sealed class Args
{
    private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);

    public Args(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--")) throw new ArgumentException($"unexpected argument '{args[i]}'");
            string name = args[i][2..];
            bool hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--");
            _values[name] = hasValue ? args[++i] : null;
        }
    }

    public bool Has(string name) => _values.ContainsKey(name);
    public string? Get(string name) => _values.TryGetValue(name, out string? v) ? v : null;
    public double GetDouble(string name, double fallback) => Get(name) is string v ? double.Parse(v, CultureInfo.InvariantCulture) : fallback;
    public int GetInt(string name, int fallback) => Get(name) is string v ? int.Parse(v, CultureInfo.InvariantCulture) : fallback;
}

/// <summary>
/// Records what Windows.Graphics.Capture delivers for a window: arrival cadence in display refreshes, duplicates,
/// how much of the picture changed, and the system's dirty regions. This is the evidence the frame gate and the
/// cadence estimator are tuned against.
/// </summary>
internal static class TraceCommand
{
    public static int Run(Args a)
    {
        var window = Program.ResolveWindow(a);
        double seconds = a.GetDouble("seconds", 10);
        string? csvPath = a.Get("csv");

        double refresh = DisplayClock.MeasureRefreshPeriod();
        using var gpu = GpuDevice.Create();
        using var capture = new WgcCapture(gpu, window.Handle);
        using var analyzer = new FrameAnalyzer(gpu);
        using var store = new FrameStore(gpu, realCapacity: 2, generatedCapacity: 1);
        Console.WriteLine($"target : {window}");
        Console.WriteLine($"display: {1 / refresh:F3} Hz   capture: border {(capture.BorderRemoved ? "removed" : "kept")}, " +
                          $"update interval {(capture.UpdateIntervalLifted ? "1 ms" : "default")}, dirty regions {(capture.DirtyRegionsAvailable ? "on" : "n/a")}");

        using var csv = csvPath is null ? null : new StreamWriter(csvPath);
        csv?.WriteLine("index,time_s,interval_ms,interval_refreshes,width,height,dirty_count,dirty_fraction,changed_fraction,max_diff,ssim,duplicate,qpc_s,age_ms");
        var ages = new List<double>();

        using var tensor = new InterpolatIt.Core.Util.NativeBuffer(1);
        InterpolatIt.Core.Util.NativeBuffer? buffer = null;
        var histogram = new SortedDictionary<int, int>();
        var intervals = new List<double>();
        int frames = 0, duplicates = 0, sizeChanges = 0;
        double changedSum = 0, dirtySum = 0;
        int dirtySamples = 0;
        double start = Qpc.Now, last = double.NaN, firstFrame = double.NaN, lastFrame = double.NaN;
        int lastW = 0, lastH = 0;

        while (Qpc.Now - start < seconds && !capture.IsClosed)
        {
            capture.FrameSignal.WaitOne(100);
            while (capture.TryAcquire(out var frame))
            {
                using (frame)
                {
                    double age = Qpc.Now - frame!.Time;      // how old the frame is when it reaches us
                    ages.Add(age);
                    int w = frame.ContentWidth, h = frame.ContentHeight;
                    if (w != lastW || h != lastH)
                    {
                        if (lastW == 0)
                        {
                            // How the captured picture relates to the window's rectangles decides where the overlay goes.
                            Dwm.TryGetExtendedFrameBounds(window.Handle, out RECT frameBounds);
                            User32.GetWindowRect(window.Handle, out RECT windowRect);
                            User32.GetClientRect(window.Handle, out RECT clientRect);
                            var origin = new POINT();
                            User32.ClientToScreen(window.Handle, ref origin);
                            var desc = frame.Texture.Description;
                            Console.WriteLine($"picture: content {w}x{h} in a {desc.Width}x{desc.Height} texture; frame bounds {frameBounds}; " +
                                              $"window rect {windowRect}; client {clientRect.Width}x{clientRect.Height} at ({origin.X},{origin.Y})");
                        }
                        if (lastW != 0) sizeChanges++;
                        lastW = w; lastH = h;
                        store.Configure(w, h);
                        int iw = 640, ih = Math.Max(16, (int)Math.Round(640.0 * h / w / 16) * 16);
                        analyzer.Configure(iw, ih);
                        buffer?.Dispose();
                        buffer = new InterpolatIt.Core.Util.NativeBuffer(analyzer.TensorBytes);
                    }
                    var stored = store.StoreReal(frame, frames);
                    var stats = analyzer.Analyze(stored.View, w, h, w, h, buffer!.Pointer);

                    bool duplicate = stats.HasPrevious && stats.ChangedTiles == 0;
                    double interval = double.IsNaN(last) ? double.NaN : frame.Time - last;
                    int inRefreshes = double.IsNaN(interval) ? 0 : (int)Math.Round(interval / refresh);
                    if (!double.IsNaN(interval))
                    {
                        intervals.Add(interval);
                        histogram[inRefreshes] = histogram.GetValueOrDefault(inRefreshes) + 1;
                    }
                    double dirtyFraction = frame.DirtyRegionCount < 0 ? double.NaN : (double)frame.DirtyArea / ((long)w * h);
                    if (!double.IsNaN(dirtyFraction)) { dirtySum += dirtyFraction; dirtySamples++; }
                    if (duplicate) duplicates++;
                    if (stats.HasPrevious) changedSum += stats.ChangedFraction;
                    csv?.WriteLine(string.Create(CultureInfo.InvariantCulture,
                        $"{frames},{frame.Time - start:F6},{interval * 1000:F3},{inRefreshes},{w},{h},{frame.DirtyRegionCount},{dirtyFraction:F4},{stats.ChangedFraction:F4},{stats.MaxTileDiff:F4},{stats.Ssim:F4},{(duplicate ? 1 : 0)},{frame.Time:F6},{age * 1000:F3}"));
                    if (double.IsNaN(firstFrame)) firstFrame = frame.Time;
                    lastFrame = last = frame.Time;
                    frames++;
                }
            }
        }
        buffer?.Dispose();

        if (frames < 2)
        {
            Console.WriteLine($"only {frames} frame(s) arrived in {seconds:F0} s: the window is not updating (or its content is protected).");
            return 1;
        }
        double span = lastFrame - firstFrame;
        int unique = frames - duplicates;
        intervals.Sort();
        Console.WriteLine($"frames : {frames} in {span:F2} s = {(frames - 1) / span:F2} fps arriving, {unique} unique = {(unique - 1) / span:F2} fps, {duplicates} duplicates ({100.0 * duplicates / frames:F1} %)");
        Console.WriteLine($"interval: median {intervals[intervals.Count / 2] * 1000:F2} ms, min {intervals[0] * 1000:F2}, max {intervals[^1] * 1000:F2}");
        ages.Sort();
        Console.WriteLine($"age when received (now - frame timestamp): median {ages[ages.Count / 2] * 1000:F2} ms = {ages[ages.Count / 2] / refresh:F2} refreshes, " +
                          $"min {ages[0] * 1000:F2}, p95 {ages[(int)(0.95 * ages.Count)] * 1000:F2}");
        Console.WriteLine("interval in refreshes: " + string.Join("  ", histogram.Select(kv => $"{kv.Key}x:{kv.Value}")));
        Console.WriteLine($"changed tiles per frame: {100 * changedSum / Math.Max(1, frames - 1):F1} %   " +
                          (dirtySamples > 0 ? $"dirty region area per frame: {100 * dirtySum / dirtySamples:F1} %" : "dirty regions: n/a") +
                          (sizeChanges > 0 ? $"   size changes: {sizeChanges}" : ""));
        return 0;
    }
}
