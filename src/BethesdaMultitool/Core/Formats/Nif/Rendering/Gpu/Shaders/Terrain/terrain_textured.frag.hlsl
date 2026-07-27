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

// Shared scene atmosphere (b3). CPU mirror: WorldView3DControl.AtmosphereConstants,
// uploaded once per frame and bound for the whole scene (terrain/reference/water all read it).
cbuffer Atmosphere : register(b3)
{
    float4 uSunDirIntensity;    // xyz = sun world dir (toward sun), w = intensity
    float4 uSunColorLighting;   // rgb = sun color, w = lightingEnabled (0/1)
    float4 uAmbientColor;       // rgb = ambient, w = spare
    float4 uSkyTopSkyEnabled;   // rgb = sky-top color, w = skyEnabled (0/1)
    float4 uSkyHorizon;         // rgb = sky-horizon color, w = spare
    float4 uFogColorFogEnabled; // rgb = fog color, w = fogEnabled (0/1)
    float4 uAtmosphereParams;   // x = gameHour, y = fogNear, z = fogFar, w = placed-light count
    float4 uCameraPosFogPower;  // xyz = camera world pos, w = fog power (1 = linear)
    float4 uFogFarColorMax;     // rgb = far-fog color, w = max powered fog amount
    float4 uCameraOrigin;       // xyz = camera-relative render origin (VS-consumed; layout parity)
    // Sun shadow CASCADES, near→far (appended — earlier shaders declare only the prefix above,
    // layout-safe). Each matrix: origin-relative world → that cascade's shadow clip (xy ±1,
    // z reversed 0..1); each params: x = enabled, y = texel UV size, z = bias, w = SRV slot.
    float4x4 uShadowMatrix0;
    float4x4 uShadowMatrix1;
    float4x4 uShadowMatrix2;
    float4x4 uShadowMatrix3;
    float4 uShadowParams0;
    float4 uShadowParams1;
    float4 uShadowParams2;
    float4 uShadowParams3;
    float4 uAmbientPositiveX;  // w = full DALC cube present
    float4 uAmbientNegativeX;
    float4 uAmbientPositiveY;
    float4 uAmbientNegativeY;
    float4 uAmbientPositiveZ;
    float4 uAmbientNegativeZ;
};

// Must stay layout-identical to reference.frag.hlsl and GpuPointLight.
struct PointLight
{
    float4 PositionRadius;
    float4 ColorIntensity;
    float4 AuthoredMetadata;    // parsed falloff/FOV/flags; retained, not interpreted by FNV path
    float4 Reserved;
};
StructuredBuffer<PointLight> uPointLights : register(t9, space0);

// Skyrim BSLightingShader constant fog: the same powered, FNAM-capped amount blends near→far fog
// color and then surface→fog. Legacy weather binds far=near/max=1.
float3 ApplyFog(float3 color, float3 worldPos)
{
    if (uFogColorFogEnabled.w < 0.5)
    {
        return color;
    }

    float dist = length(worldPos - uCameraPosFogPower.xyz);
    float q = saturate((dist - uAtmosphereParams.y) / max(uAtmosphereParams.z - uAtmosphereParams.y, 1.0));
    float amount = min(pow(q, max(uCameraPosFogPower.w, 0.01)), saturate(uFogFarColorMax.w));
    float3 fogRgb = lerp(uFogColorFogEnabled.rgb, uFogFarColorMax.rgb, amount);
    return lerp(color, fogRgb, amount);
}

// One cascade attempt — IDENTICAL to reference.frag.hlsl's (terrain and placed meshes must darken
// the same way under the same occluder; see there for the full rationale): footprint test with a
// PCF-border margin, then a 3x3 tent of BILINEAR GatherRed comparison taps.
bool TryCascadeShadow(float4x4 shadowMatrix, float4 cascade, float3 worldPos, out float visibility)
{
    visibility = 1.0;
    if (cascade.x < 0.5)
    {
        return false;
    }

    float4 clip = mul(shadowMatrix, float4(worldPos, 1.0));
    float texel = cascade.y;
    float2 uv = float2(clip.x * 0.5 + 0.5, 0.5 - clip.y * 0.5);
    float border = 2.5 * texel;
    if (min(uv.x, uv.y) < border || max(uv.x, uv.y) > 1.0 - border || clip.z <= 0.0 || clip.z >= 1.0)
    {
        return false;
    }

    uint slot = (uint)cascade.w;
    float reference = clip.z + cascade.z;
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
    visibility = lit / 9.0;
    return true;
}

