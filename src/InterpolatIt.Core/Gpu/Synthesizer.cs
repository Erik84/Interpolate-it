using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace InterpolatIt.Core.Gpu;

/// <summary>
/// Composes an in-between frame at full window resolution from the two real frames and the network's flow +
/// mask logit (see Shaders/Synthesize.hlsl). The field is uploaded as five R16F slices, i.e. the planar NCHW
/// fp16 output tensors go to the GPU without any per-pixel CPU work.
/// <para>
/// Pixels that did not change between the two real frames are copied instead of warped once
/// <see cref="BuildPairMask"/> has been called for the pair (see Shaders/PairMask.hlsl); without it the result
/// is exactly RIFE's own composition.
/// </para>
/// </summary>
public sealed unsafe class Synthesizer : IDisposable
{
    public const int FieldSlices = 5;       // flow t->0 xy, flow t->1 xy, mask logit

    /// <summary>Per-channel difference up to which a pixel counts as unchanged between two captured frames.</summary>
    public const float UnchangedThreshold = 1.5f / 255f;

    /// <summary>Flow (in output pixels) below which an unchanged pixel is copied without looking any further.</summary>
    public const float SnapFlowPixels = 1.5f;

    [StructLayout(LayoutKind.Sequential)]
    private struct Params
    {
        public Vector2 InvFieldSize;
        public Vector2 UvScale;
        public Vector2 UvMax;
        public Vector2 FlowToPixels;
        public float SnapFlowSquared;
        public Vector3 Pad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MaskParams
    {
        public int MaxX, MaxY;
        public float Threshold;
        public float Pad;
    }

    private readonly GpuDevice _gpu;
    private readonly ID3D11VertexShader _vs;
    private readonly ID3D11PixelShader _ps, _maskPs;
    private readonly ID3D11SamplerState _linearClamp;
    private readonly ID3D11Buffer _constants, _maskConstants;
    private readonly ID3D11Texture2D _everythingChanged;
    private readonly ID3D11ShaderResourceView _everythingChangedSrv;
    private ID3D11Texture2D? _field, _mask;
    private ID3D11ShaderResourceView? _fieldSrv, _maskSrv;
    private ID3D11RenderTargetView? _maskRtv;
    private int _maskWidth, _maskHeight;
    private bool _maskValid;

    public int FieldWidth { get; private set; }
    public int FieldHeight { get; private set; }

    public Synthesizer(GpuDevice gpu)
    {
        _gpu = gpu;
        _vs = gpu.Device.CreateVertexShader(ShaderLibrary.Compile("Fullscreen.hlsl", "main", "vs_5_0"));
        _ps = gpu.Device.CreatePixelShader(ShaderLibrary.Compile("Synthesize.hlsl", "main", "ps_5_0"));
        _maskPs = gpu.Device.CreatePixelShader(ShaderLibrary.Compile("PairMask.hlsl", "main", "ps_5_0"));
        _linearClamp = gpu.Device.CreateSamplerState(SamplerDescription.LinearClamp);
        _constants = gpu.Device.CreateBuffer((uint)sizeof(Params), BindFlags.ConstantBuffer);
        _maskConstants = gpu.Device.CreateBuffer((uint)sizeof(MaskParams), BindFlags.ConstantBuffer);

        // Stands in for the pair mask when none has been built: every pixel "changed", so nothing is snapped.
        _everythingChanged = gpu.Device.CreateTexture2D(new Texture2DDescription(Format.R8_UNorm, 1, 1, 1, 1, BindFlags.ShaderResource));
        _everythingChangedSrv = gpu.Device.CreateShaderResourceView(_everythingChanged);
        byte one = 255;
        using (gpu.UseContext()) gpu.Context.UpdateSubresource(_everythingChanged, 0, null, (nint)(&one), 1, 1);
    }

    /// <summary>(Re)allocates the field texture for an inference size.</summary>
    public void Configure(int fieldWidth, int fieldHeight)
    {
        if (fieldWidth == FieldWidth && fieldHeight == FieldHeight) return;
        _fieldSrv?.Dispose();
        _field?.Dispose();
        FieldWidth = fieldWidth;
        FieldHeight = fieldHeight;
        var desc = new Texture2DDescription(Format.R16_Float, (uint)fieldWidth, (uint)fieldHeight, FieldSlices, 1,
            BindFlags.ShaderResource);
        _field = _gpu.Device.CreateTexture2D(desc);
        _fieldSrv = _gpu.Device.CreateShaderResourceView(_field);
    }

    /// <summary>
    /// Uploads the network outputs: <paramref name="flow"/> is 4 x H x W fp16, <paramref name="maskLogit"/> is
    /// H x W fp16, both planar and tightly packed.
    /// </summary>
    public void UploadField(nint flow, nint maskLogit)
    {
        if (_field is null) throw new InvalidOperationException("Configure must be called first.");
        uint rowPitch = (uint)(FieldWidth * sizeof(ushort));
        uint slicePitch = rowPitch * (uint)FieldHeight;
        using (_gpu.UseContext())
        {
            for (uint slice = 0; slice < 4; slice++)
                _gpu.Context.UpdateSubresource(_field, slice, null, flow + (nint)(slice * slicePitch), rowPitch, slicePitch);
            _gpu.Context.UpdateSubresource(_field, 4, null, maskLogit, rowPitch, slicePitch);
        }
    }

