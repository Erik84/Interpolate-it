using System.Runtime.InteropServices;
using InterpolatIt.Core.Native;

namespace InterpolatIt.Core.Recording;

/// <summary>
/// The sound one application is playing - the process and the processes it started, and nothing else on the
/// system - as 48 kHz 16-bit stereo. This is Windows' "process loopback" capture (Windows 10 2004 and later):
/// a virtual audio device that is activated for a process id.
/// <para>
/// Packets arrive while the application plays something and not otherwise; each carries the time (on the QPC
/// clock) at which its first sample was played. <see cref="AudioTimeline"/> makes an unbroken stream of them.
/// </para>
/// <para>
/// Start it, read it and dispose of it on one thread (or on threads of the multithreaded apartment only): the
/// audio client cannot be reached from a window thread.
/// </para>
/// </summary>
public sealed unsafe class ProcessAudioCapture : IDisposable
{
    public const int SampleRate = 48000, Channels = 2, BytesPerFrame = 4;

    public delegate void PacketHandler(ReadOnlySpan<byte> pcm, int frames, double time, bool silent);

    private const string ProcessLoopbackDevice = @"VAD\Process_Loopback";
    private const uint StreamFlagsLoopback = 0x00020000, StreamFlagsEventCallback = 0x00040000,
        StreamFlagsAutoConvertPcm = 0x80000000, StreamFlagsDefaultQuality = 0x08000000;
    private const uint BufferFlagsSilent = 0x2, BufferFlagsTimestampError = 0x4;

