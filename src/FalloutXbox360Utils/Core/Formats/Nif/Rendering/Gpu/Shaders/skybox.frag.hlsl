// v3 skybox fragment shader. A vertical gradient from the horizon color up to the sky-top color (both
// from the shared b3 atmosphere CB, so they track the time-of-day slider + weather), then the real
// in-game CLOUD and STAR textures projected onto the sky and composited over the gradient. +Z is up
// (matches the AtmosphereState sun frame). The sun/moon are drawn separately as textured billboards
// (SkyBillboardRenderer12) after this pass — the old procedural sun disc was removed to avoid a double
// sun. The cloud/star textures stand in for the engine's sky-dome NIF layers (a faithful dome-NIF port
// is a future upgrade); the projection here treats them as an overhead layer at infinity.

Texture2D    textures[] : register(t0, space1);
SamplerState sSky       : register(s0);

// Cloud/star params (this renderer's own b0 CB; uInvViewProj is consumed by the vertex shader).
cbuffer SkyParams : register(b0)
{
    float4x4 uInvViewProj;
    float4 uCloudTintOpacity;  // rgb = cloud tint, a = cloud opacity
    float4 uStarTintFade;      // rgb = star tint, a = star fade (night)
    float4 uCloudScroll;       // xy = cloud scroll offset, z = cloud scale, w = star scale
    uint4  uSkyTexIndices;     // x = cloud tex index, y = star tex index (0xFFFFFFFF = none)
};

// Shared scene atmosphere (b3). CPU mirror: WorldView3DControl.AtmosphereConstants. Layout is
// byte-identical to the terrain/reference/water copies (the skybox reads only the first 7 float4).
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

    // Clouds + stars are projected onto an overhead plane via the view ray (sky-at-infinity, so they
    // rotate with the camera but don't parallax with position). Faded out toward/below the horizon,
    // where the planar projection would otherwise smear to infinity.
    float horizon = saturate((ray.z - 0.02) / 0.20);
    if (horizon > 0.0)
    {
        float2 planar = ray.xy / max(ray.z, 0.08);

        // Stars FIRST (behind clouds): additive, night-faded.
        if (uSkyTexIndices.y != 0xFFFFFFFFu && uStarTintFade.a > 0.001)
        {
            float2 starUv = (planar * uCloudScroll.w) + 0.5;
            float3 star = textures[NonUniformResourceIndex(uSkyTexIndices.y)].Sample(sSky, starUv).rgb;
            sky += star * uStarTintFade.rgb * (uStarTintFade.a * horizon);
        }

        // Clouds OVER: alpha-blended, scrolled, tinted by the time-of-day cloud color.
        if (uSkyTexIndices.x != 0xFFFFFFFFu && uCloudTintOpacity.a > 0.001)
        {
            float2 cloudUv = (planar * uCloudScroll.z) + uCloudScroll.xy;
            float4 cloud = textures[NonUniformResourceIndex(uSkyTexIndices.x)].Sample(sSky, cloudUv);
            float a = saturate(cloud.a * uCloudTintOpacity.a * horizon);
            sky = lerp(sky, cloud.rgb * uCloudTintOpacity.rgb, a);
        }
    }

    return float4(sky, 1.0);
}
