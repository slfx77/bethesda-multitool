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

// Summed diffuse contribution of the placed point lights (count = uAtmosphereParams.w) at a
// world-space position, for a world-space normal.
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

#endif // SCENE_LIGHTING_HLSLI
