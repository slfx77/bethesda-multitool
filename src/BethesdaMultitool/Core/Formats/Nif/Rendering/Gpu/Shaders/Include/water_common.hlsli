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
    // Planar sky-reflection target (mirrored-camera sky pass). Appended at the tail; every earlier
    // constant keeps its register. y/z are the SCENE viewport dimensions (not the target's) because
    // the lookup is a normalized screen-space UV — the target may be rendered at any resolution.
    uint4 uWaterReflection;     // x = reflection SceneColor Texture2D index (0xFFFFFFFF = none),
                                // y/z = scene viewport dimensions, w = asuint(UV distortion scale)
    // Appended (matching the C# struct tail): the mirror pass's view-projection (origin-relative)
    // and the scene-content flag (uReflectionParams.x = 1 ⇒ the RT holds mirrored SCENE content —
    // Oblivion's projective WATER007 arm applies; 0 ⇒ sky-only screen-UV semantics).
    float4x4 uReflectionViewProj;
    uint4 uReflectionParams;
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
// Explicitly bounded to the heap's persistent region (16384; depth SRVs are persistent slots):
// the UNBOUNDED `[]` form miscompiled/misresolved on the deployed driver — sampleinfo/ldms
// through the unbounded 2DMS array intermittently returned unrelated low heap slots (the live
// view's depth) instead of the indexed slot, while the space1 unbounded Texture2D array resolved
// the same indices correctly. A bounded declaration emits different (reliable) DXBC indexing.
Texture2DMS<float> gWaterDepthTexturesMsaa[16384] : register(t0, space3);
SamplerState gWaterSampler : register(s0);
// WATER001's opaque-scene snapshot and FO4's generated LUT/cubemap paths both use the root
// signature's clamp sampler. Keep it outside the FO4 guard so the FNV permutation can bind it.
SamplerState gWaterClampSampler : register(s2);

// Planar sky reflection, replacing the 2-row gradient stand-in when a target is bound.
//
// Retail WATER000 samples a planar ReflectionMap RT (sky + scene) and scales it by c5.w
// ReflectivityAmount; a 2-row vertical gradient cannot reproduce the cloud-shaped mottling, warm
// tint or sun glitter that reflection carries, which is why our water read flat and subdued.
//
// The target is the sky drawn with the view's Z axis MIRRORED. Under that mirror the pixel at a
// given screen position holds the sky along reflect(eyeDir, up) — exactly the direction a flat
// water surface reflects there — so the correct lookup is the fragment's OWN screen UV, and no
// per-plane setup is needed: the dome is at infinity, so one target serves every water height.
// The ripple normal perturbs the UV, which is what makes waves visible in the reflection.
float3 SampleSkyReflection(float2 screenPos, float3 N, float3 R)
{
    // 2-row stand-in, retained as the fallback (no target bound, or the sky is disabled).
    float3 gradient = lerp(uSkyHorizon.rgb, uSkyTopSkyEnabled.rgb, saturate(R.z));
    if (uWaterReflection.x == 0xFFFFFFFFu || uWaterReflection.y == 0u || uWaterReflection.z == 0u)
    {
        return gradient;
    }

    float2 uv = screenPos / float2((float)uWaterReflection.y, (float)uWaterReflection.z);
    uv += N.xy * asfloat(uWaterReflection.w);
    // Clamp sampler + saturate: a perturbed UV must never wrap to the opposite edge of the sky.
    return gWaterTextures[NonUniformResourceIndex(uWaterReflection.x)]
        .SampleLevel(gWaterClampSampler, saturate(uv), 0).rgb;
}

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

