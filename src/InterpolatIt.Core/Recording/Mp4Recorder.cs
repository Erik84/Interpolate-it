using System.Runtime.InteropServices;
using InterpolatIt.Core.Gpu;
using InterpolatIt.Core.Native;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace InterpolatIt.Core.Recording;

public enum RecordingCodec { Auto, H264, Hevc }

public sealed class RecordingOptions
{
    /// <summary>Auto: H.264 while the picture fits its levels (up to 2560x1440 at 120 fps), HEVC above.</summary>
    public RecordingCodec Codec { get; init; } = RecordingCodec.Auto;

    /// <summary>Bits per second; 0 = chosen from the size and the frame rate.</summary>
    public int Bitrate { get; init; }

    /// <summary>Record the sound that this process and the processes it started are playing (not the rest of
    /// the system); 0 = no sound.</summary>
    public uint AudioProcessId { get; init; }

    /// <summary>Seconds by which the picture being recorded runs behind the application's own picture (the
    /// playback delay of the interpolation). The sound is put that much later in the file, so that the two
    /// belong together there the way they did in the application.</summary>
    public double AudioDelay { get; init; }
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

    /// <summary>The file gets a sound track with what the application plays.</summary>
    public bool Sound { get; internal set; }

    /// <summary>Why there is no sound track although one was asked for; null otherwise.</summary>
    public string? SoundProblem { get; internal set; }

    internal void CountFrame() => Interlocked.Increment(ref _frames);
    internal void CountDropped() => Interlocked.Increment(ref _dropped);
}

/// <summary>
/// Writes the pictures that reach the screen to an MP4 file, at the rate they are shown (up to the display's
/// refresh rate, so 120 frames per second on a 120 Hz display), with the sound of the application if wanted.
/// <para>
/// The frames stay on the GPU. Each one is converted to the encoder's own format (NV12) by a shader of ours,
/// into one of a small pool of textures, and handed to a Media Foundation sink writer that works on this same
/// Direct3D device: the hardware encoder (NVENC, AMF, Quick Sync) reads the texture where it is. Timestamps
/// are the frames' own display times, so a picture that stayed on screen for two refreshes also lasts two
/// refreshes in the file.
/// </para>
/// <para>
/// Both choices come from frames that went missing. Version 1.0.0 handed BGRA textures to a MediaStreamSource
/// and a MediaTranscoder, which works on a Direct3D device of its own. The picture then has to cross from our
/// device to that one, and nothing makes the reader wait for the writer: with the GPU busy (three inferences
/// per source frame at x4) one frame in four was encoded before its picture had arrived - black - and with our
/// own conversion to NV12 in front of the same route, one in a few hundred with only its lower part. On one
/// device, everything happens in the order it was asked for.
/// </para>
/// <para>
/// Sound comes from <see cref="ProcessAudioCapture"/> and goes in as AAC. A file's streams are written
/// interleaved, so the picture may not fall silent while the sound goes on: when no new picture has come for a
/// tenth of a second (a paused video with music) the last one is repeated.
/// </para>
/// <para>
/// One thread of the recorder's own talks to the sink writer; <see cref="AddFrame"/> only converts the picture
/// and queues it, so the presenter never waits for the encoder.
/// </para>
/// </summary>
public sealed class Mp4Recorder : IDisposable
{
    private const int PoolSize = 12;

    /// <summary>A texture is used again only after this many later frames have been let go of by the encoder too.</summary>
    private const int ReuseDistance = 2;

    /// <summary>The sound is handed to the encoder this far behind the clock: that long a packet has to arrive.</summary>
    private const double AudioGuard = 0.120;

    /// <summary>With sound: the longest the picture stream goes without a frame before the last one is repeated.</summary>
    private const double KeepAliveInterval = 0.100;

    private const uint AudioBitrate = 192_000;

    private sealed class Slot
    {
        public required ID3D11Texture2D Texture { get; init; }
        public required ID3D11RenderTargetView Luma { get; init; }
        public required ID3D11RenderTargetView Chroma { get; init; }

        /// <summary>The callback (a COM pointer) by which Media Foundation says it is done with this texture.</summary>
        public nint Released;
        public bool Busy;
    }

    /// <summary>Called by Media Foundation when the encoder has let go of the sample that held a pool texture.</summary>
    private sealed class SampleReleased(Mp4Recorder owner, int slot) : Mf.IMFAsyncCallback
    {
        public int GetParameters(out uint flags, out uint queue)
        {
            flags = 0;
            queue = 0;
            return unchecked((int)0x80004001);      // E_NOTIMPL: nothing special about this callback
        }

