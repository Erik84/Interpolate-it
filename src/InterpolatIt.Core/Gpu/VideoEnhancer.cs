using System.Diagnostics;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace InterpolatIt.Core.Gpu;

/// <summary>What <see cref="VideoEnhancer.Check"/> found.</summary>
/// <param name="Engaged">The driver changed the picture when it was asked to enhance it.</param>
/// <param name="ChangedShare">Share of the pixels that differ by more than 2/255 from the plain conversion.</param>
/// <param name="Milliseconds">Time of one enhanced conversion on an otherwise idle GPU (median of a few).</param>
public readonly record struct EnhancerCheck(bool Engaged, double ChangedShare, double Milliseconds);

/// <summary>
/// The graphics driver's own clean-up of compressed video ("RTX Video Super Resolution" on NVIDIA cards): one
/// Direct3D 11 video-processor conversion per frame that removes compression artifacts and sharpens.
/// <para>
/// It is asked for through a private extension of the video processor, the same one browsers and video players
/// use. The driver accepts the request whether or not it then does anything: it only acts when the user has
/// switched the feature on in the vendor's control panel (NVIDIA app > System > Video > RTX Video Enhancement >
/// Super Resolution; the quality level is chosen there too), and it never says which of the two happened.
/// <see cref="Check"/> therefore looks at the pixels: the same picture is converted with and without the
/// request, and the two results are compared.
/// </para>
/// <para>
/// Measured on an RTX 4080 SUPER (driver 621.01): the driver enhances pictures from about 640x300 up to
/// 2560x1440 - not one pixel more - and the cost follows the size of what goes in, not of what comes out: 1.0 ms
/// at 640x360, 2.4 ms at 1280x720, 4.7 ms at 1920x1080, 7.7 ms at 2560x1440 (more when the GPU has clocked down
/// between frames). A larger picture is therefore first scaled down (plainly, 0.2-0.5 ms) to a size the driver
/// takes and enhanced back up to its own size: a 3840x2160 window goes through 1920x1080, which is also what a
/// 1080p stream shown in such a window really consists of.
/// </para>
/// <para>
/// The driver reads its input when it gets round to the work, which can be after the call has returned and
/// after later commands of ours have run. A video player never notices: every frame it hands over is a surface
/// of its own. Copying each frame into one input texture did get noticed: about once in a few hundred frames a
/// conversion came back with the picture of the frame after it. So the driver reads straight from the caller's
/// texture, which the caller leaves alone for a while, and everything in between takes turns.
/// </para>
/// </summary>
public sealed unsafe class VideoEnhancer : IDisposable
{
    private const uint NvidiaVendorId = 0x10DE;

    // Chromium, ui/gl/swap_chain_presenter.cc (ToggleNvidiaVpSuperResolution): { version, method, enable }.
    private static readonly Guid NvidiaExtension = new("d43ce1b3-1f4b-48ac-baee-c3c25375e6f7");
    private const uint NvidiaVersion = 1, NvidiaSuperResolution = 2;

    /// <summary>The most pixels the driver enhances (2560x1440 is enhanced, 2562x1472 is not).</summary>
    public const int MaxPixels = 2560 * 1440;

    /// <summary>The fewest pixels the driver enhances (640x300 is enhanced, 512x288 is not).</summary>
    public const int MinPixels = 640 * 300;

    /// <summary>A conversion that changes fewer of the test picture's pixels than this did not enhance it.</summary>
    private const double EngagedShare = 0.02;

    /// <summary>Textures that take turns: what one conversion reads or writes is left alone by the next two.</summary>
    private const int Lanes = 3;

    private readonly GpuDevice _gpu;
    private readonly ID3D11VideoDevice _videoDevice;
    private readonly ID3D11VideoContext _videoContext;
    private readonly ID3D11VideoContext1 _videoContext1;

    /// <summary>A video processor set up for one conversion (from one size to another).</summary>
    private sealed class Stage : IDisposable
    {
        public required ID3D11VideoProcessorEnumerator Enumerator { get; init; }
        public required ID3D11VideoProcessor Processor { get; init; }

