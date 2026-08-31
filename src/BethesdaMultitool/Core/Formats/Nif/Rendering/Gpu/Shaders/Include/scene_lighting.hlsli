// Shared placed-light (LIGH REFR) forward-lighting core: the per-frame point-light list and the
// bounded omni sum both reference.frag.hlsl and terrain_textured.frag.hlsl feed into their
// AtmosphereLight variants.
#ifndef SCENE_LIGHTING_HLSLI
#define SCENE_LIGHTING_HLSLI

#include "atmosphere.hlsli"

// Per-frame local-light list. Must stay layout-identical to GpuPointLight on the CPU side. The
// root SRV lives outside the legacy t0..t7 texture table so terrain and placed geometry can share
// the same forward loop without disturbing bindless slots. Position is already expressed in the
// same absolute/origin-relative space as the consumers' vWorldPos.
struct PointLight
{
    float4 PositionRadius;
    float4 ColorIntensity;
    float4 AuthoredMetadata;    // parsed falloff/FOV/flags; retained, not interpreted by FNV path
    float4 Reserved;
};
StructuredBuffer<PointLight> uPointLights : register(t9, space0);
// Element 0 is uint2(tileCountX, tileCountY); elements 1..N are 64-bit masks split low/high.
// A bit is present whenever the corresponding light sphere can intersect that tile's four side
// planes. Near/far are deliberately ignored, so the CPU culler can create false positives but
// never remove a contributing light because of depth.
StructuredBuffer<uint2> uPointLightTileMasks : register(t10, space0);

static const uint kPointLightTileSize = 16u;

void AccumulatePlacedLight(uint lightIndex, float3 N, float3 worldPos, inout float3 contribution)
{
    PointLight light = uPointLights[lightIndex];
    float radius = light.PositionRadius.w;
    float3 toLight = light.PositionRadius.xyz - worldPos;
    float distanceSquared = dot(toLight, toLight);
    float radiusSquared = radius * radius;
    if (radius <= 0.0 || distanceSquared >= radiusSquared)
    {
        return;
    }

    float3 L = toLight * rsqrt(max(distanceSquared, 1e-8));
    float ndotl = saturate(dot(N, L));
    if (ndotl <= 0.0)
    {
        return;
    }

    // Exact shipped FNV SLS2128 omni term: 1-dot((lightPos-worldPos)/radius, ...), i.e.
    // 1-(d/r)^2 (pc_land_shader_disassembly.txt, SLS 2128-2131). The engine draws a bounded
    // light volume; this global loop performs that same bound explicitly above.
    float attenuation = saturate(1.0 - distanceSquared / radiusSquared);
    contribution += light.ColorIntensity.rgb *
        (light.ColorIntensity.w * ndotl * attenuation);
}

// Summed diffuse contribution of the placed point lights (count = uAtmosphereParams.w) at a
// world-space position, for a world-space normal.
float3 PlacedLightContribution(float3 N, float3 worldPos, float2 pixelPosition)
{
    float3 contribution = 0.0;
    uint count = (uint)max(round(uAtmosphereParams.w), 0.0);
    if (count == 0u)
    {
        return contribution;
    }

    uint2 dimensions = uPointLightTileMasks[0];
    // Defensive compatibility fallback: a malformed/uninitialized header keeps the old full loop
    // rather than dropping illumination. The host normally binds at least a 1x1 all-active grid.
    if (dimensions.x == 0u || dimensions.y == 0u)
    {
        [loop]
        for (uint i = 0u; i < count; i++)
        {
            AccumulatePlacedLight(i, N, worldPos, contribution);
        }
        return contribution;
    }

    uint2 tile = min((uint2)max(pixelPosition, 0.0) / kPointLightTileSize, dimensions - 1u);
    uint2 mask = uPointLightTileMasks[1u + tile.y * dimensions.x + tile.x];

    // Low word then high word, each least-significant bit first, retains the original 0..63
    // floating-point accumulation order (including signed/negative emitters).
    [loop]
    while (mask.x != 0u)
    {
        uint bit = (uint)firstbitlow(mask.x);
        AccumulatePlacedLight(bit, N, worldPos, contribution);
        mask.x &= mask.x - 1u;
    }
    [loop]
    while (mask.y != 0u)
    {
        uint bit = (uint)firstbitlow(mask.y);
        AccumulatePlacedLight(32u + bit, N, worldPos, contribution);
        mask.y &= mask.y - 1u;
    }
    return contribution;
}

#endif // SCENE_LIGHTING_HLSLI
