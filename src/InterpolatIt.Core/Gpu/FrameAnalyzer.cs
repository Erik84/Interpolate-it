using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace InterpolatIt.Core.Gpu;

/// <summary>Change statistics of a captured frame against the previous one, at inference resolution.</summary>
public readonly record struct FrameStats(
    bool HasPrevious,
    float MaxTileDiff,          // largest per-tile max |difference| (0..1); 0 means an exact duplicate
    int ChangedTiles,           // tiles whose max difference exceeds the duplicate threshold
    int StrongTiles,            // tiles whose max difference is clearly more than re-compression noise
    int TileCount,
    float Ssim)                 // global structural similarity of the tile-luma images (1 = identical structure)
{
    public float ChangedFraction => TileCount == 0 ? 0f : (float)ChangedTiles / TileCount;

    /// <summary>Share of the picture in which something really moved or changed (see
    /// <see cref="FrameAnalyzer.StrongThreshold"/>).</summary>
    public float StrongFraction => TileCount == 0 ? 0f : (float)StrongTiles / TileCount;
}

/// <summary>
/// Downscales a captured frame to the inference resolution as a planar fp16 RGB tensor and measures how much it
/// changed, in one compute pass and one read-back (see Shaders/DownscaleAnalyze.hlsl for the texture layout).
/// </summary>
public sealed unsafe class FrameAnalyzer : IDisposable
{
    /// <summary>A tile counts as changed above this difference: one 8-bit level after averaging is ~0.004.</summary>
    public const float DuplicateThreshold = 1.5f / 255f;

    /// <summary>A tile changed "strongly" above this difference. A video stream that sends the same picture twice
    /// does not decode to identical pixels (the second copy refines the first by a few levels); real motion
    /// moves edges and changes pixels by far more.</summary>
    public const float StrongThreshold = 12f / 255f;

    private const int Tile = 16;

    [StructLayout(LayoutKind.Sequential)]
    private struct Params
    {
        public Vector2 SrcTexSize;
        public Vector2 SrcContentSize;
        public uint DstW, DstH;
        public uint TapsX, TapsY;
        public uint HasPrev, Pad0, Pad1, Pad2;
    }

    private readonly GpuDevice _gpu;
    private readonly ID3D11ComputeShader _shader;
    private readonly ID3D11SamplerState _linearClamp;
    private readonly ID3D11Buffer _constants;

    private readonly ID3D11Texture2D?[] _planes = new ID3D11Texture2D?[2];
    private readonly ID3D11ShaderResourceView?[] _planeSrv = new ID3D11ShaderResourceView?[2];
    private readonly ID3D11UnorderedAccessView?[] _planeUav = new ID3D11UnorderedAccessView?[2];
    private ID3D11Texture2D? _staging;
    private float[] _lumaCur = [], _lumaPrev = [];
    private int _current;
    private bool _hasPrevious;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public int TilesX => Width / Tile;
    public int TilesY => Height / Tile;

    /// <summary>Bytes of one planar RGB fp16 tensor at the configured size.</summary>
    public long TensorBytes => 3L * Width * Height * sizeof(ushort);

    public FrameAnalyzer(GpuDevice gpu)
    {
        _gpu = gpu;
        _shader = gpu.Device.CreateComputeShader(ShaderLibrary.Compile("DownscaleAnalyze.hlsl", "main", "cs_5_0"));
        _linearClamp = gpu.Device.CreateSamplerState(SamplerDescription.LinearClamp);
        _constants = gpu.Device.CreateBuffer((uint)sizeof(Params), BindFlags.ConstantBuffer);
    }

    /// <summary>(Re)allocates for an inference size; both dimensions must be multiples of 16.</summary>
    public void Configure(int width, int height)
    {
        if (width % Tile != 0 || height % Tile != 0)
            throw new ArgumentException($"inference size {width}x{height} must be a multiple of {Tile}");
        if (width == Width && height == Height) return;
        ReleaseTextures();
        Width = width;
        Height = height;

        var desc = new Texture2DDescription(Format.R16_Float, (uint)width, (uint)(3 * height + TilesY), 1, 1,
            BindFlags.ShaderResource | BindFlags.UnorderedAccess);
        for (int i = 0; i < 2; i++)
        {
            _planes[i] = _gpu.Device.CreateTexture2D(desc);
            _planeSrv[i] = _gpu.Device.CreateShaderResourceView(_planes[i]!);
            _planeUav[i] = _gpu.Device.CreateUnorderedAccessView(_planes[i]!);
        }
        var stagingDesc = desc;
        stagingDesc.BindFlags = BindFlags.None;
        stagingDesc.Usage = ResourceUsage.Staging;
        stagingDesc.CPUAccessFlags = CpuAccessFlags.Read;
        _staging = _gpu.Device.CreateTexture2D(stagingDesc);

        _lumaCur = new float[TilesX * TilesY];
        _lumaPrev = new float[TilesX * TilesY];
        _hasPrevious = false;
    }

    /// <summary>Forget the previous frame (after a pause, a seek or a source change).</summary>
    public void Reset() => _hasPrevious = false;