// Sun-shadow visibility for an (origin-relative) world position — 1 = fully lit, 0 = fully
// occluded. CASCADED: the smallest (sharpest) cascade containing the sample wins; terrain both
// receives AND casts (hillsides shade valleys and self-shadow — the near cascade's small texels
// keep the depth bias tight enough for gentle slopes). All cascades disabled returns 1.0:
// pixel-identical to the pre-shadow renderer.
float ShadowFactor(float3 worldPos)
{
    float visibility;
    if (TryCascadeShadow(uShadowMatrix0, uShadowParams0, worldPos, visibility)) return visibility;
    if (TryCascadeShadow(uShadowMatrix1, uShadowParams1, worldPos, visibility)) return visibility;
    if (TryCascadeShadow(uShadowMatrix2, uShadowParams2, worldPos, visibility)) return visibility;
    if (TryCascadeShadow(uShadowMatrix3, uShadowParams3, worldPos, visibility)) return visibility;
    return 1.0;
}

// Per-pixel light factor (rgb) for a world-space normal. When lighting is disabled
// (uSunColorLighting.w == 0) this returns the EXACT legacy flat shade — scalar 0.4 + 0.6*lambert
// against the old fixed sun — so toggling lighting off is pixel-identical to the pre-atmosphere
// viewer. Enabled: colored ambient + sun·(N·L)·sunShadow (shadow = 1.0 whenever the shadow pass
// is off, preserving the exact pre-shadow output), energy-bounded so a fully sunlit surface lands
// near the legacy max (~1.0) instead of blowing out.
float3 PlacedLightContribution(float3 N, float3 worldPos)
{
    float3 contribution = 0.0;
    uint count = (uint)max(round(uAtmosphereParams.w), 0.0);
    [loop]
    for (uint i = 0; i < count; i++)
    {
        PointLight light = uPointLights[i];
        float radius = light.PositionRadius.w;
        float3 toLight = light.PositionRadius.xyz - worldPos;
        float distanceSquared = dot(toLight, toLight);
        float radiusSquared = radius * radius;
        if (radius <= 0.0 || distanceSquared >= radiusSquared)
        {
            continue;
        }

        float3 L = toLight * rsqrt(max(distanceSquared, 1e-8));
        float ndotl = saturate(dot(N, L));
        if (ndotl <= 0.0)
        {
            continue;
        }

        // Exact shipped FNV SLS2128 omni term: 1-dot((lightPos-worldPos)/radius, ...), i.e.
        // 1-(d/r)^2 (pc_land_shader_disassembly.txt, SLS 2128-2131). The engine draws a bounded
        // light volume; this global loop performs that same bound explicitly above.
        float attenuation = saturate(1.0 - distanceSquared / radiusSquared);
        contribution += light.ColorIntensity.rgb *
            (light.ColorIntensity.w * ndotl * attenuation);
    }
    return contribution;
}

float3 AtmosphereLight(float3 N, float3 worldPos, float sunShadow)
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
        PlacedLightContribution(N, worldPos);
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
    float4 vLayerWeights0 : TEXCOORD3;
    float4 vLayerWeights1 : TEXCOORD4;
    float4 vLayerWeights2 : TEXCOORD5;
    float4 vLayerWeights3 : TEXCOORD6;
    float3 vWorldPos     : TEXCOORD7;
};

float4 main(PSInput input) : SV_Target
{
    float3 normal = normalize(input.vWorldNormal);
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
        float3 tangentNormalSum = 0;
        float totalWeight = 0;
        bool useTerrainNormals = uDebugMode_UvScale_Pad.w > 0.5;
        [unroll] for (int g = 0; g < 4; g++)
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
    float3 shade = AtmosphereLight(normal, input.vWorldPos, sunShadow);
    return float4(ApplyFog(color * shade, input.vWorldPos), 1.0);
}
