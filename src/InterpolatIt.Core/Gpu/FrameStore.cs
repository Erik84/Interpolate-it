using InterpolatIt.Core.Capture;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace InterpolatIt.Core.Gpu;

/// <summary>A frame-sized BGRA8 texture that can be sampled and rendered to.</summary>
public sealed class GpuFrame : IDisposable
{
    public ID3D11Texture2D Texture { get; }
    public ID3D11ShaderResourceView View { get; }
    public ID3D11RenderTargetView Target { get; }
    public int Width { get; }
    public int Height { get; }

    /// <summary>Identity of the content currently held (a source frame id, or -1).</summary>
    public long Id { get; set; } = -1;

    /// <summary>Capture time of a source frame, seconds on the QPC clock.</summary>
    public double Time { get; set; }

    /// <summary>The content is no longer what was captured: the driver's clean-up has been applied to it.</summary>
    public bool Enhanced { get; set; }

    public GpuFrame(GpuDevice gpu, int width, int height)
    {
        Width = width;
        Height = height;
        var desc = new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)width, (uint)height, 1, 1,
            BindFlags.ShaderResource | BindFlags.RenderTarget);
        Texture = gpu.Device.CreateTexture2D(in desc);
        View = gpu.Device.CreateShaderResourceView(Texture);
        Target = gpu.Device.CreateRenderTargetView(Texture);
    }

    public void Dispose()
    {
        Target.Dispose();
        View.Dispose();
        Texture.Dispose();
    }
}

/// <summary>
/// Our own copies of the captured (real) frames and a pool of targets for generated frames, all at window
/// resolution. Captured textures belong to the capture pool and must be released at once, so every accepted
/// frame is copied here (a GPU-side copy) and addressed by its id afterwards.
/// </summary>
public sealed class FrameStore : IDisposable
{
    private readonly GpuDevice _gpu;
    private readonly int _realCapacity, _generatedCapacity;
    private GpuFrame[] _real = [], _generated = [];
    private int _nextReal, _nextGenerated;

    public int Width { get; private set; }
    public int Height { get; private set; }

    public FrameStore(GpuDevice gpu, int realCapacity = 6, int generatedCapacity = 12)
    {
        _gpu = gpu;
        _realCapacity = realCapacity;
        _generatedCapacity = generatedCapacity;
    }

    /// <summary>(Re)allocates all textures for a window size. Existing content is lost.</summary>
    public void Configure(int width, int height)
    {
        if (width == Width && height == Height && _real.Length > 0) return;
        Release();
        Width = width;
        Height = height;
        _real = new GpuFrame[_realCapacity];
        for (int i = 0; i < _real.Length; i++) _real[i] = new GpuFrame(_gpu, width, height);
        _generated = new GpuFrame[_generatedCapacity];
        for (int i = 0; i < _generated.Length; i++) _generated[i] = new GpuFrame(_gpu, width, height);
        _nextReal = _nextGenerated = 0;
    }

    /// <summary>Copies a captured frame into the next slot of the ring (overwriting the oldest real frame).</summary>
    public GpuFrame StoreReal(CapturedFrame captured, long id)
    {
        var slot = _real[_nextReal];
        _nextReal = (_nextReal + 1) % _real.Length;
        int w = Math.Min(captured.ContentWidth, Width), h = Math.Min(captured.ContentHeight, Height);
        lock (_gpu.Sync)
        {
            _gpu.Context.CopySubresourceRegion(slot.Texture, 0, 0, 0, 0, captured.Texture, 0,
                new Vortice.Mathematics.Box(0, 0, 0, w, h, 1));
        }
        slot.Id = id;
        slot.Time = captured.Time;
        slot.Enhanced = false;
        return slot;
    }

    /// <summary>The stored real frame with this id, or null when it has been overwritten already.</summary>
    public GpuFrame? FindReal(long id)
    {
        foreach (var f in _real)
            if (f.Id == id) return f;
        return null;
    }

    /// <summary>Next render target for a generated frame (round robin; sized so that queued frames survive).</summary>
    public GpuFrame NextGenerated()
    {
        var slot = _generated[_nextGenerated];
        _nextGenerated = (_nextGenerated + 1) % _generated.Length;
        return slot;
    }

    private void Release()
    {
        foreach (var f in _real) f.Dispose();
        foreach (var f in _generated) f.Dispose();
        _real = [];
        _generated = [];
    }

    public void Dispose() => Release();
}
