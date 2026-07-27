// Shared plumbing for the per-game water pixel shaders (water_fnv / water_oblivion / water_fo4 /
// water_morrowind / water_fnv001 .frag.hlsl): the full-superset Uniforms cbuffer (b0) — every
// variant binds the same CPU-side layout, so member order and registers are append-only and
// shared verbatim — plus the bindless texture/sampler declarations, the PSInput contract, the
// static sun fallback, and the noise/depth helpers each variant taps.
// FO4_WATER_ARCHITECTURAL-only resources and helpers live in water_fo4.frag.hlsl.
#ifndef WATER_COMMON_HLSLI
#define WATER_COMMON_HLSLI

cbuffer Uniforms : register(b0)
{
    float4x4 uViewProj;
    float4 uShallow;     // rgb in 0..1   (DNAM ShallowColor / FNV c2)
    float4 uDeep;        // rgb in 0..1   (DNAM DeepColor    / FNV c3)
    float4 uReflection;  // rgb in 0..1   (DNAM ReflectionColor / FNV c4)
    float4 uCamPosTime;  // xyz = camera world pos (FNV EyePos c1), w = elapsed seconds
    uint4 uNoiseParams;  // x = NNAM bindless index (0xFFFFFFFF = none), y = world units/tile,
                         // z = fNoiseScale bits, w = WATR ANAM opacity/100 bits (Oblivion VarAmounts.z)
    float4 uSurface0;    // NormalsUvScale, FresnelAmount(=F0), ReflectivityAmount, Shininess(spec exp)
    float4 uSurface1;    // SunPower, DepthFalloffStart, DepthFalloffEnd, w = lava flag (1 = emissive lava)
    float4 uLayer1;      // per noise layer: UvScale, WindDirDeg, WindSpeed, AmpScale
    float4 uLayer2;
    float4 uLayer3;
    uint4 uDepthParams;  // x = scene-depth SRV bindless index (0xFFFFFFFF = none), y/z = near/far bits,
                         // w = depth-occlusion tie-break bias (world units, asfloat) — water wins coplanar ties
    float4 uRenderOrigin; // xyz = camera-relative render origin; w = scene-depth sample count
    // ---- FO4_WATER-only constants (appended at the end so every other variant's offsets are
    // untouched; sourced from the FO4 WATR DNAM — see WaterShaderVariant.Fo4Water). ----
    float4 uFo4Spec;     // x = Sun Specular Magnitude, y = Silt Amount, z = Shallow Alpha, w = Deep Alpha
    float4 uFo4Ranges;   // x/y = Color Shallow/Deep Range, z/w = Alpha Shallow/Deep Range
                         //       (multipliers of Depth Amount; retail authors them ≈1.0)
    float4 uFo4DarkSilt; // rgb = DNAM silt Dark Color (the FO4 PS's unshadowed ambient add),
                         // w = DNAM Depth Amount (world-unit column at which the depth ramps saturate)
    uint4 uNormalIndices; // ordered normal sources; Oblivion repurposes y as WATR TNAM DetailMap
    float4 uLegacySurface0; // Oblivion: WaveAmplitude, WaveFrequency, ScrollXSpeed, ScrollYSpeed
    float4 uLegacySurface1; // Oblivion: FogNear, FogFar, TextureBlend, WindVelocity
    // WATER-07 opt-in FO4/FO76 architecture (FO4_WATER_ARCHITECTURAL only).
    uint4 uModernIndices;   // body/coverage, composited normal, gloss/flow, depth LUT
    uint4 uModernTechnique; // x = recovered technique ID, y = TextureCube index, z = point-light cap
    float4 uModernParams;   // glossScaleA/B, neutral outputAlpha, neutral alphaTestThreshold
    float4 uModernLightSilt;// retained LightSilt rgb, normal magnitude in w
    // Bounded FNV WATER001 tail. Appended after the established 416-byte prefix so every existing
    // WATER003/Oblivion/FO4/Morrowind constant retains its exact register.
    uint4 uFnvWater001Snapshot; // x = opaque SceneColor Texture2D index, y/z = dimensions,
                                // w = asuint(horizontal generated-cell plane height)
    float4 uFnvWater001Surface; // UnderwaterFogNear/Far, AboveWaterFogAmount, DistortionAmount
};

// Shared scene atmosphere (b3). CPU mirror: WorldView3DControl.AtmosphereConstants,
// bound once per frame for the whole scene. Water reads the sun dir/color from here so its specular
// and body-lighting track the time-of-day/weather sun like the rest of the scene (P3). When lighting
// is disabled (uSunColorLighting.w == 0) it falls back to the static kSunDir/kSunColor below, so the
// water looks exactly as it did pre-atmosphere.
#include "atmosphere.hlsli"

// Skyrim constant fog: the same powered, FNAM-capped amount blends near→far fog color and then
// surface→fog. Legacy weather binds far=near/max=1.
float3 ApplyFog(float3 color, float3 worldPos)
{
    if (uFogColorFogEnabled.w < 0.5)
    {
        return color;
    }

    // Water renders in absolute world space (its ripple UVs are world-anchored), so the fog distance
    // uses water's OWN absolute camera (uCamPosTime.xyz) rather than the shared atmosphere camera, which
    // is zeroed in camera-relative mode (1G). The fog power (uCameraPosFogPower.w) is position-free.
    float dist = length(worldPos - uCamPosTime.xyz);
    float q = saturate((dist - uAtmosphereParams.y) / max(uAtmosphereParams.z - uAtmosphereParams.y, 1.0));
    float amount = min(pow(q, max(uCameraPosFogPower.w, 0.01)), saturate(uFogFarColorMax.w));
    float3 fogRgb = lerp(uFogColorFogEnabled.rgb, uFogFarColorMax.rgb, amount);
    return lerp(color, fogRgb, amount);
}