        public void Dispose()
        {
            Processor.Dispose();
            Enumerator.Dispose();
        }
    }

    private Stage? _shrink, _enhance;       // source -> work size (only for pictures the driver does not take), work size -> output
    private ID3D11Texture2D[] _work = [], _outputs = [];
    private ID3D11VideoProcessorOutputView[] _workTargets = [], _outputTargets = [];
    private ID3D11VideoProcessorInputView[] _workInputs = [];
    private readonly Dictionary<nint, ID3D11VideoProcessorInputView> _sourceViews = [];
    private readonly VideoProcessorStream[] _stream = new VideoProcessorStream[1];
    private int _turn;

    /// <summary>Size of the pictures handed to <see cref="Process"/>.</summary>
    public int SourceWidth { get; private set; }
    public int SourceHeight { get; private set; }

    /// <summary>Size at which the driver works: the source's, or smaller when the source is more than it takes.</summary>
    public int InputWidth { get; private set; }
    public int InputHeight { get; private set; }

    public int OutputWidth { get; private set; }
    public int OutputHeight { get; private set; }

    private VideoEnhancer(GpuDevice gpu, ID3D11VideoDevice videoDevice, ID3D11VideoContext videoContext, ID3D11VideoContext1 videoContext1)
    {
        _gpu = gpu;
        _videoDevice = videoDevice;
        _videoContext = videoContext;
        _videoContext1 = videoContext1;
    }

    /// <summary>
    /// The enhancer for this device, or null with the reason when there is none. That one exists does not mean
    /// the driver will do anything: see <see cref="Check"/>.
    /// </summary>
    public static VideoEnhancer? TryCreate(GpuDevice gpu, out string? unavailable)
    {
        unavailable = null;
        if (gpu.Adapter.VendorId != NvidiaVendorId)
        {
            unavailable = "only available on NVIDIA RTX graphics cards for now";
            return null;
        }
        var videoDevice = gpu.Device.QueryInterfaceOrNull<ID3D11VideoDevice>();
        var videoContext = gpu.Context.QueryInterfaceOrNull<ID3D11VideoContext>();
        var videoContext1 = gpu.Context.QueryInterfaceOrNull<ID3D11VideoContext1>();
        if (videoDevice is null || videoContext is null || videoContext1 is null)
        {
            videoContext1?.Dispose();
            videoContext?.Dispose();
            videoDevice?.Dispose();
            unavailable = "this graphics device has no video processing";
            return null;
        }
        return new VideoEnhancer(gpu, videoDevice, videoContext, videoContext1);
    }

    /// <summary>
    /// The size at which a picture is enhanced: its own while the driver takes it, otherwise the largest that
    /// fits when that is still at least four fifths of it (a window just over 2560x1440), otherwise a whole
    /// fraction of it (a half for 3840x2160: the size of the stream such a window usually shows).
    /// </summary>
    public static (int Width, int Height) WorkSize(int width, int height)
    {
        long pixels = (long)width * height;
        if (pixels <= MaxPixels) return (width, height);
        double fit = Math.Sqrt(MaxPixels / (double)pixels);
        double scale = fit >= 0.8 ? fit : 1.0 / Math.Ceiling(1.0 / fit);
        return (Math.Max(2, (int)Math.Floor(width * scale)), Math.Max(2, (int)Math.Floor(height * scale)));
    }