        public int Invoke(nint asyncResult)
        {
            owner.Release(slot);
            return 0;
        }
    }

    private enum Opened { Yes, PictureRefused, SoundRefused }

    private readonly GpuDevice _gpu;
    private readonly Slot[] _slots = new Slot[PoolSize];
    private readonly ID3D11Texture2D _picture;
    private readonly ID3D11ShaderResourceView _pictureView;
    private readonly ID3D11VertexShader _vs;
    private readonly ID3D11PixelShader _lumaPs, _chromaPs;
    private readonly Queue<(int Slot, long Time)> _queue = new();
    private readonly Queue<int> _processed = new();
    private readonly Lock _lock = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly ManualResetEventSlim _finished = new(false);
    private readonly int _frameRate;
    private readonly uint _audioProcessId;
    private readonly double _audioDelay;
    private double _firstTime = double.NaN, _lastTime = double.NegativeInfinity, _lastAddedAt, _endTime;
    private int _lastSlot = -1;
    private volatile bool _ready, _ending;
    private bool _disposed;

    private Thread? _thread;
    private ProcessAudioCapture? _audio;
    private AudioTimeline? _timeline;
    private nint _deviceManager, _writer;
    private uint _videoStream, _audioStream;

    public RecordingStatus Status { get; }
    public int Width => Status.Width;
    public int Height => Status.Height;

