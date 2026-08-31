// v3 Phase 2c+ engine-accurate terrain fragment shader. Samples up to 16 diffuse textures
// addressed by per-cell bindless texture indices and composes them by the per-vertex weights
// interpolated across the cell mesh. Mirrors the per-vertex weighted sum the engine's
// NiTerrainLandShader does, so quadrant midlines and (with neighbor-fed weight tables)
// cell boundaries fade smoothly across rather than snapping at a hard edge.
//
// FNV normal maps use the exact same weights and world-space UV as their diffuse companions,
// matching the recovered SLS2128/SLS2132 land passes. All samples use anisotropic wrap so tiling stays
// sharp at any zoom — that's the property the pre-baked-composite alternative would have
// given up.

// Layer-weight quads this variant reads — must equal the vertex shader's TERRAIN_BLEND_QUADS, since
// PSInput has to match VSOutput exactly. Set per cell from ceil(ActiveSlotCount / 4), which also
// shortens the weighted sum below from a fixed 16 iterations to 4 * this. See the vertex shader for
// why dropping the absent quads is exact.
#ifndef TERRAIN_BLEND_QUADS
#define TERRAIN_BLEND_QUADS 4
#endif

Texture2D    textures[] : register(t0, space1);
SamplerState sDiffuse  : register(s0);
SamplerState sShadowPoint : register(s3); // CLAMP, point — sun-shadow-map depth taps (PCF)

cbuffer PerFrame : register(b0)
{
    float4x4 uViewProj;
};

cbuffer PerCell : register(b1)
{
    // 16 bindless diffuse indices for the cell's blend slots (uint4[4] = slots 0..3, 4..7, …).
    // This original array stays first so its byte offsets remain ABI-stable.
    uint4 uTextureIndices[4];
    // FNV TXST slot-1 companions. 0xffffffff means no authored normal (exact flat identity).
    uint4 uNormalTextureIndices[4];
    // x low 16 bits: slot uses ATI2/BC5 and therefore stores only XY; yzw reserved.
    uint4 uNormalDecodeMetadata;
};

cbuffer PerMode : register(b2)
{
    // x = 1.0 → show diffuse terrain textures (0 → flat white base)
    // y = uv scale (mirrors vertex shader so it stays in sync)
    // z = 1.0 → apply per-vertex (VCLR) tint
    // w = 1.0 → apply recovered layered FNV terrain normal maps
    // (textures off + vclr on == the old "vertex colors only" debug look)
    float4 uDebugMode_UvScale_Pad;
};

#include "atmosphere.hlsli"
#include "scene_lighting.hlsli"
#include "fog.hlsli"

// Terrain both RECEIVES and CASTS sun shadows (hillsides shade valleys and self-shadow — the near
// cascade's small texels keep the depth bias tight enough for gentle slopes). Terrain and placed
// meshes must darken the same way under the same occluder, so both consume the shared header.
#include "shadow_sampling.hlsli"

// Per-pixel light factor (rgb) for a world-space normal. When lighting is disabled
// (uSunColorLighting.w == 0) this returns the EXACT legacy flat shade — scalar 0.4 + 0.6*lambert
// against the old fixed sun — so toggling lighting off is pixel-identical to the pre-atmosphere
// viewer. Enabled: colored ambient + sun·(N·L)·sunShadow (shadow = 1.0 whenever the shadow pass
// is off, preserving the exact pre-shadow output), energy-bounded so a fully sunlit surface lands
// near the legacy max (~1.0) instead of blowing out.
float3 AtmosphereLight(float3 N, float3 worldPos, float2 pixelPosition, float sunShadow)
{
    if (uSunColorLighting.w < 0.5)
    {
        float legacyLambert = saturate(dot(N, normalize(float3(0.5, 0.5, 1.0))));
        return (0.4 + 0.6 * legacyLambert).xxx;
    }

    // SLS sum, IDENTICAL to reference.frag.hlsl so terrain and placed meshes shade the SAME way:
    // lit = kAmbientScale*AmbientColor + NdotL*SunColor. The engine value is FULL-strength ambient (1.0):
    // the old 0.3 "ambient scale" was a misread of fRam8323ca10, which is the LIGHTNING-FLASH ambient
    // boost fraction (additive, decays to zero — see reference.frag.hlsl's AtmosphereLight for the full
    // refutation with decompile citations). Terrain and objects MUST read the same uAmbientColor.w
    // (GameProfile.AmbientLightScale) or they re-imbalance at night — that invariant stands regardless of
    // the value. Falls back to 1.0 when the slot is unset.
    float kAmbientScale = uAmbientColor.w > 0.0001 ? uAmbientColor.w : 1.0;
    float3 unitNormal = normalize(N);
    float3 ambient = uAmbientColor.rgb;
    if (uAmbientPositiveX.w > 0.5)
    {
        float3 normalSquared = unitNormal * unitNormal;
        ambient = (unitNormal.x >= 0.0 ? uAmbientPositiveX.rgb : uAmbientNegativeX.rgb) * normalSquared.x +
                  (unitNormal.y >= 0.0 ? uAmbientPositiveY.rgb : uAmbientNegativeY.rgb) * normalSquared.y +
                  (unitNormal.z >= 0.0 ? uAmbientPositiveZ.rgb : uAmbientNegativeZ.rgb) * normalSquared.z;
    }
    float ndotl = saturate(dot(N, uSunDirIntensity.xyz));
    float3 shade = ambient * kAmbientScale +
        uSunColorLighting.rgb * (ndotl * sunShadow) +
        PlacedLightContribution(N, worldPos, pixelPosition);
    return max(shade, 0.0);
}