    /// <summary>(Re)creates the video processors and their textures: pictures of the source size go in, enhanced
    /// pictures of the output size come out.</summary>
    public void Configure(int sourceWidth, int sourceHeight, int outputWidth, int outputHeight)
    {
        if (_enhance is not null && sourceWidth == SourceWidth && sourceHeight == SourceHeight
            && outputWidth == OutputWidth && outputHeight == OutputHeight) return;
        lock (_gpu.Sync)
        {
            Release();
            SourceWidth = sourceWidth;
            SourceHeight = sourceHeight;
            (InputWidth, InputHeight) = WorkSize(sourceWidth, sourceHeight);
            OutputWidth = outputWidth;
            OutputHeight = outputHeight;

            _enhance = NewStage(InputWidth, InputHeight, outputWidth, outputHeight);
            Request(_enhance, true);
            bool shrinking = InputWidth != sourceWidth || InputHeight != sourceHeight;
            if (shrinking)
            {
                _shrink = NewStage(sourceWidth, sourceHeight, InputWidth, InputHeight);
                Request(_shrink, false);
            }

            // Work-size textures: what the shrinking stage writes and the enhancing stage reads. Without
            // shrinking only the check needs one (for its test picture).
            int workCount = shrinking ? Lanes : 1;
            _work = new ID3D11Texture2D[workCount];
            _workInputs = new ID3D11VideoProcessorInputView[workCount];
            _workTargets = new ID3D11VideoProcessorOutputView[shrinking ? workCount : 0];
            for (int i = 0; i < workCount; i++)
            {
                _work[i] = NewTexture(InputWidth, InputHeight);
                _workInputs[i] = InputView(_work[i], _enhance);
                if (shrinking) _workTargets[i] = OutputView(_work[i], _shrink!);
            }
            _outputs = new ID3D11Texture2D[Lanes];
            _outputTargets = new ID3D11VideoProcessorOutputView[Lanes];
            for (int i = 0; i < Lanes; i++)
            {
                _outputs[i] = NewTexture(outputWidth, outputHeight);
                _outputTargets[i] = OutputView(_outputs[i], _enhance);
            }
        }
    }

