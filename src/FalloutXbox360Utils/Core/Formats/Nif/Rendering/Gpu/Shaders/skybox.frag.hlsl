// v3 Phase 5 skybox fragment shader. A vertical gradient from the horizon color up to the sky-top
// color (both from the shared b3 atmosphere CB, so they track the time-of-day slider + weather) plus
// a procedural sun disc + soft glare at the atmosphere sun direction. +Z is up (matches the
// AtmosphereState sun frame). PLACEHOLDER gradient/disc model — P2b/P5b grounds it against the
// decompiled Sky/Atmosphere shader.

// Shared scene atmosphere (b3). CPU mirror: WorldView3DControl.AtmosphereConstants (7×float4), bound
// once per frame for the whole scene. Layout is byte-identical to the terrain/reference/water copies.
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

struct PSInput
{
    float4 Position : SV_Position;
    float3 vRayDir  : TEXCOORD0;
};

float4 main(PSInput input) : SV_Target
{
    float3 ray = normalize(input.vRayDir);

    // Vertical gradient: horizon at the skyline (ray.z≈0) → top overhead (ray.z≈1). Below the horizon
    // keeps the horizon color (no separate ground plane in this cut).
    float3 sky = lerp(uSkyHorizon.rgb, uSkyTopSkyEnabled.rgb, saturate(ray.z));

    // Procedural sun disc + glare, only when the sun is above the horizon (sunDir.z > 0).
    float3 sunDir = uSunDirIntensity.xyz;
    if (sunDir.z > 0.0)
    {
        float sunDot = saturate(dot(ray, normalize(sunDir)));
        float disc = smoothstep(0.9995, 0.9999, sunDot); // sharp disc (~1° radius)
        float glare = pow(sunDot, 256.0) * 0.35;         // soft halo
        // Sun color from the atmosphere; floor it so the disc stays visible even with lighting off
        // (the skybox toggle is independent of the lighting toggle).
        float3 sunColor = max(uSunColorLighting.rgb, float3(0.08, 0.08, 0.08));
        sky += (disc + glare) * sunColor;
    }

    return float4(sky, 1.0);
}
