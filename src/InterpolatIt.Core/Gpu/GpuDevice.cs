using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace InterpolatIt.Core.Gpu;

/// <summary>
/// The single Direct3D 11 device shared by capture, analysis, synthesis and presentation.
/// <para>
/// The immediate context is not thread-safe: every multi-call sequence takes <see cref="Sync"/>, and nothing
/// blocks on the GPU while holding it — use <see cref="WaitForGpu"/> (outside the lock) before a read-back.
/// </para>
/// </summary>
public sealed class GpuDevice : IDisposable
{
    private readonly ID3D11DeviceContext4? _context4;
    private readonly ID3D11Fence? _fence;
    private readonly ID3D11Query? _eventQuery;
    private readonly AutoResetEvent _fenceEvent = new(false);
    private ulong _fenceValue;

    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    public AdapterDescription1 Adapter { get; }
    public Lock Sync { get; } = new();

    /// <summary>True when GPU completion is awaited through a D3D11.4 fence (an event query is the fallback).</summary>
    public bool UsesFence => _fence is not null;

    private GpuDevice(ID3D11Device device, ID3D11DeviceContext context, AdapterDescription1 adapter)
    {
        Device = device;
        Context = context;
        Adapter = adapter;

        using (var mt = device.QueryInterfaceOrNull<ID3D11Multithread>())
            mt?.SetMultithreadProtected(true);      // insurance for calls made by WGC on its own threads

        var device5 = device.QueryInterfaceOrNull<ID3D11Device5>();
        _context4 = context.QueryInterfaceOrNull<ID3D11DeviceContext4>();
        if (device5 is not null && _context4 is not null)
        {
            try { _fence = device5.CreateFence(0, FenceFlags.None); }
            catch (SharpGen.Runtime.SharpGenException) { _fence = null; }
        }
        device5?.Dispose();
        if (_fence is null)
            _eventQuery = device.CreateQuery(QueryType.Event, QueryFlags.None);
    }

    /// <summary>Creates the device on the adapter with the given LUID, or on the high-performance adapter.</summary>
    public static GpuDevice Create(long? adapterLuid = null)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory6>();
        IDXGIAdapter1? chosen = null;
        for (uint i = 0; factory.EnumAdapterByGpuPreference(i, GpuPreference.HighPerformance, out IDXGIAdapter1? adapter).Success; i++)
        {
            var desc = adapter!.Description1;
            bool usable = (desc.Flags & AdapterFlags.Software) == 0
                          && (adapterLuid is null || LuidToInt64(desc.Luid) == adapterLuid);
            if (usable && chosen is null) chosen = adapter;
            else adapter.Dispose();
        }
        if (chosen is null)
            throw new InvalidOperationException("No hardware Direct3D adapter found.");

        using (chosen)
        {
            FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0];
            D3D11.D3D11CreateDevice(chosen, DriverType.Unknown, DeviceCreationFlags.BgraSupport, levels,
                out ID3D11Device device, out ID3D11DeviceContext context).CheckError();
            return new GpuDevice(device, context, chosen.Description1);
        }
    }

    public static long LuidToInt64(Vortice.Luid luid) => luid;

    /// <summary>
    /// Asks the GPU scheduler to run this device's work ahead of other work: the few small draws and copies per
    /// refresh must not queue behind our own inference or another application's rendering, or a frame reaches
    /// the compositor one refresh late. (Measured: with the GPU clocked down at light load and a second GPU
    /// client active, a third of the presents made during an inference were shown a refresh late.)
    /// Returns a short description of what was granted.
    /// </summary>
    public string RaiseSchedulingPriority()
    {
        string device = "device priority unchanged";
        using (var dxgi = Device.QueryInterfaceOrNull<IDXGIDevice>())
        {
            if (dxgi is not null && dxgi.SetGPUThreadPriority(7).Success) device = "device priority +7";
        }
        // The process-wide class needs no privilege up to "high"; "realtime" would need SeIncreaseBasePriority.
        int status = Native.Gdi32.D3DKMTSetProcessSchedulingPriorityClass(Native.Kernel32.GetCurrentProcess(),
            Native.Gdi32.D3DKMT_SCHEDULINGPRIORITYCLASS_HIGH);
        return status == 0 ? $"{device}, process class high" : $"{device}, process class unchanged (0x{status:X8})";
    }

    /// <summary>
    /// Flushes and blocks until everything submitted so far has executed on the GPU. Call without holding
    /// <see cref="Sync"/>; other threads can keep submitting while this one waits.
    /// </summary>
    public void WaitForGpu()
    {
        if (_fence is not null)
        {
            ulong value;
            lock (Sync)
            {
                value = ++_fenceValue;
                _context4!.Signal(_fence, value);
                Context.Flush();
            }
            _fence.SetEventOnCompletion(value, _fenceEvent);
            _fenceEvent.WaitOne();
            return;
        }

        lock (Sync)
        {
            Context.End(_eventQuery!);
            Context.Flush();
        }
        var spin = new SpinWait();
        while (true)
        {
            lock (Sync)
            {
                if (Context.GetData(_eventQuery!, AsyncGetDataFlags.DoNotFlush, out int done) && done != 0)
                    return;
            }
            spin.SpinOnce();
        }
    }

    /// <summary>Copies a BGRA8 texture to system memory (diagnostics: it stalls the pipeline for a moment).</summary>
    public unsafe byte[] ReadBack(ID3D11Texture2D texture, int width, int height)
    {
        var desc = new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)width, (uint)height, 1, 1,
            BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read);
        using var staging = Device.CreateTexture2D(desc);
        var pixels = new byte[width * height * 4];
        lock (Sync)
        {
            Context.CopySubresourceRegion(staging, 0, 0, 0, 0, texture, 0, new Vortice.Mathematics.Box(0, 0, 0, width, height, 1));
            var map = Context.Map(staging, 0, MapMode.Read);
            try
            {
                fixed (byte* dst = pixels)
                {
                    for (int y = 0; y < height; y++)
                        Buffer.MemoryCopy((byte*)map.DataPointer + (long)y * map.RowPitch, dst + (long)y * width * 4, width * 4, width * 4);
                }
            }
            finally
            {
                Context.Unmap(staging, 0);
            }
        }
        return pixels;
    }

    public void Dispose()
    {
        _fence?.Dispose();
        _eventQuery?.Dispose();
        _context4?.Dispose();
        _fenceEvent.Dispose();
        Context.ClearState();
        Context.Flush();
        Context.Dispose();
        Device.Dispose();
    }
}