    private static readonly Guid IidAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    private static readonly Guid IidAudioCaptureClient = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        [PreserveSig] int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        [PreserveSig] int GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object? activatedInterface);
    }

    /// <summary>Marks an object as callable from any thread; the activation refuses a handler that is not.</summary>
    [ComImport, Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAgileObject
    {
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, nint format, nint audioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(int shareMode, nint format, out nint closestMatch);
        [PreserveSig] int GetMixFormat(out nint format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(nint handle);
        [PreserveSig] int GetService(in Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object? service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out nint data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct WaveFormat
    {
        public ushort FormatTag, Channels;
        public uint SamplesPerSecond, AverageBytesPerSecond;
        public ushort BlockAlign, BitsPerSample, ExtraSize;
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int ActivateAudioInterfaceAsync([MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath, in Guid iid,
        nint activationParams, IActivateAudioInterfaceCompletionHandler completionHandler, out IActivateAudioInterfaceAsyncOperation? operation);

    private sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        private readonly ManualResetEventSlim _done = new(false);
        private int _result = unchecked((int)0x80004005);
        private object? _client;

        public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
        {
            int hr = operation.GetActivateResult(out _result, out _client);
            if (hr < 0) _result = hr;
            _done.Set();
            return 0;
        }

        public (int Result, object? Client) Wait(int milliseconds)
            => _done.Wait(milliseconds) ? (_result, _client) : (unchecked((int)0x800705B4), null);      // or: timed out
    }

    private readonly IAudioClient _client;
    private readonly IAudioCaptureClient _capture;
    private readonly AutoResetEvent _dataAvailable = new(false);
    private bool _disposed;

    /// <summary>Signalled when there may be packets to read.</summary>
    public WaitHandle DataAvailable => _dataAvailable;

    private ProcessAudioCapture(IAudioClient client, IAudioCaptureClient capture)
    {
        _client = client;
        _capture = capture;
    }

    /// <summary>
    /// Starts capturing what the process plays. Throws <see cref="InvalidOperationException"/> with a reason a
    /// user can read when Windows will not do it.
    /// </summary>
    public static ProcessAudioCapture Start(uint processId)
    {
        // AUDIOCLIENT_ACTIVATION_PARAMS { type = process loopback, process id, mode = include the process tree },
        // handed over as a blob in a PROPVARIANT (vt at 0, size at 8, pointer at 16).
        uint* parameters = stackalloc uint[3] { 1, processId, 0 };
        byte* variant = stackalloc byte[24];
        new Span<byte>(variant, 24).Clear();
        *(ushort*)variant = 65;                     // VT_BLOB
        *(uint*)(variant + 8) = 3 * sizeof(uint);
        *(nint*)(variant + 16) = (nint)parameters;

        var handler = new ActivationHandler();
        int hr = ActivateAudioInterfaceAsync(ProcessLoopbackDevice, in IidAudioClient, (nint)variant, handler, out var operation);
        if (hr < 0) throw Refused("start capturing the application's sound", hr);
        var (result, activated) = handler.Wait(5000);
        if (operation is not null) Marshal.ReleaseComObject(operation);
        if (result < 0 || activated is not IAudioClient client) throw Refused("capture the application's sound", result);

        // The virtual device has no format of its own: it converts to the one asked for.
        var format = new WaveFormat
        {
            FormatTag = 1,                          // PCM
            Channels = Channels,
            SamplesPerSecond = SampleRate,
            AverageBytesPerSecond = SampleRate * BytesPerFrame,
            BlockAlign = BytesPerFrame,
            BitsPerSample = 16,
        };
        hr = client.Initialize(0, StreamFlagsLoopback | StreamFlagsEventCallback | StreamFlagsAutoConvertPcm | StreamFlagsDefaultQuality,
            2_000_000, 0, (nint)(&format), 0);      // shared mode, a buffer of 200 ms
        if (hr < 0) throw Refused("set up the capture of the application's sound", hr);
        hr = client.GetService(in IidAudioCaptureClient, out object? service);
        if (hr < 0 || service is not IAudioCaptureClient capture) throw Refused("read the application's sound", hr);

        var result2 = new ProcessAudioCapture(client, capture);
        hr = client.SetEventHandle(result2._dataAvailable.SafeWaitHandle.DangerousGetHandle());
        if (hr >= 0) hr = client.Start();
        if (hr < 0)
        {
            result2.Dispose();
            throw Refused("start the capture of the application's sound", hr);
        }
        return result2;
    }

    private static InvalidOperationException Refused(string what, int hr)
        => new($"Windows did not {what} (0x{hr:X8})");

    /// <summary>Hands every packet that is waiting to <paramref name="handler"/>, oldest first.</summary>
    public void Drain(PacketHandler handler)
    {
        while (!_disposed)
        {
            if (_capture.GetNextPacketSize(out uint size) < 0 || size == 0) return;
            if (_capture.GetBuffer(out nint data, out uint frames, out uint flags, out _, out ulong qpc) < 0) return;
            try
            {
                // The time of the first sample, in 100 ns units of the QPC clock; when the device could not tell,
                // the packet is taken to have ended just now.
                double time = (flags & BufferFlagsTimestampError) != 0 || qpc == 0
                    ? Qpc.Now - frames / (double)SampleRate
                    : qpc * 1e-7;
                handler(new ReadOnlySpan<byte>((void*)data, (int)frames * BytesPerFrame), (int)frames, time, (flags & BufferFlagsSilent) != 0);
            }
            finally
            {
                _capture.ReleaseBuffer(frames);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _client.Stop();
        Marshal.ReleaseComObject(_capture);
        Marshal.ReleaseComObject(_client);
        _dataAvailable.Dispose();
    }
}

/// <summary>
/// Makes the unbroken stream an encoder wants out of packets that only come while something is playing:
/// silence where nothing was played, every sample at the place in the recording its time gives it.
/// Positions are in frames (one sample for each channel) since the start of the recording.
/// </summary>
public sealed class AudioTimeline
{
    public const int Rate = ProcessAudioCapture.SampleRate, FrameBytes = ProcessAudioCapture.BytesPerFrame;

    /// <summary>Frames per piece handed to the encoder: 20 ms.</summary>
    public const int ChunkFrames = Rate / 50;

    private const int Capacity = Rate * 4;

    /// <summary>A packet that starts within this distance of where the stream stands is simply appended: the two
    /// clocks differ by a few milliseconds over a minute, and a correction on every packet would be a click.</summary>
    private const int Tolerance = Rate * 40 / 1000;

    private readonly Lock _lock = new();
    private readonly byte[] _ring = new byte[Capacity * FrameBytes];
    private long _written;          // frames before this position are settled: sound or silence
    private long _next;             // the next frame to hand out

    /// <summary>The position up to which the stream has been handed out.</summary>
    public long Position { get { lock (_lock) return _next; } }

    /// <summary>Sound that had to be thrown away because nobody took it in time (frames).</summary>
    public long Lost { get; private set; }

    /// <summary>Puts a packet at the place its time (seconds since the start of the recording) gives it.</summary>
    public void Write(ReadOnlySpan<byte> pcm, int frames, double time, bool silent)
    {
        lock (_lock)
        {
            long target = (long)Math.Round(time * Rate);
            if (target + frames <= _next) return;           // from before the recording began, or too late to be used
            long skip = 0;
            long gap = target - _written;
            if (gap > Tolerance) FillSilence(target);       // nothing was played in between
            else if (gap < -Tolerance) skip = -gap;         // overlaps what is settled already: keep what is new
            if (skip >= frames) return;
            Append(pcm[(int)(skip * FrameBytes)..], frames - (int)skip, silent);
        }
    }

    /// <summary>
    /// The next piece of <see cref="ChunkFrames"/> frames, once the recording's clock has passed its end
    /// (<paramref name="upTo"/>, seconds): what has not arrived by then was silence.
    /// </summary>
    public bool TryRead(double upTo, byte[] chunk, out long position)
    {
        lock (_lock)
        {
            position = _next;
            long end = _next + ChunkFrames;
            if (end > (long)Math.Floor(upTo * Rate)) return false;
            if (_written < end) FillSilence(end);
            Copy(_next, chunk, ChunkFrames);
            _next = end;
            return true;
        }
    }

    private void FillSilence(long to)
    {
        if (to <= _written) return;
        long count = Math.Min(to - _written, Capacity);
        long from = to - count;
        for (long done = 0; done < count;)
        {
            int index = (int)((from + done) % Capacity);
            int run = (int)Math.Min(count - done, Capacity - index);
            Array.Clear(_ring, index * FrameBytes, run * FrameBytes);
            done += run;
        }
        _written = to;
        MakeRoom();
    }

    private void Append(ReadOnlySpan<byte> pcm, int frames, bool silent)
    {
        for (int done = 0; done < frames;)
        {
            int index = (int)((_written + done) % Capacity);
            int run = Math.Min(frames - done, Capacity - index);
            var destination = _ring.AsSpan(index * FrameBytes, run * FrameBytes);
            if (silent) destination.Clear();
            else pcm.Slice(done * FrameBytes, run * FrameBytes).CopyTo(destination);
            done += run;
        }
        _written += frames;
        MakeRoom();
    }

    /// <summary>The ring holds four seconds. When the encoder is further behind than that, the oldest goes.</summary>
    private void MakeRoom()
    {
        if (_written - _next <= Capacity) return;
        long dropped = _written - Capacity - _next;
        Lost += dropped;
        _next += dropped - dropped % ChunkFrames + (dropped % ChunkFrames == 0 ? 0 : ChunkFrames);
    }

    private void Copy(long from, byte[] destination, int frames)
    {
        for (int done = 0; done < frames;)
        {
            int index = (int)((from + done) % Capacity);
            int run = Math.Min(frames - done, Capacity - index);
            Buffer.BlockCopy(_ring, index * FrameBytes, destination, done * FrameBytes, run * FrameBytes);
            done += run;
        }
    }
}
