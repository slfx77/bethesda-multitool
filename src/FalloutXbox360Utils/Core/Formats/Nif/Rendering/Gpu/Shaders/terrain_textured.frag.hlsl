// v3 Phase 2b textured-terrain pixel shader. Samples up to 4 diffuse textures bound at
// t0..t3 with world-space UV (wrap, anisotropic), blends each successive layer over the
// previous using the corresponding R8 opacity texture at t4..t6 sampled with quadrant-local
// UV (clamp, bilinear). Final color is modulated by per-vertex VCLR and a single hardcoded
// Lambert sun for shape.
//
// The shader uses explicit per-layer `if` branches rather than an array+loop because
// Vortice's HLSL compile is finicky about non-literal array indexing on texture arrays.
// Up to N=4 layers per quadrant (1 base + 3 alpha) — cells with more layers truncate at
// the C# side (TerrainRenderer.SelectQuadrant) and silently drop the extras.

Texture2D    tDiffuse0 : register(t0);
Texture2D    tDiffuse1 : register(t1);
Texture2D    tDiffuse2 : register(t2);
Texture2D    tDiffuse3 : register(t3);
Texture2D    tOpacity0 : register(t4);
Texture2D    tOpacity1 : register(t5);
Texture2D    tOpacity2 : register(t6);
SamplerState sDiffuse  : register(s0);
SamplerState sOpacity  : register(s1);

cbuffer PerFrame    : register(b0) { float4x4 uViewProj; }
cbuffer PerQuadrant : register(b1) { float4 uQuadrantUvOrigin_LayerCount_UvScale; }
cbuffer PerMode     : register(b2)
{
    // x = 1.0 → VCLR-only debug mode (skip texture sampling, render Phase 2a look)
    // y..w = padding
    float4 uDebugMode_Pad;
};

struct PSInput
{
    float4 Position     : SV_Position;
    float3 vWorldNormal : TEXCOORD0;
    float4 vVertexColor : TEXCOORD1;
    float2 vWorldUv     : TEXCOORD2;
    float2 vQuadrantUv  : TEXCOORD3;
};

float4 main(PSInput input) : SV_Target
{
    float3 normal = normalize(input.vWorldNormal);
    float lambert = saturate(dot(normal, normalize(float3(0.5, 0.5, 1.0))));
    float shade = 0.4 + 0.6 * lambert;

    if (uDebugMode_Pad.x > 0.5)
    {
        return float4(input.vVertexColor.rgb * shade, 1.0);
    }

    int layerCount = (int)uQuadrantUvOrigin_LayerCount_UvScale.z;
    float3 color = tDiffuse0.Sample(sDiffuse, input.vWorldUv).rgb;

    if (layerCount >= 2)
    {
        float a1 = tOpacity0.Sample(sOpacity, input.vQuadrantUv).r;
        color = lerp(color, tDiffuse1.Sample(sDiffuse, input.vWorldUv).rgb, a1);
    }
    if (layerCount >= 3)
    {
        float a2 = tOpacity1.Sample(sOpacity, input.vQuadrantUv).r;
        color = lerp(color, tDiffuse2.Sample(sDiffuse, input.vWorldUv).rgb, a2);
    }
    if (layerCount >= 4)
    {
        float a3 = tOpacity2.Sample(sOpacity, input.vQuadrantUv).r;
        color = lerp(color, tDiffuse3.Sample(sDiffuse, input.vWorldUv).rgb, a3);
    }

    // VCLR is per-vertex tint Bethesda uses for art direction (sun bleach, moist edges).
    // Multiplicative so default-white VCLR leaves the texture untouched.
    color *= input.vVertexColor.rgb;
    return float4(color * shade, 1.0);
}
