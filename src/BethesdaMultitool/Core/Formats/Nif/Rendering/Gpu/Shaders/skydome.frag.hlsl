// v3 sky-dome fragment shader (the sole exterior sky). Composites the horizon→top gradient (shared b3
// atmosphere CB), then the in-game STAR texture on the celestial dome (lat-long UV: u = azimuth,
// v = horizon→zenith — the layout the engine's star art is authored for) and the CLOUD texture on a
// flat overhead plane (gnomonic projection), so clouds read as a scrolling ceiling that compresses
// toward the horizon — the FNV look — rather than wrapped onto the dome. +Z is up. The sun/moon draw
// separately as billboards.

Texture2D    textures[] : register(t0, space1);
SamplerState sSky       : register(s0);

cbuffer SkyDome : register(b0)
{
    float4x4 uViewProj;
    float4 uCamPosRadius;
    float4 uCloudTintOpacity;  // rgb = cloud tint, a = cloud opacity
    float4 uStarTintFade;      // rgb = star tint, a = star fade (night)
    float4 uCloudScroll;       // xy = cloud scroll, z = cloud planar scale, w = star azimuth tiling
    uint4  uSkyTexIndices;     // x = cloud tex index, y = star tex index (0xFFFFFFFF = none)
};

// Shared scene atmosphere (b3). The dome reads only the first 5 float4 (sun + sky colors).
cbuffer Atmosphere : register(b3)
{
    float4 uSunDirIntensity;
    float4 uSunColorLighting;
    float4 uAmbientColor;
    float4 uSkyTopSkyEnabled;   // rgb = sky-top color
    float4 uSkyHorizon;         // rgb = sky-horizon color
};

struct PSInput
{
    float4 Position : SV_Position;
    float3 vDir     : TEXCOORD0;
    float2 vUv      : TEXCOORD1;
};

float4 main(PSInput input) : SV_Target
{
    float3 ray = normalize(input.vDir);

    // Vertical gradient: horizon (ray.z≈0) → top overhead (ray.z≈1); below the horizon keeps the
    // horizon color (no separate ground plane).
    float3 sky = lerp(uSkyHorizon.rgb, uSkyTopSkyEnabled.rgb, saturate(ray.z));

    // `horizon` fades the sky textures into the gradient at the skyline and gates the below-horizon
    // hemisphere out (and keeps the cloud-plane divide below in its safe range).
    float horizon = saturate(ray.z / 0.06);
    if (horizon > 0.0)
    {
        // Lat-long sky UV for the STARS: u = azimuth (wraps), v = horizon(0)→zenith(1). The mesh v is
        // 0.5 at the horizon and 1 at the zenith, so remap to 0..1 across the visible sky.
        float2 skyUv = float2(input.vUv.x, saturate((input.vUv.y - 0.5) * 2.0));

        // Stars FIRST (behind clouds): additive, night-faded — fixed celestial dome.
        if (uSkyTexIndices.y != 0xFFFFFFFFu && uStarTintFade.a > 0.001)
        {
            float2 starUv = float2(skyUv.x * uCloudScroll.w, skyUv.y);
            float3 star = textures[NonUniformResourceIndex(uSkyTexIndices.y)].Sample(sSky, starUv).rgb;
            sky += star * uStarTintFade.rgb * (uStarTintFade.a * horizon);
        }

        // Clouds OVER on a FLAT overhead plane (gnomonic projection ray.xy/ray.z): the cloud texture
        // reads as a flat ceiling at infinity that drifts overhead and compresses toward the horizon —
        // the FNV look — instead of wrapping the celestial dome like the stars. max(ray.z, 0.10) bounds
        // the near-horizon UV blow-up (tan θ → ∞); the `horizon` fade above already drives cloud alpha
        // to 0 there, so the residual compression is hidden rather than glitchy. uCloudScroll.z is the
        // planar scale; uCloudScroll.xy the scroll offset.
        if (uSkyTexIndices.x != 0xFFFFFFFFu && uCloudTintOpacity.a > 0.001)
        {
            float2 plane = ray.xy / max(ray.z, 0.10);
            float2 cloudUv = (plane * uCloudScroll.z) + uCloudScroll.xy;
            float4 cloud = textures[NonUniformResourceIndex(uSkyTexIndices.x)].Sample(sSky, cloudUv);
            float a = saturate(cloud.a * uCloudTintOpacity.a * horizon);
            sky = lerp(sky, cloud.rgb * uCloudTintOpacity.rgb, a);
        }
    }

    return float4(sky, 1.0);
}