// Resolves the scene depth under a water fragment, returning the SHADING depth — the surface the
// water is genuinely layered over — and handing back the unfiltered nearest sample in
// `nearestNdc` for the legacy pixel-rate occlusion clip.
//
// The two are not the same thing on an MSAA target, and conflating them is what fringed every mesh
// standing in water. A partially covered silhouette pixel holds two unrelated populations of
// samples: the OCCLUDING MESH (nearer than the water) and the bed the water is over (farther).
// Taking the nearest across all of them handed the mesh's depth to the whole pixel, so
// `column = sceneDist - waterDist` collapsed to ~0 and the fragment shaded as shoreline — pale,
// flat-normalled and over-transparent — in a one-pixel rim around the mesh.
//
// Those occluder samples were never the water's to shade. The hardware GreaterEqual test against
// the host's read-only DSV already rejects the water there, per SAMPLE, which is precisely what
// antialiases the silhouette; the surviving samples are exactly the ones at or behind the water,
// so `waterNdc` (this fragment's own reversed-Z depth) reproduces that same test and the shading
// resolve takes the nearest sample among the survivors. On any pixel with no occluder — every
// pixel that is not on a silhouette — the two results coincide and the shading is bit-unchanged.
//
// The unfiltered value stays available because the pre-hardware-occlusion path genuinely wants the
// occluder: its pixel-rate clip is asking "is anything in front of me?", not "what am I over?".
float LoadSceneDepth(
    uint depthIndex,
    int2 pixel,
    uint suppliedSampleCount,
    float waterNdc,
    out float nearestNdc)
{
    if (suppliedSampleCount <= 1u)
    {
        // Single-sample: no partial coverage, so there is no second population to separate. A lone
        // occluding sample means the hardware rejects this fragment outright and nothing it shades
        // is visible, so the unfiltered value stands — non-MSAA output is unchanged by this split.
        nearestNdc = gWaterTextures[NonUniformResourceIndex(depthIndex)].Load(int3(pixel, 0)).r;
        return nearestNdc;
    }

    // The depth index is wave-UNIFORM (a per-draw CB scalar), so index the MSAA alias directly.
    uint depthWidth, depthHeight, descriptorSampleCount;
    gWaterDepthTexturesMsaa[depthIndex]
        .GetDimensions(depthWidth, depthHeight, descriptorSampleCount);
    nearestNdc = 0.0;
    float nearestBehindNdc = 0.0;
    bool anyBehind = false;
    [loop]
    for (uint sampleIndex = 0; sampleIndex < descriptorSampleCount; sampleIndex++)
    {
        float sampleNdc = gWaterDepthTexturesMsaa[depthIndex].Load(pixel, sampleIndex);
        // Reversed-Z: maximum is the nearest covered opaque sample.
        nearestNdc = max(nearestNdc, sampleNdc);
        // Reversed-Z again: `<= waterNdc` is at or behind the water — the same GreaterEqual the
        // hardware depth test applies, so this is exactly the set of samples the water survives on.
        if (sampleNdc <= waterNdc)
        {
            nearestBehindNdc = max(nearestBehindNdc, sampleNdc);
            anyBehind = true;
        }
    }

    // Every sample occludes: the hardware rejects the fragment entirely, so nothing this returns
    // reaches the target. Return the water's own depth (a zero column) rather than the initial 0.0,
    // which is the reversed-Z FAR plane and would shade the doomed fragment as infinitely deep.
    return anyBehind ? nearestBehindNdc : waterNdc;
}

// The frontmost opaque sample, ignoring the water plane — the resolve as it behaved before the
// shading/occlusion split above. For callers that genuinely want "what is nearest here?" rather
// than "what am I over?": the WATER001 eligibility probe, which is conservative by design and must
// not declare a tap underwater while an occluder covers part of the pixel.
float LoadNearestSceneDepth(uint depthIndex, int2 pixel, uint suppliedSampleCount)
{
    // waterNdc = 1.0 is the reversed-Z near plane, so every sample counts as "behind" and the
    // filtered result degenerates to the unfiltered nearest.
    float nearestNdc;
    LoadSceneDepth(depthIndex, pixel, suppliedSampleCount, 1.0, nearestNdc);
    return nearestNdc;
}

#endif // WATER_COMMON_HLSLI