    /// <summary>
    /// Marks which pixels changed between the two real frames of the pair that is about to be rendered; every
    /// following <see cref="Render"/> copies the unchanged ones. Call once per pair.
    /// </summary>
    public void BuildPairMask(ID3D11ShaderResourceView frameA, ID3D11ShaderResourceView frameB, int contentWidth, int contentHeight)
    {
        if (_mask is null || contentWidth != _maskWidth || contentHeight != _maskHeight)
        {
            _maskSrv?.Dispose();
            _maskRtv?.Dispose();
            _mask?.Dispose();
            _maskWidth = contentWidth;
            _maskHeight = contentHeight;
            _mask = _gpu.Device.CreateTexture2D(new Texture2DDescription(Format.R8_UNorm, (uint)contentWidth, (uint)contentHeight, 1, 1,
                BindFlags.ShaderResource | BindFlags.RenderTarget));
            _maskSrv = _gpu.Device.CreateShaderResourceView(_mask);
            _maskRtv = _gpu.Device.CreateRenderTargetView(_mask);
        }

        var p = new MaskParams { MaxX = contentWidth - 1, MaxY = contentHeight - 1, Threshold = UnchangedThreshold };
        var ctx = _gpu.Context;
        using (_gpu.UseContext())
        {
            ctx.UpdateSubresource(in p, _maskConstants);
            ctx.OMSetRenderTargets(_maskRtv!);
            ctx.RSSetViewport(0, 0, contentWidth, contentHeight);
            ctx.IASetInputLayout(null);
            ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            ctx.VSSetShader(_vs);
            ctx.PSSetShader(_maskPs);
            ctx.PSSetConstantBuffer(0, _maskConstants);
            ctx.PSSetShaderResource(0, frameA);
            ctx.PSSetShaderResource(1, frameB);
            ctx.Draw(3, 0);
            ctx.PSUnsetShaderResources(0, 2);
            ctx.OMSetRenderTargets(0, [], null!);
        }
        _maskValid = true;
    }

    /// <summary>Forgets the pair mask: following renders warp every pixel (RIFE's own composition).</summary>
    public void ClearPairMask() => _maskValid = false;

    /// <summary>
    /// Renders the in-between frame described by the uploaded field into <paramref name="target"/>.
    /// The frame textures may be larger than the content (capture pools are): only the content is used.
    /// </summary>
    public void Render(ID3D11ShaderResourceView frameA, ID3D11ShaderResourceView frameB,
        int textureWidth, int textureHeight, int contentWidth, int contentHeight, ID3D11RenderTargetView target)
    {
        if (_fieldSrv is null) throw new InvalidOperationException("Configure must be called first.");
        bool snap = _maskValid && _maskWidth == contentWidth && _maskHeight == contentHeight;
        var p = new Params
        {
            InvFieldSize = new Vector2(1f / FieldWidth, 1f / FieldHeight),
            UvScale = new Vector2((float)contentWidth / textureWidth, (float)contentHeight / textureHeight),
            UvMax = new Vector2((contentWidth - 0.5f) / textureWidth, (contentHeight - 0.5f) / textureHeight),
            FlowToPixels = new Vector2((float)contentWidth / FieldWidth, (float)contentHeight / FieldHeight),
            SnapFlowSquared = SnapFlowPixels * SnapFlowPixels,
        };
        var ctx = _gpu.Context;
        using (_gpu.UseContext())
        {
            ctx.UpdateSubresource(in p, _constants);
            ctx.OMSetRenderTargets(target);
            ctx.RSSetViewport(0, 0, contentWidth, contentHeight);
            ctx.IASetInputLayout(null);
            ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            ctx.VSSetShader(_vs);
            ctx.PSSetShader(_ps);
            ctx.PSSetConstantBuffer(0, _constants);
            ctx.PSSetSampler(0, _linearClamp);
            ctx.PSSetShaderResource(0, frameA);
            ctx.PSSetShaderResource(1, frameB);
            ctx.PSSetShaderResource(2, _fieldSrv);
            ctx.PSSetShaderResource(3, snap ? _maskSrv! : _everythingChangedSrv);
            ctx.Draw(3, 0);
            ctx.PSUnsetShaderResources(0, 4);
            ctx.OMSetRenderTargets(0, [], null!);   // unbind so the target can be sampled or copied next
        }
    }

    public void Dispose()
    {
        _fieldSrv?.Dispose();
        _field?.Dispose();
        _maskSrv?.Dispose();
        _maskRtv?.Dispose();
        _mask?.Dispose();
        _everythingChangedSrv.Dispose();
        _everythingChanged.Dispose();
        _constants.Dispose();
        _maskConstants.Dispose();
        _linearClamp.Dispose();
        _maskPs.Dispose();
        _ps.Dispose();
        _vs.Dispose();
    }
}
