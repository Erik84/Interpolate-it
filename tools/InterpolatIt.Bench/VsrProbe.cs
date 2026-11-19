using System.Diagnostics;
using InterpolatIt.Core.Gpu;
using InterpolatIt.Core.Util;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace InterpolatIt.Bench;

/// <summary>
/// Does the graphics driver's own video enhancement (NVIDIA RTX Video Super Resolution) engage for the kind of
/// frames this application has - BGRA textures from a window capture - when it is asked for through the
/// Direct3D 11 video processor, and what does it cost?
/// <para>
/// The request is a private "stream extension" that Chromium, mpv, VLC and MPC Video Renderer all use. The driver
/// answers S_OK whether or not it does anything (it only acts when the user has switched the feature on in the
/// vendor's control panel), so the only proof is in the pixels: the same blit is done with the extension off and
/// on, and the two results are compared. Both a BGRA input and an NV12 input (what video players feed it) are
/// tried, so that "not switched on" can be told from "does not take BGRA".
/// </para>
/// </summary>
internal static unsafe class VsrProbe
{
    // Chromium, ui/gl/swap_chain_presenter.cc (ToggleNvidiaVpSuperResolution).
    private static readonly Guid NvidiaPpeInterface = new("d43ce1b3-1f4b-48ac-baee-c3c25375e6f7");
    private const uint NvidiaVersion1 = 1, NvidiaMethodSuperResolution = 2;