static const uint MissingNormalTextureIndex = 0xffffffffu;

float3 DecodeTerrainNormal(uint textureIndex, uint slot, float2 uv)
{
    // Do not index the bindless table for a missing TX01. Returning the exact tangent-space flat
    // vector keeps missing layers and their blends on the authored LAND/geometric normal.
    float3 decoded = float3(0.0, 0.0, 1.0);
    [branch] if (textureIndex != MissingNormalTextureIndex)
    {
        // Recovered PC FNV land PSOs decode RGB directly as (sample - 0.5) * 2. Bethesda DDS normals
        // are already authored for DirectX, so green is deliberately not inverted here. Xbox DDX
        // terrain normals promote asynchronously to ATI2/BC5, which exposes only RG; b1's live
        // per-draw mask selects the same positive-Z reconstruction used by the reference-material path.
        float3 packed = textures[NonUniformResourceIndex(textureIndex)].Sample(sDiffuse, uv).rgb;
        float2 xy = packed.rg * 2.0 - 1.0;
        bool reconstructZ = (uNormalDecodeMetadata.x & (1u << slot)) != 0u;
        decoded = reconstructZ
            ? float3(xy, sqrt(saturate(1.0 - dot(xy, xy))))
            : packed * 2.0 - 1.0;
    }
    return decoded;
}

float3 TerrainTangentToWorld(float3 tangentNormal, float3 geometricNormal)
{
    float tangentNormalLengthSquared = dot(tangentNormal, tangentNormal);
    tangentNormal = tangentNormalLengthSquared > 1e-8
        ? tangentNormal * rsqrt(tangentNormalLengthSquared)
        : float3(0.0, 0.0, 1.0);
    float3 N = normalize(geometricNormal);

    // Terrain UV is absolute world XY. For a height field, the +U tangent has zero Y and is
    // proportional to (N.z, 0, -N.x); cross(N,T) supplies the right-handed +V bitangent.
    // A vertical north/south-facing limit makes that vector degenerate, where world +X remains a
    // valid tangent. LAND is a height field, but retain the guard for malformed/runtime records.
    float3 T = float3(N.z, 0.0, -N.x);
    float tangentLengthSquared = dot(T, T);
    T = tangentLengthSquared > 1e-8
        ? T * rsqrt(tangentLengthSquared)
        : float3(1.0, 0.0, 0.0);
    float3 B = normalize(cross(N, T));

    return normalize(tangentNormal.x * T + tangentNormal.y * B + tangentNormal.z * N);
}

struct PSInput
{
    float4 Position      : SV_Position;
    float3 vWorldNormal  : TEXCOORD0;
    float4 vVertexColor  : TEXCOORD1;
    float2 vWorldUv      : TEXCOORD2;
#if TERRAIN_BLEND_QUADS >= 1
    float4 vLayerWeights0 : TEXCOORD3;
#endif
#if TERRAIN_BLEND_QUADS >= 2
    float4 vLayerWeights1 : TEXCOORD4;
#endif
#if TERRAIN_BLEND_QUADS >= 3
    float4 vLayerWeights2 : TEXCOORD5;
#endif
#if TERRAIN_BLEND_QUADS >= 4
    float4 vLayerWeights3 : TEXCOORD6;
#endif
    float3 vWorldPos     : TEXCOORD7;
};

