using System.Runtime.InteropServices;
using InterpolatIt.Core.Gpu;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage.Streams;
using WinRT;

namespace InterpolatIt.Core.Recording;

public enum RecordingCodec { Auto, H264, Hevc }

public sealed class RecordingOptions
{
    /// <summary>Auto: H.264 while the picture fits its levels (up to 2560x1440 at 120 fps), HEVC above.</summary>
    public RecordingCodec Codec { get; init; } = RecordingCodec.Auto;

    /// <summary>Bits per second; 0 = chosen from the size and the frame rate.</summary>
    public int Bitrate { get; init; }
}

/// <summary>Where a recording stands; all members can be read from any thread.</summary>
public sealed class RecordingStatus
{
    private long _frames, _dropped;
    public string Path { get; internal init; } = "";
    public int Width { get; internal init; }
    public int Height { get; internal init; }
    public string Codec { get; internal set; } = "";
    public long Frames => Interlocked.Read(ref _frames);
    public long Dropped => Interlocked.Read(ref _dropped);
    public double Seconds { get; internal set; }

    /// <summary>The encoder is running and frames are being written.</summary>
    public bool Active { get; internal set; }

    /// <summary>The file has been finalised (or the recording failed: see <see cref="Error"/>).</summary>
    public bool Finished { get; internal set; }
    public string? Error { get; internal set; }

    /// <summary>Why the recording ended by itself, if it did (the window changed size).</summary>
    public string? EndedBecause { get; internal set; }

    internal void CountFrame() => Interlocked.Increment(ref _frames);
    internal void CountDropped() => Interlocked.Increment(ref _dropped);
}

/// <summary>
/// Writes the pictures that reach the screen to an MP4 file, at the rate they are shown (up to the display's
/// refresh rate, so 120 frames per second on a 120 Hz display).
/// <para>
/// The frames stay on the GPU: each one is copied into a small pool of textures and handed to Media Foundation
/// as a Direct3D surface (MediaStreamSource -> MediaTranscoder), where the colour conversion and the hardware
/// encoder (NVENC, AMF, Quick Sync) take over. Timestamps are the frames' own display times, so a picture that
/// stayed on screen for two refreshes also lasts two refreshes in the file.
/// </para>
/// </summary>
public sealed class Mp4Recorder : IDisposable
{
    private const int PoolSize = 12;

    /// <summary>A texture is reused only after this many later frames have been through the pipeline: an encoder
    /// may still be reading the input a frame or two after the sample was reported as processed.</summary>
    private const int ReuseDistance = 3;

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11SurfaceFromDXGISurface(nint dxgiSurface, out nint graphicsSurface);

    private sealed class Slot
    {
        public required ID3D11Texture2D Texture { get; init; }
        public required IDirect3DSurface Surface { get; init; }
        public bool Busy;
    }

    private readonly GpuDevice _gpu;
    private readonly Slot[] _slots = new Slot[PoolSize];
    private readonly Queue<(int Slot, TimeSpan Time)> _queue = new();
    private readonly Queue<int> _processed = new();
    private readonly Lock _lock = new();
    private readonly AutoResetEvent _available = new(false);
    private readonly ManualResetEventSlim _finished = new(false);
    private readonly FileStream _file;
    private readonly int _frameRate;
    private double _firstTime = double.NaN, _lastTime = double.NegativeInfinity;
    private volatile bool _ready, _ending;
    private bool _disposed;

    public RecordingStatus Status { get; }
    public int Width => Status.Width;
    public int Height => Status.Height;

    private Mp4Recorder(GpuDevice gpu, string path, int width, int height, int frameRate)
    {
        _gpu = gpu;
        _frameRate = frameRate;
        Status = new RecordingStatus { Path = path, Width = width, Height = height };
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        _file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);

