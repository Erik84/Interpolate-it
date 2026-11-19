using InterpolatIt.Core.Gpu;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace InterpolatIt.Core.Presentation;

/// <summary>
/// A flip-model swap chain on a window. Flip model is what makes low-latency windowed presentation, frame
/// statistics and (when the window qualifies) direct flip possible.
/// </summary>
public sealed class WindowSwapChain : IDisposable
{
    private readonly GpuDevice _gpu;
    private readonly SwapChainFlags _flags;
    private ID3D11Texture2D? _backBuffer;
    private ID3D11RenderTargetView? _target;

    public IDXGISwapChain1 SwapChain { get; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public bool TearingSupported { get; }

    public WindowSwapChain(GpuDevice gpu, nint hwnd, int width, int height, bool waitable = false)
    {
        _gpu = gpu;
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);

        using var dxgiDevice = gpu.Device.QueryInterface<IDXGIDevice1>();
        using var adapter = dxgiDevice.GetAdapter();
        using var factory = adapter.GetParent<IDXGIFactory2>();
        using (var factory5 = factory.QueryInterfaceOrNull<IDXGIFactory5>())
            TearingSupported = factory5 is not null && factory5.PresentAllowTearing;

        _flags = (waitable ? SwapChainFlags.FrameLatencyWaitableObject : SwapChainFlags.None)
                 | (TearingSupported ? SwapChainFlags.AllowTearing : SwapChainFlags.None);
        var desc = new SwapChainDescription1
        {
            Width = (uint)Width,
            Height = (uint)Height,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipDiscard,
            AlphaMode = AlphaMode.Ignore,
            Flags = _flags,
        };
        SwapChain = factory.CreateSwapChainForHwnd(gpu.Device, hwnd, desc);
        factory.MakeWindowAssociation(hwnd, WindowAssociationFlags.IgnoreAll);     // no Alt+Enter handling by DXGI
        dxgiDevice.MaximumFrameLatency = 1;
        if (waitable)
        {
            using var sc2 = SwapChain.QueryInterfaceOrNull<IDXGISwapChain2>();
            if (sc2 is not null) sc2.MaximumFrameLatency = 1;
        }
    }

    /// <summary>The current back buffer (valid until the next Present or Resize). Use under the GPU lock.</summary>
    public ID3D11Texture2D BackBuffer => _backBuffer ??= SwapChain.GetBuffer<ID3D11Texture2D>(0);

    public ID3D11RenderTargetView BackBufferTarget => _target ??= _gpu.Device.CreateRenderTargetView(BackBuffer);

    public void Resize(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (width == Width && height == Height) return;
        lock (_gpu.Sync)
        {
            ReleaseBackBuffer();
            _gpu.Context.ClearState();
            _gpu.Context.Flush();
            SwapChain.ResizeBuffers(0, (uint)width, (uint)height, Format.Unknown, _flags).CheckError();
            Width = width;
            Height = height;
        }
    }

    /// <summary>Presents the back buffer. Call under the GPU lock; the back-buffer views stay valid in D3D11.</summary>
    public SharpGen.Runtime.Result Present(uint syncInterval)
        => SwapChain.Present(syncInterval, PresentFlags.None);

    public bool TryGetFrameStatistics(out FrameStatistics stats) => SwapChain.GetFrameStatistics(out stats).Success;

    public uint LastPresentCount => SwapChain.LastPresentCount;

    private void ReleaseBackBuffer()
    {
        _target?.Dispose();
        _target = null;
        _backBuffer?.Dispose();
        _backBuffer = null;
    }

    public void Dispose()
    {
        ReleaseBackBuffer();
        SwapChain.Dispose();
    }
}
