using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using InterpolatIt.Core.Gpu;
using InterpolatIt.Core.Native;
using InterpolatIt.Core.Presentation;
using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace InterpolatIt.TestPattern;

/// <summary>
/// A window with exactly known content, to be captured and interpolated:
///   * a white bar moving a fixed number of pixels per frame in a black band (position is linear in the frame
///     index, so the probe can check that generated frames land in between);
///   * a strip of 20 cells in the top-left corner: white, black, 16 bits of the Gray-coded frame index, black,
///     white. Consecutive indices differ in one bit, so an in-between frame shows exactly one grey cell whose
///     brightness tells the timestep;
///   * a static fine grid (must stay pixel-identical) and a moving textured box (realistic motion for the model).
/// New frames are presented on compositor ticks, so the cadence is exact in display refreshes.
/// </summary>
internal static unsafe class Program
{
    public const int CellSize = 24, CellCount = 20, StripX = 16, StripY = 16, BarWidth = 24, BandHeight = 60;

    [StructLayout(LayoutKind.Sequential)]
    private struct Params
    {
        public Vector2 Size;
        public float BarX, BoxX;
        public uint Frame, Gray, Flags, Scene;
    }

    private const string PatternShader =
        """
        cbuffer P : register(b0) { float2 Size; float BarX; float BoxX; uint Frame; uint Gray; uint Flags; uint Scene; };

        float4 main(float4 pos : SV_Position) : SV_Target
        {
            float2 p = pos.xy;
            float3 c = lerp(float3(0.10, 0.12, 0.20), float3(0.25, 0.20, 0.15), p.y / Size.y);
            c += 0.06 * sin(p.x * 0.01) * float3(1.0, 0.5, 0.2);
            if (Scene != 0)
            {
                // A cut replaces the whole picture by a different one (other structure, not just other colours).
                float2 g = floor(p / (40.0 + 24.0 * (Scene % 3)));
                float n = frac(sin(dot(g + Scene * 17.0, float2(127.1, 311.7))) * 43758.5453);
                c = float3(n, frac(n * 5.3 + Scene * 0.37), frac(n * 2.7 + Scene * 0.11));
            }

            // static fine grid, bottom-left: text-like detail that must not shimmer
            if (p.x > 40 && p.x < 360 && p.y > Size.y - 200 && p.y < Size.y - 40)
            {
                uint gx = (uint)p.x % 8, gy = (uint)p.y % 8;
                if (gx == 0 || gy == 0) c = float3(0.9, 0.9, 0.9);
            }

            // moving textured box
            float2 q = p - float2(BoxX, Size.y * 0.55);
            if (q.x >= 0 && q.x < 160 && q.y >= 0 && q.y < 120)
            {
                float2 cell = floor(q / 8);
                float n = frac(sin(dot(cell, float2(12.9898, 78.233))) * 43758.5453);
                c = float3(n, frac(n * 7.3), frac(n * 3.1));
            }

            // black band with the moving white bar. The bar travels inside a textured patch that moves with it:
            // an interpolator estimates motion from texture, and a bare white rectangle on black gives it none
            // (RIFE 4.25 then simply shows the nearest frame for timesteps below 0.2 or above 0.8, which it does
            // not do on real, textured video). The texture stays below 60 % brightness, so the probe can still
            // find the bar by thresholding.
            float bandTop = floor(Size.y * 0.30);
            if (p.y >= bandTop && p.y < bandTop + 60)
            {
                c = float3(0.02, 0.02, 0.02);
                float rel = p.x - BarX;
                if (rel >= -108 && rel < 132)
                {
                    float2 cell = floor(float2(rel + 108, p.y - bandTop) / 6);
                    float n = frac(sin(dot(cell, float2(41.3, 289.1))) * 43758.5453);
                    c = float3(0.10 + 0.45 * n, 0.10 + 0.45 * frac(n * 3.7), 0.10 + 0.45 * frac(n * 7.9));
                }
                if (rel >= 0 && rel < 24) c = float3(1, 1, 1);
            }

            // optional "blip": a small square that toggles between two content frames, as a blinking caret, a
            // spinner or a streaming client's status icon does. It is not content and must not set the cadence.
            if ((Flags & 2) && p.x > Size.x - 60 && p.x < Size.x - 40 && p.y > Size.y - 60 && p.y < Size.y - 40)
                c = float3(1, 1, 1);

            // optional static "HUD" block, as games draw over moving content
            if ((Flags & 1) && p.x > Size.x - 220 && p.x < Size.x - 20 && p.y > 20 && p.y < 80)
                c = lerp(c, ((uint)p.x / 4 + (uint)p.y / 4) % 2 ? float3(1, 0.8, 0.1) : float3(0.1, 0.1, 0.1), 0.85);

            // Gray-coded frame counter
            float2 s = p - float2(16, 16);
            if (s.x >= 0 && s.x < 24 * 20 && s.y >= 0 && s.y < 24)
            {
                uint i = (uint)(s.x / 24);
                bool on;
                if (i == 0) on = true;
                else if (i == 1) on = false;
                else if (i < 18) on = ((Gray >> (i - 2)) & 1) != 0;
                else if (i == 18) on = false;
                else on = true;
                c = on ? float3(1, 1, 1) : float3(0, 0, 0);
            }
            return float4(c, 1);
        }
        """;

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
        if (a.ContainsKey("help"))
        {
            Console.WriteLine(
                """
                InterpolatIt.TestPattern [--fps F | --every N] [--size WxH] [--pos X,Y] [--seconds S] [--log FILE]
                    [--dup]              re-present the unchanged picture on every refresh (duplicate frames)
                    [--variable A-B]     random frame times between A and B milliseconds
                    [--cut-every N]      scene cut every N frames
                    [--hud]              static block over the moving content
                    [--blip]             (with --every N) an extra frame halfway between two content frames in which
                                         only a small square in the corner toggles
                    [--jitter P]         (with --every N) each frame comes one refresh late with probability P, the
                                         way frames of a network stream do; the content itself stays on schedule
                    [--pause-at S --pause-for S]   stop producing frames for a while
                    [--hang-at S --hang-for S]     stop pumping window messages for a while (hung application)
                """);
            return 0;
        }
        double D(string k, double fallback) => a.TryGetValue(k, out string? v) && v is not null ? double.Parse(v, CultureInfo.InvariantCulture) : fallback;

