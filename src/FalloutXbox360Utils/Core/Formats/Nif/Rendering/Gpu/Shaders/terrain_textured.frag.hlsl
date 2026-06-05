// v3 Phase 2c+ engine-accurate terrain fragment shader. Samples up to 4 diffuse textures
// bound at t0..t3 (cell-wide, from CellTerrainTextureSet's top-4-by-total-weight selection)
// and composes them by the per-vertex weights interpolated across the cell mesh. Mirrors the
// per-vertex weighted sum the engine's NiTerrainLandShader does, so quadrant midlines and
// (with neighbor-fed weight tables) cell boundaries fade smoothly across rather than
// snapping at a hard edge.
//
// All diffuse samples share the same world-space UV (anisotropic wrap) so tiling stays
// sharp at any zoom — that's the property the pre-baked-composite alternative would have
// given up.

Texture2D    tDiffuse0 : register(t0);
Texture2D    tDiffuse1 : register(t1);
Texture2D    tDiffuse2 : register(t2);
Texture2D    tDiffuse3 : register(t3);
SamplerState sDiffuse  : register(s0);

cbuffer PerFrame : register(b0)
{
    float4x4 uViewProj;
};

cbuffer PerMode : register(b2)
{
    // x = 1.0 → VCLR-only debug mode
    // y = uv scale (mirrors vertex shader so it stays in sync)
    // zw = padding
    float4 uDebugMode_UvScale_Pad;
};

struct PSInput
{
    float4 Position      : SV_Position;
    float3 vWorldNormal  : TEXCOORD0;
    float4 vVertexColor  : TEXCOORD1;
    float2 vWorldUv      : TEXCOORD2;
    float4 vLayerWeights : TEXCOORD3;
};

float4 main(PSInput input) : SV_Target
{
    float3 normal = normalize(input.vWorldNormal);
    float lambert = saturate(dot(normal, normalize(float3(0.5, 0.5, 1.0))));
    float shade = 0.4 + 0.6 * lambert;

    if (uDebugMode_UvScale_Pad.x > 0.5)
    {
        return float4(input.vVertexColor.rgb * shade, 1.0);
    }

    // Engine-accurate weighted sum across the 4 cell-wide slots. Per-vertex weights were
    // renormalized at table-build time to sum to ~1, but bilinear interpolation across the
    // mesh may shift the sum slightly (especially near vertices with empty weight sets) —
    // the totalWeight rescale below restores energy conservation per pixel.
    float3 color = 0;
    float totalWeight = 0;

    if (input.vLayerWeights.x > 0)
    {
        color += input.vLayerWeights.x * tDiffuse0.Sample(sDiffuse, input.vWorldUv).rgb;
        totalWeight += input.vLayerWeights.x;
    }
    if (input.vLayerWeights.y > 0)
    {
        color += input.vLayerWeights.y * tDiffuse1.Sample(sDiffuse, input.vWorldUv).rgb;
        totalWeight += input.vLayerWeights.y;
    }
    if (input.vLayerWeights.z > 0)
    {
        color += input.vLayerWeights.z * tDiffuse2.Sample(sDiffuse, input.vWorldUv).rgb;
        totalWeight += input.vLayerWeights.z;
    }
    if (input.vLayerWeights.w > 0)
    {
        color += input.vLayerWeights.w * tDiffuse3.Sample(sDiffuse, input.vWorldUv).rgb;
        totalWeight += input.vLayerWeights.w;
    }

    if (totalWeight > 0.001)
    {
        color /= totalWeight;
    }
    else
    {
        // Vertex with no slot contributions — typically corner of a cell whose every
        // neighbor was also empty. Render as engine-default to match the 2D fallback.
        color = tDiffuse0.Sample(sDiffuse, input.vWorldUv).rgb;
    }

    // VCLR is per-vertex tint Bethesda uses for art direction (sun bleach, moist edges).
    color *= input.vVertexColor.rgb;
    return float4(color * shade, 1.0);
}
