// A picture as it is on the screen (BGRA) to the two planes of an NV12 video frame, for the recorder:
// BT.709 colours in limited range (luma 16-235, chroma 16-240), which is what players assume for HD video.
// Luma goes to the R8 view of the NV12 texture at full size, chroma to its R8G8 view at half size.
Texture2D<float4> Picture : register(t0);

struct PSIn
{
    float4 pos : SV_Position;
    float2 uv  : TEXCOORD0;
};

float Luma(PSIn i) : SV_Target
{
    float3 c = Picture.Load(int3(i.pos.xy, 0)).rgb;
    return (16.0 + 219.0 * dot(c, float3(0.2126, 0.7152, 0.0722))) / 255.0;
}

float2 Chroma(PSIn i) : SV_Target
{
    // One chroma sample for every 2 x 2 pixels: their mean.
    int2 p = int2(i.pos.xy) * 2;
    float3 c = (Picture.Load(int3(p, 0)).rgb + Picture.Load(int3(p + int2(1, 0), 0)).rgb
              + Picture.Load(int3(p + int2(0, 1), 0)).rgb + Picture.Load(int3(p + int2(1, 1), 0)).rgb) * 0.25;
    float cb = dot(c, float3(-0.2126, -0.7152, 0.9278)) / 1.8556;
    float cr = dot(c, float3(0.7874, -0.7152, -0.0722)) / 1.5748;
    return (128.0 + 224.0 * float2(cb, cr)) / 255.0;
}