        User32.SetProcessDpiAwarenessContext(User32.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        Kernel32.OptOutOfPowerThrottling();

        int width = 1280, height = 720, posX = 120, posY = 120;
        if (a.GetValueOrDefault("size") is string size) { var p = size.Split('x'); width = int.Parse(p[0]); height = int.Parse(p[1]); }
        if (a.GetValueOrDefault("pos") is string pos) { var p = pos.Split(','); posX = int.Parse(p[0]); posY = int.Parse(p[1]); }
        int every = (int)D("every", 0);
        double fps = D("fps", 30);
        bool dup = a.ContainsKey("dup"), hud = a.ContainsKey("hud"), blip = a.ContainsKey("blip");
        double jitter = D("jitter", 0);
        int cutEvery = (int)D("cut-every", 0);
        double seconds = D("seconds", 0), pauseAt = D("pause-at", -1), pauseFor = D("pause-for", 0);
        double hangAt = D("hang-at", -1), hangFor = D("hang-for", 0);
        double varMin = 0, varMax = 0;
        if (a.GetValueOrDefault("variable") is string range) { var p = range.Split('-'); varMin = double.Parse(p[0]) / 1000; varMax = double.Parse(p[1]) / 1000; }
        using var log = a.GetValueOrDefault("log") is string logPath ? new StreamWriter(logPath) { AutoFlush = true } : null;
        log?.WriteLine("event,index,time_s,gray");

        double refresh = DisplayClock.MeasureRefreshPeriod();
        if (every > 0) fps = 1.0 / (every * refresh);
        string label = varMax > 0 ? $"variable {varMin * 1000:F0}-{varMax * 1000:F0} ms" : $"{fps:F2} fps";
        string title = $"InterpolatIt TestPattern {label}";

        using var gpu = GpuDevice.Create();
        bool resized = false, quit = false;
        int clientW = width, clientH = height;
        var rect = new RECT { Left = 0, Top = 0, Right = width, Bottom = height };
        User32.AdjustWindowRectEx(ref rect, User32.WS_OVERLAPPEDWINDOW, false, 0);
        using var window = new Win32Window("InterpolatItTestPattern", title, User32.WS_OVERLAPPEDWINDOW, 0,
            posX, posY, rect.Width, rect.Height, 0, (hwnd, msg, wp, lp) =>
            {
                switch (msg)
                {
                    case User32.WM_DESTROY:
                        quit = true;
                        User32.PostQuitMessage(0);
                        return 0;
                    case User32.WM_SIZE:
                        clientW = (int)(lp & 0xFFFF);
                        clientH = (int)((lp >> 16) & 0xFFFF);
                        resized = true;
                        return 0;
                    case User32.WM_LBUTTONDOWN:
                        string line = $"click,{(short)(lp & 0xFFFF)},{(short)((lp >> 16) & 0xFFFF)},{Qpc.Now:F6}";
                        Console.WriteLine(line);
                        log?.WriteLine(line);
                        return 0;
                    case User32.WM_ERASEBKGND:
                        return 1;
                }
                return null;
            });

        // Show without taking the focus from whatever the user is doing. Twice, because the very first ShowWindow
        // of a process obeys the launcher's STARTUPINFO (which may say "hidden") instead of its argument.
        User32.ShowWindow(window.Handle, User32.SW_SHOWNOACTIVATE);
        User32.ShowWindow(window.Handle, User32.SW_SHOWNOACTIVATE);
        User32.GetClientRect(window.Handle, out RECT client);
        clientW = client.Width;
        clientH = client.Height;

        using var chain = new WindowSwapChain(gpu, window.Handle, clientW, clientH);
        using var vs = gpu.Device.CreateVertexShader(ShaderLibrary.Compile("Fullscreen.hlsl", "main", "vs_5_0"));
        using var ps = gpu.Device.CreatePixelShader(ShaderLibrary.CompileSource(PatternShader, "TestPattern", "main", "ps_5_0"));
        using var constants = gpu.Device.CreateBuffer((uint)sizeof(Params), BindFlags.ConstantBuffer);

        Console.WriteLine($"{title}: client {clientW}x{clientH} at ({posX},{posY}), display {1 / refresh:F3} Hz" +
                          (every > 0 ? $", new frame every {every} refreshes" : ""));

        var random = new Random(4242);
        double start = Qpc.Now, accumulator = 0, nextVariable = start;
        long index = -1, drawn = -1;
        uint scene = 0;
        int barStep = Math.Max(2, (int)Math.Round(480.0 / fps));        // about 480 px/s
        bool hung = false, latePending = false, blipOn = false;

        while (!quit)
        {
            if (!Win32Window.PumpPending()) break;
            if (!DisplayClock.WaitForTick()) continue;
            double now = Qpc.Now, elapsed = now - start;
            if (seconds > 0 && elapsed >= seconds) break;

            if (hangAt >= 0 && !hung && elapsed >= hangAt)
            {
                hung = true;
                Console.WriteLine($"hang,{hangFor:F1}");
                Thread.Sleep(TimeSpan.FromSeconds(hangFor));        // no message pump: this window is now "not responding"
                continue;
            }

            bool paused = pauseAt >= 0 && elapsed >= pauseAt && elapsed < pauseAt + pauseFor;
            bool blipNow = false;
            if (!paused)
            {
                if (varMax > 0)
                {
                    if (now >= nextVariable)
                    {
                        index++;
                        nextVariable = Math.Max(now, nextVariable) + varMin + random.NextDouble() * (varMax - varMin);
                    }
                }
                else if (every > 0)
                {
                    accumulator += 1;
                    if (latePending)
                    {
                        index++;                            // the frame that should have come on the previous refresh
                        latePending = false;
                    }
                    else if (index < 0 || accumulator >= every)
                    {
                        accumulator = 0;                    // the schedule itself does not slip
                        if (index >= 0 && jitter > 0 && every > 1 && random.NextDouble() < jitter) latePending = true;
                        else index++;
                    }
                    else if (blip && every > 1 && accumulator == every / 2)
                    {
                        blipOn = !blipOn;
                        blipNow = true;
                    }
                }
                else
                {
                    accumulator += fps * refresh;
                    if (index < 0) { index = 0; accumulator = 0; }
                    else if (accumulator >= 1) { index++; accumulator -= 1; }
                }
            }

            bool isNew = index != drawn;
            if (!isNew && !dup && !resized && !blipNow) continue;
            if (resized)
            {
                chain.Resize(clientW, clientH);
                resized = false;
            }
            if (isNew && cutEvery > 0 && index > 0 && index % cutEvery == 0) scene++;

            int span = Math.Max(1, clientW - BarWidth);
            int boxSpan = Math.Max(1, clientW - 160 - 80);
            long boxPhase = (index * 6) % (2 * boxSpan);
            var p = new Params
            {
                Size = new Vector2(clientW, clientH),
                BarX = (index * barStep) % span,
                BoxX = 40 + (boxPhase < boxSpan ? boxPhase : 2 * boxSpan - boxPhase),
                Frame = (uint)index,
                Gray = (uint)(index ^ (index >> 1)) & 0xFFFF,
                Flags = (hud ? 1u : 0u) | (blipOn ? 2u : 0u),
                Scene = scene,
            };
            lock (gpu.Sync)
            {
                var ctx = gpu.Context;
                ctx.UpdateSubresource(in p, constants);
                ctx.OMSetRenderTargets(chain.BackBufferTarget);
                ctx.RSSetViewport(0, 0, clientW, clientH);
                ctx.IASetInputLayout(null);
                ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                ctx.VSSetShader(vs);
                ctx.PSSetShader(ps);
                ctx.PSSetConstantBuffer(0, constants);
                ctx.Draw(3, 0);
                // Sync interval 1: the frame takes effect at the next vblank. With 0 it depends on when the
                // compositor happens to latch, and one frame in six landed a refresh early or late.
                chain.Present(1);
            }
            if (isNew)
            {
                drawn = index;
                log?.WriteLine(string.Create(CultureInfo.InvariantCulture, $"frame,{index},{now:F6},{p.Gray}"));
            }
        }
        Console.WriteLine($"done: {drawn + 1} frames in {Qpc.Now - start:F2} s");
        return 0;
    }
}