// Bindless texture table (slot 4, space1) shared with terrain/references. The NNAM normal map
// lives at uNoiseParams.x (FNV NoiseMap, sampler s2). s0 is the shared anisotropic-wrap sampler.
Texture2D gWaterTextures[] : register(t0, space1);
// space3 aliases the same bindless heap slots for R32_FLOAT Texture2DMS scene-depth descriptors.
// uRenderOrigin.w selects this declaration only when the host supplied sampleCount > 1.
Texture2DMS<float> gWaterDepthTexturesMsaa[] : register(t0, space3);
SamplerState gWaterSampler : register(s0);
// WATER001's opaque-scene snapshot and FO4's generated LUT/cubemap paths both use the root
// signature's clamp sampler. Keep it outside the FO4 guard so the FNV permutation can bind it.
SamplerState gWaterClampSampler : register(s2);

// FO3/FNV's DNAM-driven scroll/blend + normal reconstruction now runs in the explicit
// water_noise.comp.hlsl prepass. This helper remains for Skyrim's independently-authored normal maps
// and for a soft fallback if the prepass cannot reserve transient GPU constants in a saturated frame.
// FNV passes the live scene SunDir (c12) / SunColor (c13). P3 feeds those from the shared b3
// atmosphere CB (uSunDirIntensity / uSunColorLighting) when lighting is enabled, so water tracks the
// time-of-day/weather sun; when lighting is OFF these static constants stand in (the pre-atmosphere
// look — same direction the terrain shader used). P5 additionally tints the reflection with the
// atmosphere sky (uSkyTop/uSkyHorizon) when the skybox is on (see main()).
static const float3 kSunDir = float3(0.40824829, 0.40824829, 0.81649658); // normalize(0.5,0.5,1)
static const float3 kSunColor = float3(1.0, 0.97, 0.9);

struct PSInput
{
    float4 Position  : SV_Position;
    float3 vWorldPos : TEXCOORD0;
};

// Procedural fallback when the worldspace has no NNAM texture (proto/test worlds).
float2 RipplePerturb(float2 p, float t)
{
    const float2 dir1 = float2(0.80, 0.60);
    const float2 dir2 = float2(-0.50, 0.86);
    const float f1 = 0.0040;
    const float f2 = 0.0072;
    float2 grad =
        dir1 * f1 * cos(dot(p, dir1) * f1 + t * 1.3) +
        dir2 * f2 * cos(dot(p, dir2) * f2 - t * 0.9);
    return -grad * 45.0;
}

// One scrolling noise octave -> its raw xyz perturbation in [-1,1]. The FNV NoiseMap
// (genaratednoise01.dds) is a full-range RGB noise, NOT a blue-biased normal map — the engine adds the
// (0,0,1) z-bias in the pixel shader (see main()), so all three channels are the perturbation (engine
// WATER000.pso: `texld r3,v7,s2; mad r3.xyz,r3,2,-1`).
// `layer.x` is consumed by the classic prepass as fTexScale=max(1,ceil(fHeightUVScale*.01)); this
// direct fallback uses its caller-provided world frequency instead. `layer.w` remains the authored
// fAmplitude weight.
float3 SampleNoiseLayer(uint idx, float2 worldXy, float freq, float4 layer, float t)
{
    // layer = (UvScale[displacement fHeightUVScale, NOT used for noise], WindDirDeg, WindSpeed, fAmplitude).
    // Engine ISNOISESCROLLANDBLEND: each layer scrolls the noise in its own UV by WindSpeed·dt along WindDir°,
    // weighted by fAmplitude (UpdateWaterNoise, RE-recovered — no fudge multiplier on the scroll rate).
    float rad = radians(layer.y);
    // Compass-style direction: UpdateWaterNoise accumulates X with sin and Y with cos.
    float2 dir = float2(sin(rad), cos(rad));
    float2 uv = worldXy * freq + dir * (layer.z * t);
    return (gWaterTextures[NonUniformResourceIndex(idx)].Sample(gWaterSampler, uv).xyz * 2.0 - 1.0) * layer.w;
}

// Reversed-Z [1,0] depth -> positive view-space distance (world units). The scene uses reversed-Z
// (CameraState.ReverseZ): z=1 -> near, z=0 -> far. Both the sampled scene depth and this water
// fragment's own SV_Position.z are reversed, so linearizing both with this gives correct distances.
float LinearizeDepth(float ndcZ, float near, float far)
{
    return (near * far) / max(near + ndcZ * (far - near), 1e-4);
}

float LoadSceneDepth(uint depthIndex, int2 pixel, uint suppliedSampleCount)
{
    if (suppliedSampleCount <= 1u)
    {
        return gWaterTextures[NonUniformResourceIndex(depthIndex)].Load(int3(pixel, 0)).r;
    }

    uint depthWidth, depthHeight, descriptorSampleCount;
    gWaterDepthTexturesMsaa[NonUniformResourceIndex(depthIndex)]
        .GetDimensions(depthWidth, depthHeight, descriptorSampleCount);
    float nearestNdc = 0.0;
    [loop]
    for (uint sampleIndex = 0; sampleIndex < descriptorSampleCount; sampleIndex++)
    {
        // Reversed-Z: maximum is the nearest covered opaque sample. This preserves occlusion at an
        // MSAA silhouette instead of allowing water through one uncovered/far sample.
        nearestNdc = max(nearestNdc,
            gWaterDepthTexturesMsaa[NonUniformResourceIndex(depthIndex)].Load(pixel, sampleIndex));
    }
    return nearestNdc;
}

#endif // WATER_COMMON_HLSLI
