// RIFE's final step at window resolution: backward-warp the two real frames with the flow estimated at
// inference resolution and blend them with the mask.
//
//   Field slices 0,1 = flow t->0 (x, y)   2,3 = flow t->1 (x, y)   in inference-resolution pixels
//   Field slice  4   = blend-mask logit (weight of frame A before the sigmoid)
//
// Hardware bilinear sampling of the field at the output pixel's uv is the same upsampling RIFE applies
// (bilinear, align_corners = false); adding flow / inference-size to the uv displaces by flow * scale pixels,
// which also holds when the two axes are scaled differently. Clamp addressing matches RIFE's border padding.
//
// Static snap. PairMask (see PairMask.hlsl) says which pixels changed between the two real frames. A pixel whose
// neighbourhood did not change is copied from frame A unwarped when either
//   * the flow there is small: nothing can have moved a pixel or two across an unchanged neighbourhood, or
//   * the places the flow points to did not change either: whatever the flow claims to bring here was not
//     moving. (A static periodic pattern next to motion gets a flow of whole periods - at t = 0 and 1 that is
//     invisible, in between the pattern is shifted by a fraction of its period.)
// A thin object that really passes over an unchanged pixel has a large flow that points to where the object
// is in a real frame, and that place did change, so it is left alone.

cbuffer Params : register(b0)
{
    float2 InvFieldSize;    // 1 / (inference width, height)
    float2 UvScale;         // content size / texture size of the frame textures
    float2 UvMax;           // largest uv that still samples inside the content
    float2 FlowToPixels;    // output pixels per inference pixel
    float  SnapFlowSquared; // (largest flow, in output pixels, that is snapped on an unchanged pixel)^2
    float3 _pad;
};

Texture2D<float4>     FrameA   : register(t0);
Texture2D<float4>     FrameB   : register(t1);
Texture2DArray<float> Field    : register(t2);
Texture2D<float>      PairMask : register(t3);     // content-sized; a 1x1 "everything changed" texture disables the snap
SamplerState LinearClamp : register(s0);

float4 main(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
{
    float2 flowA = float2(Field.SampleLevel(LinearClamp, float3(uv, 0), 0), Field.SampleLevel(LinearClamp, float3(uv, 1), 0));
    float2 flowB = float2(Field.SampleLevel(LinearClamp, float3(uv, 2), 0), Field.SampleLevel(LinearClamp, float3(uv, 3), 0));

    float2 toA = saturate(uv + flowA * InvFieldSize);
    float2 toB = saturate(uv + flowB * InvFieldSize);

    if (PairMask.SampleLevel(LinearClamp, uv, 0) == 0.0)
    {
        float2 pxA = flowA * FlowToPixels, pxB = flowB * FlowToPixels;
        bool smallFlow = max(dot(pxA, pxA), dot(pxB, pxB)) < SnapFlowSquared;
        // Linear sampling: any changed pixel under the footprint counts.
        bool sourcesStatic = PairMask.SampleLevel(LinearClamp, toA, 0) + PairMask.SampleLevel(LinearClamp, toB, 0) == 0.0;
        if (smallFlow || sourcesStatic)
            return float4(FrameA.Load(int3(pos.xy, 0)).rgb, 1.0);
    }

    float logit = Field.SampleLevel(LinearClamp, float3(uv, 4), 0);
    float m = 1.0 / (1.0 + exp(-logit));
    float3 a = FrameA.SampleLevel(LinearClamp, min(toA * UvScale, UvMax), 0).rgb;
    float3 b = FrameB.SampleLevel(LinearClamp, min(toB * UvScale, UvMax), 0).rgb;
    return float4(a * m + b * (1.0 - m), 1.0);
}