    private ID3D11Texture2D NewTexture(int width, int height)
        => _gpu.Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)width, (uint)height, 1, 1,
            BindFlags.RenderTarget | BindFlags.ShaderResource));

    private ID3D11VideoProcessorInputView InputView(ID3D11Texture2D texture, Stage stage)
        => _videoDevice.CreateVideoProcessorInputView(texture, stage.Enumerator,
            new VideoProcessorInputViewDescription { FourCC = 0, ViewDimension = VideoProcessorInputViewDimension.Texture2D });

    private ID3D11VideoProcessorOutputView OutputView(ID3D11Texture2D texture, Stage stage)
        => _videoDevice.CreateVideoProcessorOutputView(texture, stage.Enumerator,
            new VideoProcessorOutputViewDescription { ViewDimension = VideoProcessorOutputViewDimension.Texture2D });

    private Stage NewStage(int fromWidth, int fromHeight, int toWidth, int toHeight)
    {
        var content = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputFrameRate = new Rational(60, 1),
            InputWidth = (uint)fromWidth,
            InputHeight = (uint)fromHeight,
            OutputFrameRate = new Rational(60, 1),
            OutputWidth = (uint)toWidth,
            OutputHeight = (uint)toHeight,
            Usage = VideoUsage.PlaybackNormal,
        };
        var enumerator = _videoDevice.CreateVideoProcessorEnumerator(content);
        var processor = _videoDevice.CreateVideoProcessor(enumerator, 0);

        // Window captures are full-range sRGB, and so is what goes back to the screen.
        _videoContext1.VideoProcessorSetStreamColorSpace1(processor, 0, ColorSpaceType.RgbFullG22NoneP709);
        _videoContext1.VideoProcessorSetOutputColorSpace1(processor, ColorSpaceType.RgbFullG22NoneP709);
        _videoContext.VideoProcessorSetStreamFrameFormat(processor, 0, VideoFrameFormat.Progressive);
        _videoContext.VideoProcessorSetStreamSourceRect(processor, 0, true, new Vortice.RawRect(0, 0, fromWidth, fromHeight));
        _videoContext.VideoProcessorSetStreamDestRect(processor, 0, true, new Vortice.RawRect(0, 0, toWidth, toHeight));
        _videoContext.VideoProcessorSetOutputTargetRect(processor, true, new Vortice.RawRect(0, 0, toWidth, toHeight));
        _videoContext.VideoProcessorSetStreamAutoProcessingMode(processor, 0, false);
        return new Stage { Enumerator = enumerator, Processor = processor };
    }

    /// <summary>Asks the driver to enhance a stage's conversions, or to convert plainly.</summary>
    private void Request(Stage stage, bool enhance)
    {
        // The result says nothing about what the driver will do with it (see the class comment), so it is not
        // looked at: a driver that does not know the extension refuses it, and Check() then finds no difference.
        uint* request = stackalloc uint[3] { NvidiaVersion, NvidiaSuperResolution, enhance ? 1u : 0u };
        _ = _videoContext.VideoProcessorSetStreamExtension(stage.Processor, 0, NvidiaExtension, 3 * sizeof(uint), (nint)request);
    }

    private void Convert(Stage stage, ID3D11VideoProcessorInputView from, ID3D11VideoProcessorOutputView to)
    {
        _stream[0] = new VideoProcessorStream { Enable = true, InputSurface = from };
        _videoContext.VideoProcessorBlt(stage.Processor, to, 0, 1, _stream).CheckError();
    }

    /// <summary>The driver's view of a caller's texture (made once per texture).</summary>
    private ID3D11VideoProcessorInputView SourceView(ID3D11Texture2D texture)
    {
        if (_sourceViews.TryGetValue(texture.NativePointer, out var view)) return view;
        if (_sourceViews.Count >= 64) ForgetSources();
        view = InputView(texture, _shrink ?? _enhance!);
        _sourceViews[texture.NativePointer] = view;
        return view;
    }

    /// <summary>Lets go of the textures handed to <see cref="Process"/> so far (call when they are being replaced).</summary>
    public void ForgetSources()
    {
        lock (_gpu.Sync)
        {
            foreach (var view in _sourceViews.Values) view.Dispose();
            _sourceViews.Clear();
        }
    }

    /// <summary>
    /// Enhances the top-left <see cref="SourceWidth"/> x <see cref="SourceHeight"/> of <paramref name="source"/>
    /// and writes the result (<see cref="OutputWidth"/> x <see cref="OutputHeight"/>) to the top-left of
    /// <paramref name="destination"/>, which may be the source itself. The work is queued on the GPU like any
    /// draw; nothing is waited for here.
    /// <para>
    /// The driver reads the source itself, and possibly later than this call (see the class comment): apart
    /// from being the destination, the source must be left alone until a few more frames have been processed.
    /// It needs the render-target binding, like every texture of the frame store has.
    /// </para>
    /// </summary>
    public void Process(ID3D11Texture2D source, ID3D11Texture2D destination)
    {
        if (_enhance is null) throw new InvalidOperationException("Configure must be called first.");
        lock (_gpu.Sync)
        {
            _turn = (_turn + 1) % Lanes;
            var from = SourceView(source);
            if (_shrink is not null)
            {
                Convert(_shrink, from, _workTargets[_turn]);
                Convert(_enhance, _workInputs[_turn], _outputTargets[_turn]);
            }
            else
            {
                Convert(_enhance, from, _outputTargets[_turn]);
            }
            _gpu.Context.CopySubresourceRegion(destination, 0, 0, 0, 0, _outputs[_turn], 0,
                new Vortice.Mathematics.Box(0, 0, 0, OutputWidth, OutputHeight, 1));
        }
    }

    /// <summary>
    /// Finds out whether the driver really enhances pictures of the configured size: a test picture is converted
    /// plainly and with the request, and the results are compared. Takes a few tens of milliseconds (the first
    /// time more: the driver loads its network), so not something to do between two frames that must be on time.
    /// </summary>
    public EnhancerCheck Check()
    {
        if (_enhance is null) throw new InvalidOperationException("Configure must be called first.");
        byte[] picture = TestPicture(InputWidth, InputHeight);
        lock (_gpu.Sync)
        {
            fixed (byte* p = picture)
                _gpu.Context.UpdateSubresource(_work[0], 0, null, (nint)p, (uint)(InputWidth * 4), (uint)picture.Length);
        }
        byte[] plain = ConvertTestPicture(enhance: false, rounds: 1, out _);
        byte[] enhanced = ConvertTestPicture(enhance: true, rounds: 8, out double milliseconds);

        long changed = 0;
        for (int i = 0; i < plain.Length; i += 4)
        {
            int d = Math.Max(Math.Abs(plain[i] - enhanced[i]),
                Math.Max(Math.Abs(plain[i + 1] - enhanced[i + 1]), Math.Abs(plain[i + 2] - enhanced[i + 2])));
            if (d > 2) changed++;
        }
        double share = changed / (plain.Length / 4.0);
        return new EnhancerCheck(share > EngagedShare, share, milliseconds);
    }

    /// <summary>Converts the test picture (in the first work texture); the time is the median of the later rounds.</summary>
    private byte[] ConvertTestPicture(bool enhance, int rounds, out double milliseconds)
    {
        var times = new List<double>(rounds);
        for (int i = 0; i < rounds; i++)
        {
            long t0 = Stopwatch.GetTimestamp();
            lock (_gpu.Sync)
            {
                if (i == 0) Request(_enhance!, enhance);
                Convert(_enhance!, _workInputs[0], _outputTargets[0]);
            }
            _gpu.WaitForGpu();
            if (i >= rounds / 2) times.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);     // the first ones warm up
        }
        times.Sort();
        milliseconds = times[times.Count / 2];
        byte[] result = _gpu.ReadBack(_outputs[0], OutputWidth, OutputHeight);
        lock (_gpu.Sync) Request(_enhance!, true);
        return result;
    }

    /// <summary>
    /// A picture with what a video encoder leaves behind: smooth colour fields cut into 8-pixel blocks that are
    /// each a few levels off, and hard-edged shapes with speckle around their edges. An enhancement that works
    /// changes a large part of it (38 % of the pixels, measured); the detail is the same at every picture size.
    /// </summary>
    public static byte[] TestPicture(int width, int height)
    {
        var pixels = new byte[(long)width * height * 4];
        Parallel.For(0, height, y =>
        {
            int row = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                double u = x / 240.0, v = y / 240.0;
                double r = 0.5 + 0.3 * Math.Sin(2.1 * u + 0.7 * v);
                double g = 0.5 + 0.3 * Math.Sin(1.3 * v - 1.1 * u + 1.0);
                double b = 0.5 + 0.3 * Math.Cos(1.7 * (u + v));

                // Discs on a 160-pixel lattice, with a hard edge.
                int cx = x % 160 - 80, cy = y % 160 - 80;
                double radius = Math.Sqrt(cx * cx + cy * cy);
                if (radius < 48)
                {
                    r = 1.0 - r;
                    b = 0.25 + 0.5 * b;
                }

                // Blocking: every 8x8 block a few levels off. Ringing: speckle within 6 pixels of an edge.
                double levels = (int)(Hash((uint)(x >> 3), (uint)(y >> 3)) & 15) - 7.5;
                double edge = Math.Abs(radius - 48);
                if (edge < 6) levels += ((int)(Hash((uint)x + 977u, (uint)y + 131u) & 31) - 15.5) * (6 - edge) / 6;
                double offset = levels / 255.0;

                pixels[row + 4 * x] = ToByte(b + offset);
                pixels[row + 4 * x + 1] = ToByte(g + offset);
                pixels[row + 4 * x + 2] = ToByte(r + offset);
                pixels[row + 4 * x + 3] = 255;
            }
        });
        return pixels;
    }

    private static byte ToByte(double value) => (byte)Math.Clamp((int)Math.Round(value * 255), 0, 255);

    private static uint Hash(uint a, uint b)
    {
        uint v = (a * 73856093u) ^ (b * 19349663u);
        v ^= v >> 13;
        v *= 0x5bd1e995u;
        v ^= v >> 15;
        return v;
    }

    private void Release()
    {
        ForgetSources();
        foreach (var view in _workTargets) view.Dispose();
        foreach (var view in _workInputs) view.Dispose();
        foreach (var view in _outputTargets) view.Dispose();
        foreach (var texture in _work) texture.Dispose();
        foreach (var texture in _outputs) texture.Dispose();
        _workTargets = [];
        _workInputs = [];
        _outputTargets = [];
        _work = [];
        _outputs = [];
        _shrink?.Dispose();
        _enhance?.Dispose();
        _shrink = null;
        _enhance = null;
    }

    public void Dispose()
    {
        lock (_gpu.Sync) Release();
        _videoContext1.Dispose();
        _videoContext.Dispose();
        _videoDevice.Dispose();
    }
}
