// v3 sky-dome vertex shader (the exterior sky). Projects a real celestial-sphere mesh (SkyDomeMesh)
// centered on the camera: world = camPos + dir·radius. Each vertex is a unit WORLD direction (Z up) with
// a lat-long UV, so the gradient + stars map onto geometry oriented in world space (clouds use a flat
// overhead-plane projection from the direction in skydome.frag). Drawn depth-OFF first so terrain
// overwrites it.

cbuffer SkyDome : register(b0)
{
    float4x4 uViewProj;
    float4 uCamPosRadius;     // xyz = camera world pos, w = dome radius
    float4 uCloudTintOpacity; // rgb = cloud tint, a = cloud opacity
    float4 uStarTintFade;     // rgb = star tint, a = star fade (night)
    float4 uCloudScroll;      // xy = cloud scroll offset, z = cloud azimuth tiling, w = star azimuth tiling
    uint4  uSkyTexIndices;    // x = cloud tex index, y = star tex index (0xFFFFFFFF = none)
};

struct VSInput
{
    float3 aDir : TEXCOORD0; // unit world-space direction (Z up)
    float2 aUv  : TEXCOORD1; // lat-long: x = azimuth 0..1, y = elevation 0 (nadir) .. 1 (zenith)
};

struct VSOutput
{
    float4 Position : SV_Position;
    float3 vDir     : TEXCOORD0;
    float2 vUv      : TEXCOORD1;
};

VSOutput main(VSInput input)
{
    float3 world = uCamPosRadius.xyz + input.aDir * uCamPosRadius.w;

    VSOutput o;
    o.Position = mul(uViewProj, float4(world, 1.0));
    o.vDir = input.aDir;
    o.vUv = input.aUv;
    return o;
}
