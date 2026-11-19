using System.Runtime.InteropServices;
using InterpolatIt.Core.Gpu;
using InterpolatIt.Core.Native;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace InterpolatIt.Core.Capture;

/// <summary>
/// One frame handed out by the capture pool. The texture belongs to the pool: copy what you need and dispose the
/// frame at once, or the pool runs out of buffers and capture stalls.
/// </summary>
public sealed class CapturedFrame : IDisposable
{
    private Direct3D11CaptureFrame? _frame;

    public ID3D11Texture2D Texture { get; }

    /// <summary>Size of the valid picture; the texture can be larger while the pool has not been resized yet.</summary>
    public int ContentWidth { get; }
    public int ContentHeight { get; }

    /// <summary>Time at which the compositor rendered this frame, in seconds on the QPC clock.</summary>
    public double Time { get; }

    /// <summary>Dirty rectangles reported by the system (-1 when the feature is unavailable).</summary>
    public int DirtyRegionCount { get; }
    public long DirtyArea { get; }

    internal CapturedFrame(Direct3D11CaptureFrame frame, ID3D11Texture2D texture, int dirtyCount, long dirtyArea)
    {
        _frame = frame;
        Texture = texture;
        ContentWidth = frame.ContentSize.Width;
        ContentHeight = frame.ContentSize.Height;
        Time = Qpc.FromTimeSpan(frame.SystemRelativeTime);
        DirtyRegionCount = dirtyCount;
        DirtyArea = dirtyArea;
    }

    public void Dispose()
    {
        Texture.Dispose();
        _frame?.Dispose();
        _frame = null;
    }
}

/// <summary>
/// Captures one window with Windows.Graphics.Capture: it reads the window's own DWM visual, so it works for any
/// renderer and keeps working when the window is covered by our overlay.
/// </summary>
public sealed class WgcCapture : IDisposable
{
    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        nint CreateForWindow(nint window, in Guid iid);
        nint CreateForMonitor(nint monitor, in Guid iid);
    }

    [ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        nint GetInterface(in Guid iid);
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

    private static readonly Guid IidGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid IidTexture2D = typeof(ID3D11Texture2D).GUID;

    private const int PoolBuffers = 4;

    private readonly GpuDevice _gpu;
    private readonly IDirect3DDevice _winrtDevice;
    private readonly GraphicsCaptureItem _item;                 // fields keep the WinRT objects alive; if the GC
    private readonly Direct3D11CaptureFramePool _pool;          // finalises them, capture stops without an error
    private readonly GraphicsCaptureSession _session;
    private readonly AutoResetEvent _frameSignal = new(false);
    private SizeInt32 _poolSize;
    private bool _dirtyRegions;
    private long _arrived;
    private volatile bool _closed;

    public nint Window { get; }

    /// <summary>Signalled whenever the pool has a new frame; wait on it, then drain with <see cref="TryAcquire"/>.</summary>
    public WaitHandle FrameSignal => _frameSignal;

    /// <summary>The captured window was closed.</summary>
    public bool IsClosed => _closed;

    public long FramesArrived => Interlocked.Read(ref _arrived);
    public bool BorderRemoved { get; }
    public bool UpdateIntervalLifted { get; }
    public bool DirtyRegionsAvailable => _dirtyRegions;

    public WgcCapture(GpuDevice gpu, nint hwnd)
    {
        if (!GraphicsCaptureSession.IsSupported())
            throw new NotSupportedException("Windows.Graphics.Capture is not supported on this system.");
        _gpu = gpu;
        Window = hwnd;

        using (var dxgiDevice = gpu.Device.QueryInterface<IDXGIDevice>())
        {
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out nint inspectable));
            try { _winrtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(inspectable); }
            finally { Marshal.Release(inspectable); }
        }

        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        nint itemPointer = interop.CreateForWindow(hwnd, in IidGraphicsCaptureItem);
        try { _item = GraphicsCaptureItem.FromAbi(itemPointer); }
        finally { Marshal.Release(itemPointer); }
        _item.Closed += (_, _) => { _closed = true; _frameSignal.Set(); };

        _poolSize = _item.Size;
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, PoolBuffers, _poolSize);
        _pool.FrameArrived += (_, _) =>
        {
            Interlocked.Increment(ref _arrived);
            _frameSignal.Set();
        };

        _session = _pool.CreateCaptureSession(_item);
        _session.IsCursorCaptureEnabled = false;        // the real cursor stays crisp above the click-through overlay
        BorderRemoved = Try(() =>
        {
            // Unpackaged apps are granted this without a prompt; the call just has to have happened.
            GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless).AsTask().Wait(2000);
            _session.IsBorderRequired = false;
        });
        // Since Windows 11 24H2 the default update interval caps capture near 60 fps; 1 ms lifts the cap.
        UpdateIntervalLifted = Try(() => _session.MinUpdateInterval = TimeSpan.FromMilliseconds(1));
        _dirtyRegions = Try(() => _session.DirtyRegionMode = GraphicsCaptureDirtyRegionMode.ReportOnly);
        // IncludeSecondaryWindows stays false: the overlay is an owned popup of the target and would be captured.
        _session.StartCapture();
    }

    private static bool Try(Action action)
    {
        try { action(); return true; }
        catch (Exception e) when (e is COMException or InvalidCastException or NotSupportedException
                                      or UnauthorizedAccessException or AggregateException or MissingMethodException)
        {
            return false;
        }
    }

    /// <summary>
    /// Takes the next frame from the pool, or returns false when it is empty. Recreates the pool when the window
    /// has changed size (the frame that reports the new size is still returned; the next one has the new size).
    /// </summary>
    public bool TryAcquire(out CapturedFrame? captured)
    {
        captured = null;
        if (_closed) return false;
        Direct3D11CaptureFrame? frame;
        try { frame = _pool.TryGetNextFrame(); }
        catch (Exception e) when (e is COMException or ObjectDisposedException) { return false; }
        if (frame is null) return false;

        var size = frame.ContentSize;
        if (size.Width > 0 && size.Height > 0 && (size.Width != _poolSize.Width || size.Height != _poolSize.Height))
        {
            _poolSize = size;
            _pool.Recreate(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, PoolBuffers, size);
        }

        int dirtyCount = -1;
        long dirtyArea = 0;
        if (_dirtyRegions)
        {
            try
            {
                var regions = frame.DirtyRegions;
                dirtyCount = regions.Count;
                foreach (var r in regions) dirtyArea += (long)r.Width * r.Height;
            }
            catch (Exception e) when (e is COMException or InvalidCastException or NotSupportedException or MissingMethodException)
            {
                _dirtyRegions = false;
            }
        }

        var access = frame.Surface.As<IDirect3DDxgiInterfaceAccess>();
        nint texturePointer = access.GetInterface(in IidTexture2D);
        captured = new CapturedFrame(frame, new ID3D11Texture2D(texturePointer), dirtyCount, dirtyArea);
        return true;
    }

    public void Dispose()
    {
        _closed = true;
        try { _session.Dispose(); } catch (COMException) { }
        try { _pool.Dispose(); } catch (COMException) { }
        _winrtDevice.Dispose();
        _frameSignal.Set();
        _frameSignal.Dispose();
    }
}