    /// <summary>
    /// Runs the pass on <paramref name="source"/> and blocks until the result is read back.
    /// <paramref name="tensor"/> receives 3 x Height x Width fp16 values (planar RGB in [0,1]).
    /// </summary>
    public FrameStats Analyze(ID3D11ShaderResourceView source, int textureWidth, int textureHeight,
        int contentWidth, int contentHeight, nint tensor)
    {
        if (_staging is null) throw new InvalidOperationException("Configure must be called first.");
        int cur = _current, prev = 1 - cur;
        bool hasPrev = _hasPrevious;
        var ctx = _gpu.Context;

        var p = new Params
        {
            SrcTexSize = new Vector2(textureWidth, textureHeight),
            SrcContentSize = new Vector2(contentWidth, contentHeight),
            DstW = (uint)Width,
            DstH = (uint)Height,
            TapsX = TapCount(contentWidth, Width),
            TapsY = TapCount(contentHeight, Height),
            HasPrev = hasPrev ? 1u : 0u,
        };

        using (_gpu.UseContext())
        {
            ctx.UpdateSubresource(in p, _constants);
            ctx.CSSetShader(_shader);
            ctx.CSSetConstantBuffer(0, _constants);
            ctx.CSSetSampler(0, _linearClamp);
            ctx.CSSetShaderResource(0, source);
            ctx.CSSetShaderResource(1, _planeSrv[prev]!);
            ctx.CSSetUnorderedAccessView(0, _planeUav[cur]!);
            ctx.Dispatch((uint)TilesX, (uint)TilesY, 1);
            ctx.CSUnsetUnorderedAccessView(0);
            ctx.CSUnsetShaderResources(0, 2);
            ctx.CopyResource(_staging, _planes[cur]!);
        }
        _gpu.WaitForGpu();      // so the Map below returns at once and the lock is never held across a stall

        FrameStats stats;
        using (_gpu.UseContext())
        {
            var map = ctx.Map(_staging, 0, MapMode.Read);
            try
            {
                CopyPlanes(map, tensor);
                stats = ReadStats(map, hasPrev);
            }
            finally
            {
                ctx.Unmap(_staging, 0);
            }
        }

        _current = prev;
        _hasPrevious = true;
        return stats;
    }

    private static uint TapCount(int source, int destination)
        => (uint)Math.Clamp((int)Math.Ceiling(source / (2.0 * destination) - 1e-6), 1, 8);

    private void CopyPlanes(MappedSubresource map, nint tensor)
    {
        int rowBytes = Width * sizeof(ushort);
        int rows = 3 * Height;
        byte* src = (byte*)map.DataPointer;
        byte* dst = (byte*)tensor;
        if (map.RowPitch == rowBytes)
        {
            Buffer.MemoryCopy(src, dst, (long)rows * rowBytes, (long)rows * rowBytes);
            return;
        }
        for (int y = 0; y < rows; y++)
            Buffer.MemoryCopy(src + (long)y * map.RowPitch, dst + (long)y * rowBytes, rowBytes, rowBytes);
    }

    private FrameStats ReadStats(MappedSubresource map, bool hasPrev)
    {
        int tilesX = TilesX, tilesY = TilesY;
        (_lumaCur, _lumaPrev) = (_lumaPrev, _lumaCur);
        float maxDiff = 0f;
        int changed = 0, strong = 0;
        byte* statsBase = (byte*)map.DataPointer + (long)(3 * Height) * map.RowPitch;
        for (int ty = 0; ty < tilesY; ty++)
        {
            ushort* row = (ushort*)(statsBase + (long)ty * map.RowPitch);
            for (int tx = 0; tx < tilesX; tx++)
            {
                float diff = (float)BitConverter.UInt16BitsToHalf(row[tx]);
                if (diff > maxDiff) maxDiff = diff;
                if (diff > DuplicateThreshold) changed++;
                if (diff > StrongThreshold) strong++;
                _lumaCur[ty * tilesX + tx] = (float)BitConverter.UInt16BitsToHalf(row[tilesX + tx]);
            }
        }
        float ssim = hasPrev ? GlobalSsim(_lumaCur, _lumaPrev) : 1f;
        int tiles = tilesX * tilesY;
        return new FrameStats(hasPrev, hasPrev ? maxDiff : 1f, hasPrev ? changed : tiles, hasPrev ? strong : tiles, tiles, ssim);
    }

    /// <summary>Structural similarity of two tile-luma images taken as a whole (means, variances, covariance).</summary>
    internal static float GlobalSsim(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        int n = Math.Min(a.Length, b.Length);
        if (n == 0) return 1f;
        double ma = 0, mb = 0;
        for (int i = 0; i < n; i++) { ma += a[i]; mb += b[i]; }
        ma /= n; mb /= n;
        double va = 0, vb = 0, cov = 0;
        for (int i = 0; i < n; i++)
        {
            double da = a[i] - ma, db = b[i] - mb;
            va += da * da; vb += db * db; cov += da * db;
        }
        va /= n; vb /= n; cov /= n;
        const double c1 = 0.01 * 0.01, c2 = 0.03 * 0.03;
        return (float)(((2 * ma * mb + c1) * (2 * cov + c2)) / ((ma * ma + mb * mb + c1) * (va + vb + c2)));
    }

    private void ReleaseTextures()
    {
        for (int i = 0; i < 2; i++)
        {
            _planeUav[i]?.Dispose(); _planeUav[i] = null;
            _planeSrv[i]?.Dispose(); _planeSrv[i] = null;
            _planes[i]?.Dispose(); _planes[i] = null;
        }
        _staging?.Dispose();
        _staging = null;
    }

    public void Dispose()
    {
        ReleaseTextures();
        _constants.Dispose();
        _linearClamp.Dispose();
        _shader.Dispose();
    }
}
