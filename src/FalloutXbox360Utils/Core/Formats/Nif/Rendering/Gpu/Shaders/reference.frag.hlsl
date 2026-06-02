// v3 Phase 3 placed-object pixel shader. Samples the diffuse texture, applies alpha-test for
// foliage / fence / wire-mesh REFRs, modulates by vertex color and Lambert lighting using the
// same hardcoded sun direction as terrain (keeps the scene visually coherent).

Texture2D    tDiffuse  : register(t0);
SamplerState sDiffuse  : register(s0); // wrap, anisotropic (set in C#)

cbuffer PerFrame : register(b0) { float4x4 uViewProj; }
cbuffer PerDraw  : register(b1)
{
    float4x4 uWorld;
    float4   uAlphaTestThreshold_DoubleSided_Pad;
}

struct PSInput
{
    float4 Position     : SV_Position;
    float3 vWorldNormal : TEXCOORD0;
    float2 vTexCoord    : TEXCOORD1;
    float4 vVertexColor : TEXCOORD2;
};

float4 main(PSInput input) : SV_Target
{
    float4 sample = tDiffuse.Sample(sDiffuse, input.vTexCoord);

    // Alpha-test branch — controlled per-draw so foliage with NiAlphaProperty bit 9 set
    // discards transparent pixels rather than rendering them as opaque. Threshold of 0 means
    // alpha-test is disabled (most opaque meshes).
    float threshold = uAlphaTestThreshold_DoubleSided_Pad.x;
    if (threshold > 0.0 && sample.a < threshold) discard;

    float3 normal = normalize(input.vWorldNormal);
    float lambert = saturate(dot(normal, normalize(float3(0.5, 0.5, 1.0))));
    float shade = 0.4 + 0.6 * lambert;

    // Vertex color modulates the diffuse — NIFs use it for art-direction tints (e.g. dusty
    // rocks, painted billboards). Default-white VCLR leaves the texture untouched.
    return float4(sample.rgb * input.vVertexColor.rgb * shade, 1.0);
}