        var desc = new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)width, (uint)height, 1, 1,
            BindFlags.RenderTarget | BindFlags.ShaderResource);
        for (int i = 0; i < PoolSize; i++)
        {
            var texture = gpu.Device.CreateTexture2D(desc);
            using var dxgiSurface = texture.QueryInterface<IDXGISurface>();
            Marshal.ThrowExceptionForHR(CreateDirect3D11SurfaceFromDXGISurface(dxgiSurface.NativePointer, out nint inspectable));
            IDirect3DSurface surface;
            try { surface = MarshalInterface<IDirect3DSurface>.FromAbi(inspectable); }
            finally { Marshal.Release(inspectable); }
            _slots[i] = new Slot { Texture = texture, Surface = surface };
        }
    }

    /// <summary>
    /// Starts a recording of pictures of the given size (odd sizes lose their last row or column: video
    /// encoders work on even sizes). Returns at once; frames are taken from the moment the encoder is ready.
    /// </summary>
    public static Mp4Recorder Start(GpuDevice gpu, string path, int width, int height, double refreshRate, RecordingOptions? options = null)
    {
        options ??= new RecordingOptions();
        int w = width & ~1, h = height & ~1;
        if (w < 64 || h < 64) throw new InvalidOperationException($"A picture of {width}x{height} is too small to record.");
        int rate = Math.Clamp((int)Math.Round(refreshRate), 24, 240);
        var recorder = new Mp4Recorder(gpu, path, w, h, rate);
        _ = Task.Run(() => recorder.RunAsync(options));
        return recorder;
    }

    private async Task RunAsync(RecordingOptions options)
    {
        try
        {
            IRandomAccessStream output = _file.AsRandomAccessStream();
            string? refusal = null;
            foreach (RecordingCodec codec in CodecsToTry(options.Codec))
            {
                var source = CreateSource();
                var profile = CreateProfile(codec, options.Bitrate);
                var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true };
                PrepareTranscodeResult prepared = await transcoder.PrepareMediaStreamSourceTranscodeAsync(source, output, profile);
                if (!prepared.CanTranscode)
                {
                    refusal = $"{codec}: {prepared.FailureReason}";
                    continue;
                }
                Status.Codec = codec == RecordingCodec.Hevc ? "HEVC" : "H.264";
                Status.Active = true;
                _ready = true;
                await prepared.TranscodeAsync();
                return;
            }
            Status.Error = $"no video encoder accepted {Width}x{Height} at {_frameRate} fps ({refusal})";
        }
        catch (Exception e)
        {
            Status.Error = $"{e.GetType().Name}: {e.Message}";
        }
        finally
        {
            _ready = false;
            Status.Active = false;
            Status.Finished = true;
            _finished.Set();
        }
    }

    /// <summary>
    /// H.264 level 5.2 ends at 2,073,600 macroblocks per second: 2560x1440 at 120 fps fits, 3840x2160 at 120 does
    /// not. HEVC goes on from there. Whichever comes first, the other is the fallback.
    /// </summary>
    private IEnumerable<RecordingCodec> CodecsToTry(RecordingCodec wanted)
    {
        if (wanted == RecordingCodec.H264) return [RecordingCodec.H264, RecordingCodec.Hevc];
        if (wanted == RecordingCodec.Hevc) return [RecordingCodec.Hevc, RecordingCodec.H264];
        long macroblocksPerSecond = (long)((Width + 15) / 16) * ((Height + 15) / 16) * _frameRate;
        return macroblocksPerSecond <= 2_073_600 ? [RecordingCodec.H264, RecordingCodec.Hevc] : [RecordingCodec.Hevc, RecordingCodec.H264];
    }

    private MediaStreamSource CreateSource()
    {
        var input = VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Bgra8, (uint)Width, (uint)Height);
        input.FrameRate.Numerator = (uint)_frameRate;
        input.FrameRate.Denominator = 1;
        var source = new MediaStreamSource(new VideoStreamDescriptor(input)) { BufferTime = TimeSpan.Zero };
        source.Starting += (_, e) => e.Request.SetActualStartPosition(TimeSpan.Zero);
        source.SampleRequested += OnSampleRequested;
        return source;
    }

    private MediaEncodingProfile CreateProfile(RecordingCodec codec, int bitrate)
    {
        // About 0.09 bits per pixel per frame for H.264 (25 Mbit/s at 1080p120), a third less for HEVC.
        double perPixel = codec == RecordingCodec.Hevc ? 0.06 : 0.09;
        long automatic = (long)(Width * (double)Height * _frameRate * perPixel);
        var profile = new MediaEncodingProfile();
        profile.Container.Subtype = MediaEncodingSubtypes.Mpeg4;
        profile.Video.Subtype = codec == RecordingCodec.Hevc ? MediaEncodingSubtypes.Hevc : MediaEncodingSubtypes.H264;
        profile.Video.Width = (uint)Width;
        profile.Video.Height = (uint)Height;
        profile.Video.Bitrate = (uint)Math.Clamp(bitrate > 0 ? bitrate : automatic, 2_000_000, 150_000_000);
        profile.Video.FrameRate.Numerator = (uint)_frameRate;
        profile.Video.FrameRate.Denominator = 1;
        profile.Video.PixelAspectRatio.Numerator = 1;
        profile.Video.PixelAspectRatio.Denominator = 1;
        return profile;
    }

    /// <summary>
    /// Adds the picture that is on the display at <paramref name="displayTime"/> (seconds on the QPC clock).
    /// Cheap and non-blocking: one GPU copy. A frame is skipped (and counted) when the encoder cannot keep up.
    /// </summary>
    public void AddFrame(ID3D11Texture2D picture, int contentWidth, int contentHeight, double displayTime)
    {
        if (!_ready || _ending) return;
        if ((contentWidth & ~1) != Width || (contentHeight & ~1) != Height)
        {
            // A video has one size. End this file; the caller may start another.
            Status.EndedBecause = "the window changed size";
            Stop();
            return;
        }

        int index = -1;
        TimeSpan time;
        lock (_lock)
        {
            if (displayTime <= _lastTime + 0.0005) return;      // never two pictures for one moment
            if (double.IsNaN(_firstTime)) _firstTime = displayTime;
            for (int i = 0; i < PoolSize; i++)
            {
                if (_slots[i].Busy) continue;
                index = i;
                break;
            }
            if (index < 0)
            {
                Status.CountDropped();
                return;
            }
            _slots[index].Busy = true;
            _lastTime = displayTime;
            time = TimeSpan.FromTicks((long)Math.Round((displayTime - _firstTime) * 1e7));
        }

        lock (_gpu.Sync)
            _gpu.Context.CopySubresourceRegion(_slots[index].Texture, 0, 0, 0, 0, picture, 0, new Vortice.Mathematics.Box(0, 0, 0, Width, Height, 1));

        lock (_lock) _queue.Enqueue((index, time));
        Status.Seconds = time.TotalSeconds;
        _available.Set();
    }

    /// <summary>Called by Media Foundation, on its own thread, whenever the encoder wants the next picture.</summary>
    private void OnSampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
    {
        while (true)
        {
            int index = -1;
            TimeSpan time = default;
            lock (_lock)
            {
                if (_queue.Count > 0) (index, time) = _queue.Dequeue();
                else if (_ending)
                {
                    args.Request.Sample = null;         // end of stream: the file gets finalised
                    return;
                }
            }
            if (index >= 0)
            {
                int slot = index;
                var sample = MediaStreamSample.CreateFromDirect3D11Surface(_slots[slot].Surface, time);
                sample.Duration = TimeSpan.FromTicks(10_000_000 / _frameRate);
                sample.Processed += (_, _) => Release(slot);
                args.Request.Sample = sample;
                Status.CountFrame();
                return;
            }
            _available.WaitOne(50);
        }
    }

    private void Release(int slot)
    {
        lock (_lock)
        {
            _processed.Enqueue(slot);
            while (_processed.Count > ReuseDistance) _slots[_processed.Dequeue()].Busy = false;
        }
    }

    /// <summary>Ends the recording; the file is complete once <see cref="RecordingStatus.Finished"/> is set.</summary>
    public void Stop()
    {
        _ending = true;
        _available.Set();
    }

    /// <summary>Ends the recording and waits until the file has been written out.</summary>
    public bool StopAndWait(int milliseconds = 10_000)
    {
        Stop();
        return _finished.Wait(milliseconds);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopAndWait();
        _file.Dispose();
        lock (_gpu.Sync)        // a copy into the pool may still be on its way
        {
            foreach (Slot slot in _slots)
            {
                (slot.Surface as IDisposable)?.Dispose();
                slot.Texture.Dispose();
            }
        }
        _available.Dispose();
        _finished.Dispose();
    }
}
