// One pass per captured frame:
//   * area-downscale the BGRA frame to the inference resolution and write it as planar RGB (three stacked
//     R16F planes: rows [0,H) = R, [H,2H) = G, [2H,3H) = B) - exactly the NCHW fp16 tensor the network takes;
//   * per 16x16 tile, the maximum absolute difference against the previous frame's planes and the mean luma,
//     written to extra rows below the planes (row 3H + tileY; columns [0,tilesX) = diff, [tilesX,2*tilesX) = luma).
// One read-back of this single texture therefore delivers the tensor and the change statistics.

cbuffer Params : register(b0)
{
    float2 SrcTexSize;      // source texture size in pixels
    float2 SrcContentSize;  // valid content inside the source texture
    uint2  DstSize;         // inference width, height (multiples of 16)
    uint2  Taps;            // bilinear taps per axis; each tap integrates up to 2 source pixels
    uint   HasPrev;
    uint3  _pad;
};

Texture2D<float4>  Src  : register(t0);
Texture2D<float>   Prev : register(t1);
RWTexture2D<float> Dst  : register(u0);
SamplerState LinearClamp : register(s0);

groupshared float gDiff[256];
groupshared float gLuma[256];

[numthreads(16, 16, 1)]
void main(uint3 id : SV_DispatchThreadID, uint3 gid : SV_GroupID, uint gi : SV_GroupIndex)
{
    float diff = 0.0;
    float luma = 0.0;

    if (id.x < DstSize.x && id.y < DstSize.y)
    {
        // Footprint of this output pixel in source pixels, sampled on a regular grid of bilinear taps.
        // Ratio 1 -> one tap on the pixel centre (exact copy); ratio 2 -> one tap between four pixels (exact
        // 2x2 box); ratio 4 with 2 taps per axis -> exact 4x4 box; other ratios are a close approximation.
        float2 ratio = SrcContentSize / float2(DstSize);
        float2 origin = float2(id.xy) * ratio;
        float2 step = ratio / float2(Taps);
        float2 limit = SrcContentSize - 0.5;
        float3 sum = 0.0;
        for (uint j = 0; j < Taps.y; j++)
        {
            for (uint i = 0; i < Taps.x; i++)
            {
                float2 p = min(origin + (float2(i, j) + 0.5) * step, limit);
                sum += Src.SampleLevel(LinearClamp, p / SrcTexSize, 0).rgb;
            }
        }
        float3 c = sum / float(Taps.x * Taps.y);

        uint2 px = id.xy;
        Dst[px] = c.r;
        Dst[px + uint2(0, DstSize.y)] = c.g;
        Dst[px + uint2(0, 2 * DstSize.y)] = c.b;

        luma = dot(c, float3(0.299, 0.587, 0.114));
        if (HasPrev != 0)
        {
            float3 p = float3(Prev[px], Prev[px + uint2(0, DstSize.y)], Prev[px + uint2(0, 2 * DstSize.y)]);
            float3 d = abs(c - p);
            diff = max(d.r, max(d.g, d.b));
        }
    }

    gDiff[gi] = diff;
    gLuma[gi] = luma;
    GroupMemoryBarrierWithGroupSync();

    if (gi == 0)
    {
        float m = 0.0;
        float s = 0.0;
        for (uint k = 0; k < 256; k++)
        {
            m = max(m, gDiff[k]);
            s += gLuma[k];
        }
        uint tilesX = DstSize.x / 16;
        uint row = 3 * DstSize.y + gid.y;
        Dst[uint2(gid.x, row)] = m;
        Dst[uint2(tilesX + gid.x, row)] = s / 256.0;
    }
}
