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
SamplerState sShadowPoint : register(s3); // CLAMP, point — sun-shadow-map depth taps (PCF)

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
    // x = 1.0 → show diffuse terrain textures (0 → flat white base)
    // y = uv scale (mirrors vertex shader so it stays in sync)
    // z = 1.0 → apply per-vertex (VCLR) tint
    // (textures off + vclr on == the old "vertex colors only" debug look)
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
    float4 uCameraPosFogPower;  // xyz = camera world pos, w = fog power (1 = linear)
    float4 uCameraOrigin;       // xyz = camera-relative render origin (VS-consumed; layout parity)
    // Sun shadow map (appended — earlier shaders declare only the prefix above, layout-safe).
    float4x4 uShadowMatrix;     // origin-relative world → shadow clip (xy ±1, z reversed 0..1)
    float4 uShadowParams;       // x = enabled, y = texel UV size, z = depth bias, w = SRV slot
};

// Engine distance fog (grounded in Sky::UpdateFog): a linear near→far ramp toward the resolved fog
// color, raised to the weather's fog power. fogEnabled (uFogColorFogEnabled.w) gates it; OFF returns
// the color unchanged. near/far/power are the daylight-blended WTHR FNAM values from AtmosphereState.
float3 ApplyFog(float3 color, float3 worldPos)
{
    if (uFogColorFogEnabled.w < 0.5)
    {
        return color;
    }

    float dist = length(worldPos - uCameraPosFogPower.xyz);
    float f = saturate((dist - uAtmosphereParams.y) / max(uAtmosphereParams.z - uAtmosphereParams.y, 1.0));
    f = pow(f, max(uCameraPosFogPower.w, 0.01));
    return lerp(color, uFogColorFogEnabled.rgb, f);
}

// Sun-shadow visibility for an (origin-relative) world position — 1 = fully lit, 0 = fully
// occluded. IDENTICAL to reference.frag.hlsl's ShadowFactor (terrain and placed meshes must
// darken the same way under the same occluder). Terrain RECEIVES shadows but does not cast
// (only the reference batches are replayed into the map) — hills won't self-shadow the valley,
// a documented follow-up. uShadowParams.x OFF returns 1.0: pixel-identical to pre-shadow.
float ShadowFactor(float3 worldPos)
{
    if (uShadowParams.x < 0.5)
    {
        return 1.0;
    }

    // Ortho light projection: w == 1, no perspective divide. Reversed-Z: stored depth GROWS
    // toward the light, so "an occluder exists" reads as stored > pixelDepth + bias.
    float4 clip = mul(uShadowMatrix, float4(worldPos, 1.0));
    float2 uv = float2(clip.x * 0.5 + 0.5, 0.5 - clip.y * 0.5);
    if (min(uv.x, uv.y) < 0.0 || max(uv.x, uv.y) > 1.0 || clip.z <= 0.0 || clip.z >= 1.0)
    {
        return 1.0;
    }

    uint slot = (uint)uShadowParams.w;
    float reference = clip.z + uShadowParams.z;
    float texel = uShadowParams.y;
    // 3x3 tent of BILINEAR comparison taps — IDENTICAL to reference.frag.hlsl's (see there for the
    // rationale): gather each tap's 2x2 quad, compare, and blend the COMPARE RESULTS by the
    // sub-texel fraction so shadow edges resolve smoothly instead of as raw map texels.
    float2 texelPos = uv / texel - 0.5;
    float2 f = frac(texelPos);
    float2 gatherBase = (floor(texelPos) + 0.5) * texel;
    float lit = 0.0;
    [unroll]
    for (int dy = -1; dy <= 1; dy++)
    {
        [unroll]
        for (int dx = -1; dx <= 1; dx++)
        {
            float4 quad = textures[NonUniformResourceIndex(slot)]
                .GatherRed(sShadowPoint, gatherBase + float2(dx, dy) * texel);
            // Gather quad order (v grows downward): x=(0,+1) y=(+1,+1) z=(+1,0) w=(0,0).
            float4 vis = 1.0 - step(reference.xxxx, quad);
            lit += lerp(lerp(vis.w, vis.z, f.x), lerp(vis.x, vis.y, f.x), f.y);
        }
    }
    return lit / 9.0;
}

// Per-pixel light factor (rgb) for a world-space normal. When lighting is disabled
// (uSunColorLighting.w == 0) this returns the EXACT legacy flat shade — scalar 0.4 + 0.6*lambert
// against the old fixed sun — so toggling lighting off is pixel-identical to the pre-atmosphere
// viewer. Enabled: colored ambient + sun·(N·L)·sunShadow (shadow = 1.0 whenever the shadow pass
// is off, preserving the exact pre-shadow output), energy-bounded so a fully sunlit surface lands
// near the legacy max (~1.0) instead of blowing out.
float3 AtmosphereLight(float3 N, float sunShadow)
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
    float ndotl = saturate(dot(N, uSunDirIntensity.xyz));
    return uAmbientColor.rgb * kAmbientScale + uSunColorLighting.rgb * (ndotl * sunShadow);
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
    float3 vWorldPos     : TEXCOORD7;
};

float4 main(PSInput input) : SV_Target
{
    float3 normal = normalize(input.vWorldNormal);
    // Shared atmosphere lighting (rgb). Lighting-off path inside AtmosphereLight reproduces the
    // legacy `0.4 + 0.6*lambert` scalar exactly, so the OFF state is pixel-identical to before.
    float sunShadow = uSunColorLighting.w >= 0.5 ? ShadowFactor(input.vWorldPos) : 1.0;
    float3 shade = AtmosphereLight(normal, sunShadow);

    float3 color;
    if (uDebugMode_UvScale_Pad.x > 0.5)
    {
        // Engine-accurate weighted sum across the cell's blend slots (up to 16 — matches the 2D
        // per-pixel blit's layer ceiling, so the 3D blend is non-lossy). Per-vertex weights were
        // renormalized at table-build time to sum to ~1, but interpolation across the mesh may
        // shift the sum slightly (especially near vertices with empty weight sets) — the
        // totalWeight rescale below restores energy conservation per pixel.
        float4 weights[4] = {
            input.vLayerWeights0, input.vLayerWeights1, input.vLayerWeights2, input.vLayerWeights3
        };

        color = 0;
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
    return float4(ApplyFog(color * shade, input.vWorldPos), 1.0);
}