    private Mp4Recorder(GpuDevice gpu, string path, int width, int height, int frameRate, RecordingOptions options)
    {
        _gpu = gpu;
        _frameRate = frameRate;
        _audioProcessId = options.AudioProcessId;
        _audioDelay = Math.Clamp(options.AudioDelay, 0, 0.5);
        Status = new RecordingStatus { Path = System.IO.Path.GetFullPath(path), Width = width, Height = height };
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Status.Path)!);

        if ((gpu.Device.CheckFormatSupport(Format.NV12) & FormatSupport.RenderTarget) == 0)
            throw new InvalidOperationException("This graphics card cannot draw video frames (NV12), which recording needs.");
        var pool = new Texture2DDescription(Format.NV12, (uint)width, (uint)height, 1, 1, BindFlags.RenderTarget | BindFlags.ShaderResource);
        for (int i = 0; i < PoolSize; i++)
        {
            var texture = gpu.Device.CreateTexture2D(pool);
            _slots[i] = new Slot
            {
                Texture = texture,
                // The two planes of an NV12 texture are drawn to through views of their own.
                Luma = gpu.Device.CreateRenderTargetView(texture, new RenderTargetViewDescription(texture, RenderTargetViewDimension.Texture2D, Format.R8_UNorm)),
                Chroma = gpu.Device.CreateRenderTargetView(texture, new RenderTargetViewDescription(texture, RenderTargetViewDimension.Texture2D, Format.R8G8_UNorm)),
                Released = Marshal.GetComInterfaceForObject<SampleReleased, Mf.IMFAsyncCallback>(new SampleReleased(this, i)),
            };
        }
        _picture = gpu.Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)width, (uint)height, 1, 1, BindFlags.ShaderResource));
        _pictureView = gpu.Device.CreateShaderResourceView(_picture);
        _vs = gpu.Device.CreateVertexShader(ShaderLibrary.Compile("Fullscreen.hlsl", "main", "vs_5_0"));
        _lumaPs = gpu.Device.CreatePixelShader(ShaderLibrary.Compile("ToNv12.hlsl", "Luma", "ps_5_0"));
        _chromaPs = gpu.Device.CreatePixelShader(ShaderLibrary.Compile("ToNv12.hlsl", "Chroma", "ps_5_0"));
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
        var recorder = new Mp4Recorder(gpu, path, w, h, rate, options);
        var thread = new Thread(() => recorder.Run(options)) { IsBackground = true, Name = "InterpolatIt recording" };
        thread.SetApartmentState(ApartmentState.MTA);
        recorder._thread = thread;
        thread.Start();
        return recorder;
    }

    // ------------------------------------------------------------------ the recorder's own thread

    private void Run(RecordingOptions options)
    {
        try
        {
            Mf.Startup();
            if (_audioProcessId != 0)
            {
                // Without sound is better than not at all: a refusal becomes a remark, not a failure.
                try
                {
                    _audio = ProcessAudioCapture.Start(_audioProcessId);
                    _timeline = new AudioTimeline();
                }
                catch (Exception e) when (e is InvalidOperationException or COMException or InvalidCastException
                                              or DllNotFoundException or EntryPointNotFoundException)
                {
                    Status.SoundProblem = e.Message;
                }
            }

            // The encoder works on our Direct3D device: it is handed over through a "device manager".
            Mf.Check(Mf.MFCreateDXGIDeviceManager(out uint resetToken, out _deviceManager), "set up the encoder");
            Mf.ResetDevice(_deviceManager, _gpu.Device.NativePointer, resetToken);

            string? refusal = null;
            foreach (RecordingCodec codec in CodecsToTry(options.Codec))
            {
                Opened opened = Open(codec, options.Bitrate, _audio is not null, out int reason);
                if (opened == Opened.SoundRefused)
                {
                    Status.SoundProblem = $"Windows has no encoder for the sound (0x{reason:X8})";
                    _audio?.Dispose();
                    _audio = null;
                    _timeline = null;
                    opened = Open(codec, options.Bitrate, false, out reason);
                }
                if (opened == Opened.Yes)
                {
                    Status.Codec = NameOf(codec);
                    break;
                }
                refusal = $"{NameOf(codec)}: 0x{reason:X8}";
            }
            if (_writer == 0)
            {
                Status.Error = $"no video encoder accepted {Width}x{Height} at {_frameRate} fps ({refusal})";
                return;
            }

            Status.Sound = _audio is not null;
            Status.Active = true;
            _ready = true;
            WriteUntilStopped();
            _ready = false;
            Mf.Check(Mf.Finalize(_writer), "finish the file");
        }
        catch (DllNotFoundException)
        {
            Status.Error = "Media Foundation is missing (on Windows N: install the Media Feature Pack)";
        }
        catch (Exception e)
        {
            Status.Error = e is COMException ? e.Message : $"{e.GetType().Name}: {e.Message}";
        }
        finally
        {
            _ready = false;
            Status.Active = false;
            // The sound capture ends here, on the thread that started it: its COM objects cannot be used from a
            // thread of another kind (the application's window thread; found as an InvalidCastException there).
            _audio?.Dispose();
            _audio = null;
            Mf.Release(ref _writer);
            Mf.Release(ref _deviceManager);
            Status.Finished = true;
            _finished.Set();
        }
    }

    private static string NameOf(RecordingCodec codec) => codec == RecordingCodec.Hevc ? "HEVC" : "H.264";

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

    /// <summary>Creates the file and its encoders. A refusal of the picture's or the sound's format is an answer
    /// (with Windows' reason in <paramref name="reason"/>); anything else that goes wrong is thrown.</summary>
    private Opened Open(RecordingCodec codec, int bitrate, bool sound, out int reason)
    {
        reason = 0;
        nint attributes = 0, writer = 0;
        try
        {
            if (File.Exists(Status.Path)) File.Delete(Status.Path);
            Mf.Check(Mf.MFCreateAttributes(out attributes, 8), "prepare the recording");
            Mf.SetUInt32(attributes, Mf.EnableHardwareTransforms, 1);
            Mf.SetUInt32(attributes, Mf.DisableThrottling, 1);          // never make the caller wait: frames are dropped instead
            Mf.SetUInt32(attributes, Mf.LowLatency, 1);                 // no frames held back inside the encoder
            Mf.SetGuid(attributes, Mf.ContainerType, Mf.ContainerMpeg4);
            Mf.SetUnknown(attributes, Mf.SinkWriterD3DManager, _deviceManager);
            Mf.Check(Mf.MFCreateSinkWriterFromURL(Status.Path, 0, attributes, out writer), "create the file");

            // (This is where the time goes when a recording starts: the encoder takes about 0.2 s to get ready.)
            reason = AddPicture(writer, codec, bitrate, out uint video);
            if (reason < 0) return Opened.PictureRefused;
            uint audio = 0;
            if (sound)
            {
                reason = AddSound(writer, out audio);
                if (reason < 0) return Opened.SoundRefused;
            }
            reason = Mf.BeginWriting(writer);
            if (reason < 0) return Opened.PictureRefused;

            _writer = writer;
            writer = 0;
            _videoStream = video;
            _audioStream = audio;
            return Opened.Yes;
        }
        finally
        {
            Mf.Release(ref attributes);
            if (writer != 0)
            {
                Mf.Release(ref writer);
                try { File.Delete(Status.Path); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private int AddPicture(nint writer, RecordingCodec codec, int bitrate, out uint stream)
    {
        stream = 0;
        // About 0.09 bits per pixel per frame for H.264 (25 Mbit/s at 1080p120), a third less for HEVC.
        double perPixel = codec == RecordingCodec.Hevc ? 0.06 : 0.09;
        long automatic = (long)(Width * (double)Height * _frameRate * perPixel);
        nint output = 0, input = 0, parameters = 0;
        try
        {
            Mf.Check(Mf.MFCreateMediaType(out output), "describe the video");
            Mf.SetGuid(output, Mf.MajorType, Mf.Video);
            Mf.SetGuid(output, Mf.Subtype, codec == RecordingCodec.Hevc ? Mf.Hevc : Mf.H264);
            Mf.SetUInt32(output, Mf.AverageBitrate, (uint)Math.Clamp(bitrate > 0 ? bitrate : automatic, 2_000_000, 150_000_000));
            Mf.SetUInt32(output, Mf.Mpeg2Profile, codec == RecordingCodec.Hevc ? Mf.HevcMain : Mf.H264High);
            DescribePicture(output);
            int hr = Mf.AddStream(writer, output, out stream);
            if (hr < 0) return hr;

            Mf.Check(Mf.MFCreateMediaType(out input), "describe the pictures");
            Mf.SetGuid(input, Mf.MajorType, Mf.Video);
            Mf.SetGuid(input, Mf.Subtype, Mf.Nv12);
            Mf.SetUInt32(input, Mf.AllSamplesIndependent, 1);
            DescribePicture(input);

            // A key frame every second, so that a player can jump to any second. (An encoder that does not know
            // the setting gets the pictures' format without it.)
            Mf.Check(Mf.MFCreateAttributes(out parameters, 1), "describe the video");
            Mf.SetUInt32(parameters, Mf.EncoderGopSize, (uint)_frameRate);
            hr = Mf.SetInputMediaType(writer, stream, input, parameters);
            return hr < 0 ? Mf.SetInputMediaType(writer, stream, input, 0) : hr;
        }
        finally
        {
            Mf.Release(ref output);
            Mf.Release(ref input);
            Mf.Release(ref parameters);
        }
    }

    private void DescribePicture(nint type)
    {
        Mf.SetUInt32(type, Mf.InterlaceMode, Mf.Progressive);
        Mf.SetPair(type, Mf.FrameSize, (uint)Width, (uint)Height);
        Mf.SetPair(type, Mf.FrameRate, (uint)_frameRate, 1);
        Mf.SetPair(type, Mf.PixelAspectRatio, 1, 1);
        // What ToNv12.hlsl makes: BT.709 in limited range. Said in the file, so that a player need not guess.
        Mf.SetUInt32(type, Mf.VideoPrimaries, Mf.PrimariesBt709);
        Mf.SetUInt32(type, Mf.TransferFunction, Mf.TransferBt709);
        Mf.SetUInt32(type, Mf.YuvMatrix, Mf.MatrixBt709);
        Mf.SetUInt32(type, Mf.VideoNominalRange, Mf.Range16To235);
    }

    private static int AddSound(nint writer, out uint stream)
    {
        stream = 0;
        const uint rate = AudioTimeline.Rate, channels = ProcessAudioCapture.Channels, frameBytes = AudioTimeline.FrameBytes;
        nint output = 0, input = 0;
        try
        {
            Mf.Check(Mf.MFCreateMediaType(out output), "describe the sound");
            Mf.SetGuid(output, Mf.MajorType, Mf.Audio);
            Mf.SetGuid(output, Mf.Subtype, Mf.Aac);
            Mf.SetUInt32(output, Mf.AudioBitsPerSample, 16);
            Mf.SetUInt32(output, Mf.AudioSamplesPerSecond, rate);
            Mf.SetUInt32(output, Mf.AudioChannels, channels);
            Mf.SetUInt32(output, Mf.AudioAverageBytesPerSecond, AudioBitrate / 8);
            Mf.SetUInt32(output, Mf.AacPayloadType, 0);                 // raw AAC, as an MP4 file holds it
            Mf.SetUInt32(output, Mf.AacProfileLevel, 0x29);             // AAC-LC, two channels
            int hr = Mf.AddStream(writer, output, out stream);
            if (hr < 0) return hr;

            Mf.Check(Mf.MFCreateMediaType(out input), "describe the sound");
            Mf.SetGuid(input, Mf.MajorType, Mf.Audio);
            Mf.SetGuid(input, Mf.Subtype, Mf.Pcm);
            Mf.SetUInt32(input, Mf.AudioBitsPerSample, 16);
            Mf.SetUInt32(input, Mf.AudioSamplesPerSecond, rate);
            Mf.SetUInt32(input, Mf.AudioChannels, channels);
            Mf.SetUInt32(input, Mf.AudioBlockAlignment, frameBytes);
            Mf.SetUInt32(input, Mf.AudioAverageBytesPerSecond, rate * frameBytes);
            Mf.SetUInt32(input, Mf.AllSamplesIndependent, 1);
            return Mf.SetInputMediaType(writer, stream, input, 0);
        }
        finally
        {
            Mf.Release(ref output);
            Mf.Release(ref input);
        }
    }

    /// <summary>
    /// Hands the encoder what has been queued: pictures at once, sound a little behind the clock (see
    /// <see cref="AudioGuard"/>). Returns when the recording has been stopped and everything is in.
    /// </summary>
    private void WriteUntilStopped()
    {
        var chunk = new byte[AudioTimeline.ChunkFrames * AudioTimeline.FrameBytes];
        WaitHandle[] signals = _audio is null ? [_wake] : [_wake, _audio.DataAvailable];
        while (true)
        {
            WaitHandle.WaitAny(signals, 10);
            bool ending;
            double first;
            lock (_lock)
            {
                ending = _ending;
                first = _firstTime;
            }

            if (_audio is not null)
            {
                // (Also once more when the recording has been stopped: what was played up to that moment.)
                _audio.Drain((pcm, frames, time, silent) =>
                {
                    if (!double.IsNaN(first)) _timeline!.Write(pcm, frames, time + _audioDelay - first, silent);
                });
                if (!ending) RepeatPictureIfStill();
            }

            while (true)
            {
                (int Slot, long Time) next;
                lock (_lock)
                {
                    if (_queue.Count == 0) break;
                    next = _queue.Dequeue();
                }
                WritePicture(next.Slot, next.Time);
            }

            if (_timeline is not null && !double.IsNaN(first))
            {
                // At the end: what is left, up to the end of the last picture.
                double upTo = ending ? _endTime : Qpc.Now + _audioDelay - first - AudioGuard;
                while (_timeline.TryRead(upTo, chunk, out long position)) WriteSound(chunk, position);
            }

            if (ending)
            {
                lock (_lock)
                {
                    if (_queue.Count == 0) return;
                }
            }
        }
    }

    private void WritePicture(int index, long time)
    {
        Slot slot = _slots[index];
        nint buffer = 0, tracked = 0, sample = 0;
        bool handedOver = false;
        try
        {
            Mf.Check(Mf.MFCreateDXGISurfaceBuffer(in Mf.IidTexture2D, slot.Texture.NativePointer, 0, 0, out buffer), "wrap a picture");
            Mf.SetCurrentLength(buffer, Mf.ContiguousLength(buffer));
            // A sample that says when its last user has let go of it: only then is the texture free again.
            Mf.Check(Mf.MFCreateTrackedSample(out tracked), "wrap a picture");
            Mf.Check(Marshal.QueryInterface(tracked, in Mf.IidSample, out sample), "wrap a picture");
            Mf.AddBuffer(sample, buffer);
            Mf.SetSampleTime(sample, time);
            Mf.SetSampleDuration(sample, 10_000_000 / _frameRate);
            Mf.SetAllocator(tracked, slot.Released);
            handedOver = true;
            Mf.Check(Mf.WriteSample(_writer, _videoStream, sample), "encode a picture");
            Status.CountFrame();
        }
        finally
        {
            Mf.Release(ref sample);
            Mf.Release(ref tracked);
            Mf.Release(ref buffer);
            if (!handedOver) Release(index);
        }
    }

    private unsafe void WriteSound(byte[] chunk, long position)
    {
        nint buffer = 0, sample = 0;
        try
        {
            Mf.Check(Mf.MFCreateMemoryBuffer((uint)chunk.Length, out buffer), "wrap the sound");
            Marshal.Copy(chunk, 0, (nint)Mf.Lock(buffer), chunk.Length);
            Mf.Unlock(buffer);
            Mf.SetCurrentLength(buffer, (uint)chunk.Length);
            Mf.Check(Mf.MFCreateSample(out sample), "wrap the sound");
            Mf.AddBuffer(sample, buffer);
            Mf.SetSampleTime(sample, position * 10_000_000L / AudioTimeline.Rate);
            Mf.SetSampleDuration(sample, AudioTimeline.ChunkFrames * 10_000_000L / AudioTimeline.Rate);
            Mf.Check(Mf.WriteSample(_writer, _audioStream, sample), "encode the sound");
        }
        finally
        {
            Mf.Release(ref sample);
            Mf.Release(ref buffer);
        }
    }

    private void RepeatPictureIfStill()
    {
        Slot last;
        double time;
        lock (_lock)
        {
            if (!_ready || _ending || _lastSlot < 0 || Qpc.Now - _lastAddedAt < KeepAliveInterval) return;
            last = _slots[_lastSlot];
            time = Math.Max(_lastTime + KeepAliveInterval, Qpc.Now);
        }
        Add(null, last, Width, Height, time);
    }

    // ------------------------------------------------------------------ called by the engine's threads

    /// <summary>
    /// Adds the picture that is on the display at <paramref name="displayTime"/> (seconds on the QPC clock).
    /// Cheap and non-blocking: one GPU copy and two small draws. A frame is skipped (and counted) when the
    /// encoder cannot keep up.
    /// </summary>
    public void AddFrame(ID3D11Texture2D picture, int contentWidth, int contentHeight, double displayTime)
        => Add(picture, null, contentWidth, contentHeight, displayTime);

    /// <summary>Adds a picture, or once more one that is in the pool already (<paramref name="repeat"/>).</summary>
    private void Add(ID3D11Texture2D? picture, Slot? repeat, int contentWidth, int contentHeight, double displayTime)
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
        long time;
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
            time = (long)Math.Round((displayTime - _firstTime) * 1e7);
        }

        using (_gpu.UseContext())
        {
            if (repeat is null) Convert(picture!, _slots[index]);
            else if (!ReferenceEquals(repeat, _slots[index])) _gpu.Context.CopyResource(_slots[index].Texture, repeat.Texture);
            // (A repeated picture can get its own texture back: then there is nothing to copy.)
            _gpu.Context.Flush();       // on its way before the encoder's thread starts on it
        }

        lock (_lock)
        {
            _queue.Enqueue((index, time));
            _lastSlot = index;
            _lastAddedAt = Qpc.Now;
        }
        _wake.Set();
        Status.Seconds = time * 1e-7;
    }

    /// <summary>BGRA picture -> the two planes of an NV12 pool texture (see Shaders/ToNv12.hlsl). Inside UseContext.</summary>
    private void Convert(ID3D11Texture2D picture, Slot slot)
    {
        var ctx = _gpu.Context;
        // The picture's own texture is not necessarily readable by a shader, and its owner goes on with it: a copy first.
        ctx.CopySubresourceRegion(_picture, 0, 0, 0, 0, picture, 0, new Vortice.Mathematics.Box(0, 0, 0, Width, Height, 1));
        ctx.IASetInputLayout(null);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        ctx.VSSetShader(_vs);
        ctx.PSSetShaderResource(0, _pictureView);
        ctx.OMSetRenderTargets(slot.Luma);
        ctx.RSSetViewport(0, 0, Width, Height);
        ctx.PSSetShader(_lumaPs);
        ctx.Draw(3, 0);
        ctx.OMSetRenderTargets(slot.Chroma);
        ctx.RSSetViewport(0, 0, Width / 2, Height / 2);
        ctx.PSSetShader(_chromaPs);
        ctx.Draw(3, 0);
        ctx.PSUnsetShaderResources(0, 1);
        ctx.OMSetRenderTargets(0, [], null!);
    }

    /// <summary>The encoder has let go of a texture (called on one of Media Foundation's threads).</summary>
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
        lock (_lock)
        {
            if (_ending) return;
            _ending = true;
            _endTime = double.IsNaN(_firstTime) ? 0 : _lastTime - _firstTime + 1.0 / _frameRate;
        }
        _wake.Set();
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
        // An encoder that will not finish keeps what it works with: better a leak than pulling it away.
        if (_thread is { } thread && !thread.Join(2000)) return;
        using (_gpu.UseContext())        // a conversion into the pool may still be on its way
        {
            foreach (Slot slot in _slots)
            {
                slot.Luma.Dispose();
                slot.Chroma.Dispose();
                slot.Texture.Dispose();
                Mf.Release(ref slot.Released);
            }
            _pictureView.Dispose();
            _picture.Dispose();
            _lumaPs.Dispose();
            _chromaPs.Dispose();
            _vs.Dispose();
        }
    }
}
