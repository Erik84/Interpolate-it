// Which pixels differ between the two real frames of a pair?
//
// Output (R8): 1 where anything in the pixel's 3x3 neighbourhood differs between frame A and frame B, else 0.
// The synthesis shader uses it to copy pixels that provably did not move (window chrome, text, a paused part of
// a page) instead of warping them: a flow network gives such pixels small - and next to motion or on a periodic
// pattern sometimes large - bogus motion, which shows as shimmering text.

cbuffer Params : register(b0)
{
    int2  ContentMax;       // content size - 1
    float Threshold;        // largest per-channel difference that still counts as "the same"
    float _pad;
};

Texture2D<float4> FrameA : register(t0);
Texture2D<float4> FrameB : register(t1);

float main(float4 pos : SV_Position) : SV_Target
{
    int2 p = int2(pos.xy);
    float d = 0.0;
    for (int y = -1; y <= 1; y++)
    {
        for (int x = -1; x <= 1; x++)
        {
            int3 q = int3(clamp(p + int2(x, y), int2(0, 0), ContentMax), 0);
            float3 diff = abs(FrameA.Load(q).rgb - FrameB.Load(q).rgb);
            d = max(d, max(diff.r, max(diff.g, diff.b)));
        }
    }
    return d > Threshold ? 1.0 : 0.0;
}