    public static int Run(GpuDevice gpu, string inputPng, int scale, string outDir)
    {
        byte[] source = Png.ReadBgra(inputPng, out int w, out int h);
        int outW = w * scale, outH = h * scale;
        Directory.CreateDirectory(outDir);
        Console.WriteLine($"\n== driver video enhancement: {w}x{h} -> {outW}x{outH} on {gpu.Adapter.Description}");

        var device = gpu.Device;
        using var videoDevice = device.QueryInterfaceOrNull<ID3D11VideoDevice>();
        using var videoContext = gpu.Context.QueryInterfaceOrNull<ID3D11VideoContext>();
        using var videoContext1 = gpu.Context.QueryInterfaceOrNull<ID3D11VideoContext1>();
        if (videoDevice is null || videoContext is null || videoContext1 is null)
        {
            Console.WriteLine("   this device has no Direct3D 11 video support");
            return 1;
        }

        var bgraDesc = new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)w, (uint)h, 1, 1, BindFlags.RenderTarget | BindFlags.ShaderResource);
        ID3D11Texture2D created;
        fixed (byte* p = source)
            created = device.CreateTexture2D(in bgraDesc, new SubresourceData((nint)p, (uint)(w * 4), 0));
        using var bgra = created;

        // The same picture as NV12, made by the video processor itself (no enhancement, same size).
        using var nv12 = device.CreateTexture2D(new Texture2DDescription(Format.NV12, (uint)w, (uint)h, 1, 1,
            BindFlags.RenderTarget | BindFlags.ShaderResource));
        Convert(gpu, videoDevice, videoContext, videoContext1, bgra, ColorSpaceType.RgbFullG22NoneP709, w, h,
            nv12, ColorSpaceType.YcbcrStudioG22LeftP709, w, h, enhance: false, out _, out _);

        bool any = false;
        foreach (var (label, texture, space) in new[]
                 {
                     ("BGRA input (what a window capture delivers)", bgra, ColorSpaceType.RgbFullG22NoneP709),
                     ("NV12 input (what video players use)", nv12, ColorSpaceType.YcbcrStudioG22LeftP709),
                 })
        {
            using var plainTarget = NewTarget(device, outW, outH);
            using var enhancedTarget = NewTarget(device, outW, outH);
            Convert(gpu, videoDevice, videoContext, videoContext1, texture, space, w, h,
                plainTarget, ColorSpaceType.RgbFullG22NoneP709, outW, outH, enhance: false, out double plainFast, out double plainPaced);
            Convert(gpu, videoDevice, videoContext, videoContext1, texture, space, w, h,
                enhancedTarget, ColorSpaceType.RgbFullG22NoneP709, outW, outH, enhance: true, out double enhancedFast, out double enhancedPaced);
            byte[] plain = gpu.ReadBack(plainTarget, outW, outH), enhanced = gpu.ReadBack(enhancedTarget, outW, outH);

            long changed = 0;
            double sum = 0;
            for (int i = 0; i < plain.Length; i += 4)
            {
                int d = Math.Max(Math.Abs(plain[i] - enhanced[i]), Math.Max(Math.Abs(plain[i + 1] - enhanced[i + 1]), Math.Abs(plain[i + 2] - enhanced[i + 2])));
                if (d > 2) changed++;
                sum += d;
            }
            double share = changed / (plain.Length / 4.0);
            bool engaged = share > 0.02;
            any |= engaged;
            string tag = label.StartsWith("BGRA") ? "bgra" : "nv12";
            Console.WriteLine($"   {label}");
            Console.WriteLine($"      plain scaling          {plainFast,6:F2} ms back to back, {plainPaced,6:F2} ms at 60 per second");
            Console.WriteLine($"      enhancement requested  {enhancedFast,6:F2} ms back to back, {enhancedPaced,6:F2} ms at 60 per second");
            Console.WriteLine($"      pixels that differ by more than 2/255: {share:P1} (mean difference {sum / (plain.Length / 4.0):F2}/255)  => {(engaged ? "ENGAGED" : "not engaged")}");
            SaveCrop(plain, outW, outH, Path.Combine(outDir, $"vsr-{tag}-plain-crop.png"));
            SaveCrop(enhanced, outW, outH, Path.Combine(outDir, $"vsr-{tag}-enhanced-crop.png"));
        }

        Console.WriteLine(any
            ? "   => the driver's enhancement works on this PC for the inputs marked ENGAGED"
            : "   => the driver enhanced nothing: the pictures are the same with and without the request.\n" +
              "      It only acts when it is switched on: NVIDIA app > System > Video > RTX Video Enhancement > Super Resolution.");
        Console.WriteLine($"   close-ups: {outDir}\\vsr-*-crop.png");
        return 0;
    }

    private static ID3D11Texture2D NewTarget(ID3D11Device device, int width, int height)
        => device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)width, (uint)height, 1, 1,
            BindFlags.RenderTarget | BindFlags.ShaderResource));

    /// <summary>One video-processor conversion, timed back to back and paced at 60 per second.</summary>
    private static void Convert(GpuDevice gpu, ID3D11VideoDevice videoDevice, ID3D11VideoContext videoContext, ID3D11VideoContext1 videoContext1,
        ID3D11Texture2D input, ColorSpaceType inputSpace, int w, int h,
        ID3D11Texture2D output, ColorSpaceType outputSpace, int outW, int outH, bool enhance, out double backToBackMs, out double pacedMs)
    {
        var content = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputFrameRate = new Rational(60, 1),
            InputWidth = (uint)w,
            InputHeight = (uint)h,
            OutputFrameRate = new Rational(60, 1),
            OutputWidth = (uint)outW,
            OutputHeight = (uint)outH,
            Usage = VideoUsage.PlaybackNormal,
        };
        using var enumerator = videoDevice.CreateVideoProcessorEnumerator(content);
        using var processor = videoDevice.CreateVideoProcessor(enumerator, 0);
        using var inputView = videoDevice.CreateVideoProcessorInputView(input, enumerator,
            new VideoProcessorInputViewDescription { FourCC = 0, ViewDimension = VideoProcessorInputViewDimension.Texture2D });
        using var outputView = videoDevice.CreateVideoProcessorOutputView(output, enumerator,
            new VideoProcessorOutputViewDescription { ViewDimension = VideoProcessorOutputViewDimension.Texture2D });

        videoContext1.VideoProcessorSetStreamColorSpace1(processor, 0, inputSpace);
        videoContext1.VideoProcessorSetOutputColorSpace1(processor, outputSpace);
        videoContext.VideoProcessorSetStreamFrameFormat(processor, 0, VideoFrameFormat.Progressive);
        videoContext.VideoProcessorSetStreamSourceRect(processor, 0, true, new Vortice.RawRect(0, 0, w, h));
        videoContext.VideoProcessorSetStreamDestRect(processor, 0, true, new Vortice.RawRect(0, 0, outW, outH));
        videoContext.VideoProcessorSetOutputTargetRect(processor, true, new Vortice.RawRect(0, 0, outW, outH));
        videoContext.VideoProcessorSetStreamAutoProcessingMode(processor, 0, false);

        uint* request = stackalloc uint[3] { NvidiaVersion1, NvidiaMethodSuperResolution, enhance ? 1u : 0u };
        videoContext.VideoProcessorSetStreamExtension(processor, 0, NvidiaPpeInterface, 12, (nint)request);

        var streams = new[] { new VideoProcessorStream { Enable = true, InputSurface = inputView } };
        for (int i = 0; i < 10; i++) { videoContext.VideoProcessorBlt(processor, outputView, 0, 1, streams); gpu.WaitForGpu(); }
        var times = new List<double>();
        for (int i = 0; i < 120; i++)
        {
            long t0 = Stopwatch.GetTimestamp();
            videoContext.VideoProcessorBlt(processor, outputView, 0, 1, streams);
            gpu.WaitForGpu();
            times.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
        }
        times.Sort();
        backToBackMs = times[times.Count / 2];

        // The way it would be used: one frame every 16.7 ms, the GPU idle in between (and clocking down).
        times.Clear();
        for (int i = 0; i < 150; i++)
        {
            long t0 = Stopwatch.GetTimestamp();
            videoContext.VideoProcessorBlt(processor, outputView, 0, 1, streams);
            gpu.WaitForGpu();
            double ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            if (i >= 30) times.Add(ms);
            Thread.Sleep(Math.Max(0, (int)(16.7 - ms)));
        }
        times.Sort();
        pacedMs = times[times.Count / 2];
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