float4 main(PSInput input) : SV_Target
{
    // Mirror-pass clip plane (b3 uClipPlane, origin-relative space; neutral (0,0,0,1) = no-op).
    clip(dot(input.vWorldPos, uClipPlane.xyz) + uClipPlane.w);

    float3 normal = normalize(input.vWorldNormal);
    float3 color;
    if (uDebugMode_UvScale_Pad.x > 0.5)
    {
        // Engine-accurate weighted sum across the cell's blend slots — 4 * TERRAIN_BLEND_QUADS of
        // them, up to 16, which matches the 2D per-pixel blit's layer ceiling so the 3D blend is
        // non-lossy at the widest variant. Per-vertex weights were
        // renormalized at table-build time to sum to ~1, but interpolation across the mesh may
        // shift the sum slightly (especially near vertices with empty weight sets) — the
        // totalWeight rescale below restores energy conservation per pixel.
        float4 weights[TERRAIN_BLEND_QUADS];
        weights[0] = input.vLayerWeights0;
#if TERRAIN_BLEND_QUADS >= 2
        weights[1] = input.vLayerWeights1;
#endif
#if TERRAIN_BLEND_QUADS >= 3
        weights[2] = input.vLayerWeights2;
#endif
#if TERRAIN_BLEND_QUADS >= 4
        weights[3] = input.vLayerWeights3;
#endif

        color = 0;
        float3 tangentNormalSum = 0;
        float totalWeight = 0;
        bool useTerrainNormals = uDebugMode_UvScale_Pad.w > 0.5;
        [unroll] for (int g = 0; g < TERRAIN_BLEND_QUADS; g++)
        {
            [unroll] for (int c = 0; c < 4; c++)
            {
                float wt = weights[g][c];
                if (wt > 0)
                {
                    uint ti = uTextureIndices[g][c];
                    color += wt * textures[NonUniformResourceIndex(ti)].Sample(sDiffuse, input.vWorldUv).rgb;
                    // Recovered SLS2128/SLS2132 use the very same v0/v1 layer weights for the normal
                    // and diffuse companion samplers, then normalize once after accumulation.
                    [branch] if (useTerrainNormals)
                    {
                        uint slot = (uint)(g * 4 + c);
                        tangentNormalSum += wt * DecodeTerrainNormal(
                            uNormalTextureIndices[g][c], slot, input.vWorldUv);
                    }
                    totalWeight += wt;
                }
            }
        }

        if (totalWeight > 0.001)
        {
            color /= totalWeight;
            if (useTerrainNormals)
            {
                float normalLengthSquared = dot(tangentNormalSum, tangentNormalSum);
                float3 tangentNormal = normalLengthSquared > 1e-8
                    ? tangentNormalSum * rsqrt(normalLengthSquared)
                    : float3(0.0, 0.0, 1.0);
                normal = TerrainTangentToWorld(tangentNormal, normal);
            }
        }
        else
        {
            // Vertex with no slot contributions — typically corner of a cell whose every
            // neighbor was also empty. Render as engine-default to match the 2D fallback.
            color = textures[NonUniformResourceIndex(uTextureIndices[0].x)].Sample(sDiffuse, input.vWorldUv).rgb;
            if (useTerrainNormals)
            {
                normal = TerrainTangentToWorld(
                    DecodeTerrainNormal(uNormalTextureIndices[0].x, 0u, input.vWorldUv),
                    normal);
            }
        }
    }
    else
    {
        // Terrain textures off — flat white base so VCLR / shading still read.
        color = float3(1.0, 1.0, 1.0);
    }

    // VCLR is per-vertex tint Bethesda uses for art direction (sun bleach, moist edges).
    if (uDebugMode_UvScale_Pad.z > 0.5)
    {
        color *= input.vVertexColor.rgb;
    }

    // Shared atmosphere lighting (rgb), now fed the weighted terrain normal for FNV. Lighting-off
    // inside AtmosphereLight retains its existing diagnostic equation; non-FNV games never enter
    // the normal-map branch because b2.w is zero.
    float sunShadow = uSunColorLighting.w >= 0.5 ? ShadowFactor(input.vWorldPos) : 1.0;
    float3 shade = AtmosphereLight(normal, input.vWorldPos, input.Position.xy, sunShadow);
    return float4(ApplyFog(color * shade, input.vWorldPos), 1.0);
}
