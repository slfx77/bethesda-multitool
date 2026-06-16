// v3 Phase 2c+ engine-accurate terrain fragment shader. Samples up to 4 diffuse textures
// addressed by per-cell bindless texture indices (from CellTerrainTextureSet's
// top-4-by-total-weight selection) and composes them by the per-vertex weights
// interpolated across the cell mesh. Mirrors the per-vertex weighted sum the engine's
// NiTerrainLandShader does, so quadrant midlines and (with neighbor-fed weight tables)
// cell boundaries fade smoothly across rather than snapping at a hard edge.
//
// All diffuse samples share the same world-space UV (anisotropic wrap) so tiling stays
// sharp at any zoom — that's the property the pre-baked-composite alternative would have
// given up.

Texture2D    textures[] : register(t0, space1);
SamplerState sDiffuse  : register(s0);

cbuffer PerFrame : register(b0)
{
    float4x4 uViewProj;
};

cbuffer PerCell : register(b1)
{
    // 16 bindless diffuse indices for the cell's blend slots (uint4[4] = slots 0..3, 4..7, …).
    uint4 uTextureIndices[4];
};

cbuffer PerMode : register(b2)
{
    // x = 1.0 → VCLR-only debug mode
    // y = uv scale (mirrors vertex shader so it stays in sync)
    // zw = padding
    float4 uDebugMode_UvScale_Pad;
};

// Shared scene atmosphere (b3). CPU mirror: WorldView3DControl.AtmosphereConstants (7×float4),
// uploaded once per frame and bound for the whole scene (terrain/reference/water all read it).
cbuffer Atmosphere : register(b3)
{
    float4 uSunDirIntensity;    // xyz = sun world dir (toward sun), w = intensity
    float4 uSunColorLighting;   // rgb = sun color, w = lightingEnabled (0/1)
    float4 uAmbientColor;       // rgb = ambient, w = spare
    float4 uSkyTopSkyEnabled;   // rgb = sky-top color, w = skyEnabled (0/1)
    float4 uSkyHorizon;         // rgb = sky-horizon color, w = spare
    float4 uFogColorFogEnabled; // rgb = fog color, w = fogEnabled (0/1)
    float4 uAtmosphereParams;   // x = gameHour, y = fogNear, z = fogFar, w = time
};

// Per-pixel light factor (rgb) for a world-space normal. When lighting is disabled
// (uSunColorLighting.w == 0) this returns the EXACT legacy flat shade — scalar 0.4 + 0.6*lambert
// against the old fixed sun — so toggling lighting off is pixel-identical to the pre-atmosphere
// viewer. Enabled: colored ambient + sun·(N·L), energy-bounded so a fully sunlit surface lands near
// the legacy max (~1.0) instead of blowing out. (Placeholder sun curve; P2b grounds it in decompile.)
float3 AtmosphereLight(float3 N)
{
    if (uSunColorLighting.w < 0.5)
    {
        float legacyLambert = saturate(dot(N, normalize(float3(0.5, 0.5, 1.0))));
        return (0.4 + 0.6 * legacyLambert).xxx;
    }

    float ndotl = saturate(dot(N, uSunDirIntensity.xyz));
    float ambientLuma = saturate(dot(uAmbientColor.rgb, float3(0.3333, 0.3333, 0.3333)));
    return uAmbientColor.rgb + uSunColorLighting.rgb * ndotl * (1.0 - ambientLuma);
}

struct PSInput
{
    float4 Position      : SV_Position;
    float3 vWorldNormal  : TEXCOORD0;
    float4 vVertexColor  : TEXCOORD1;
    float2 vWorldUv      : TEXCOORD2;
    float4 vLayerWeights0 : TEXCOORD3;
    float4 vLayerWeights1 : TEXCOORD4;
    float4 vLayerWeights2 : TEXCOORD5;
    float4 vLayerWeights3 : TEXCOORD6;
};

float4 main(PSInput input) : SV_Target
{
    float3 normal = normalize(input.vWorldNormal);
    // Shared atmosphere lighting (rgb). Lighting-off path inside AtmosphereLight reproduces the
    // legacy `0.4 + 0.6*lambert` scalar exactly, so the OFF state is pixel-identical to before.
    float3 shade = AtmosphereLight(normal);

    if (uDebugMode_UvScale_Pad.x > 0.5)
    {
        return float4(input.vVertexColor.rgb * shade, 1.0);
    }

    // Engine-accurate weighted sum across the cell's blend slots (up to 16 — matches the 2D
    // per-pixel blit's layer ceiling, so the 3D blend is non-lossy). Per-vertex weights were
    // renormalized at table-build time to sum to ~1, but interpolation across the mesh may
    // shift the sum slightly (especially near vertices with empty weight sets) — the
    // totalWeight rescale below restores energy conservation per pixel.
    float4 weights[4] = {
        input.vLayerWeights0, input.vLayerWeights1, input.vLayerWeights2, input.vLayerWeights3
    };

    float3 color = 0;
    float totalWeight = 0;
    [unroll] for (int g = 0; g < 4; g++)
    {
        [unroll] for (int c = 0; c < 4; c++)
        {
            float wt = weights[g][c];
            if (wt > 0)
            {
                uint ti = uTextureIndices[g][c];
                color += wt * textures[NonUniformResourceIndex(ti)].Sample(sDiffuse, input.vWorldUv).rgb;
                totalWeight += wt;
            }
        }
    }

    if (totalWeight > 0.001)
    {
        color /= totalWeight;
    }
    else
    {
        // Vertex with no slot contributions — typically corner of a cell whose every
        // neighbor was also empty. Render as engine-default to match the 2D fallback.
        color = textures[NonUniformResourceIndex(uTextureIndices[0].x)].Sample(sDiffuse, input.vWorldUv).rgb;
    }

    // VCLR is per-vertex tint Bethesda uses for art direction (sun bleach, moist edges).
    color *= input.vVertexColor.rgb;
    return float4(color * shade, 1.0);
}
