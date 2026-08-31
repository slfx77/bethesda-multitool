// Shared sun-shadow cascade sampling (TryCascadeShadow + ShadowFactor). The shadow atlas and the
// legacy point sampler are macro-parameterized so one text serves every bindless table naming
// scheme; the defaults match reference.frag.hlsl / terrain_textured.frag.hlsl. Define SHADOW_ATLAS
// and/or SHADOW_SAMPLER before including to retarget (e.g. water's gWaterTextures table). The
// atlas/legacy-sampler resources must be DECLARED before this include.
#ifndef SHADOW_SAMPLING_HLSLI
#define SHADOW_SAMPLING_HLSLI

#include "atmosphere.hlsli"

#ifndef SHADOW_ATLAS
#define SHADOW_ATLAS textures
#endif
#ifndef SHADOW_SAMPLER
#define SHADOW_SAMPLER sShadowPoint
#endif

// Explicit experiment only: FALLOUT_VIEWER_SHADOW_COMPARISON_PCF=1 makes the production compiler
// define SHADOW_COMPARISON_PCF for pixel shaders. s7 is a separate static comparison sampler, so
// the default s3/GatherRed path and its output stay untouched when the experiment is disabled.
// Keeping the declaration in this shared header also prevents reference/terrain/grass/water from
// drifting onto different registers or sampler types.
#if SHADOW_COMPARISON_PCF
SamplerComparisonState sShadowComparison : register(s7);
#endif

// One cascade attempt for ShadowFactor: returns true when worldPos lands inside this cascade's
// footprint (with a PCF-kernel border margin so the filter never straddles the map edge) —
// 'visibility' then carries the filtered shadow term. The PCF is a 3x3 tent of BILINEAR
// comparison taps: each GatherRed pulls the tap's 2x2 texel quad and the four COMPARE RESULTS are
// blended by the sub-texel fraction (filter the comparisons, never the depths). Ortho projection:
// w == 1, no perspective divide. Reversed-Z: stored depth GROWS toward the light, so "an occluder
// exists" reads as stored > pixelDepth + bias.
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
#if SHADOW_COMPARISON_PCF
    // The legacy sum has separable 1-D texel weights [1-f, 1, 1, f]. Pair the first two and last
    // two weights into one hardware-linear comparison sample each:
    //
    //   (2-f) * lerp(texel[-1], texel[0], 1/(2-f)) == (1-f)*texel[-1] + texel[0]
    //   (1+f) * lerp(texel[ 1], texel[2], f/(1+f)) == texel[1] + f*texel[2]
    //
    // Taking the Cartesian product preserves the legacy 4x4 comparison kernel exactly in ideal
    // arithmetic with FOUR SampleCmpLevelZero operations instead of NINE GatherRed operations.
    // ComparisonFunction.Greater at s7 is the exact reversed-Z predicate below: reference > stored
    // (strict, so equality remains shadowed). The comparison-linear filter blends the resulting
    // zero/one values; it never interpolates depth before comparing.
    float2 lowWeight = 2.0 - f;
    float2 highWeight = 1.0 + f;
    float2 lowUv = gatherBase + (-1.0 + rcp(lowWeight)) * texel;
    float2 highUv = gatherBase + (2.0 - rcp(highWeight)) * texel;

    float4 filtered = float4(
        SHADOW_ATLAS[NonUniformResourceIndex(slot)].SampleCmpLevelZero(
            sShadowComparison, float2(lowUv.x, lowUv.y), reference),
        SHADOW_ATLAS[NonUniformResourceIndex(slot)].SampleCmpLevelZero(
            sShadowComparison, float2(highUv.x, lowUv.y), reference),
        SHADOW_ATLAS[NonUniformResourceIndex(slot)].SampleCmpLevelZero(
            sShadowComparison, float2(lowUv.x, highUv.y), reference),
        SHADOW_ATLAS[NonUniformResourceIndex(slot)].SampleCmpLevelZero(
            sShadowComparison, float2(highUv.x, highUv.y), reference));
    float4 weights = float4(
        lowWeight.x * lowWeight.y,
        highWeight.x * lowWeight.y,
        lowWeight.x * highWeight.y,
        highWeight.x * highWeight.y);
    visibility = dot(filtered, weights) / 9.0;
#else
    float lit = 0.0;
    [unroll]
    for (int dy = -1; dy <= 1; dy++)
    {
        [unroll]
        for (int dx = -1; dx <= 1; dx++)
        {
            float4 quad = SHADOW_ATLAS[NonUniformResourceIndex(slot)]
                .GatherRed(SHADOW_SAMPLER, gatherBase + float2(dx, dy) * texel);
            // Gather quad order (v grows downward): x=(0,+1) y=(+1,+1) z=(+1,0) w=(0,0).
            float4 vis = 1.0 - step(reference.xxxx, quad);
            lit += lerp(lerp(vis.w, vis.z, f.x), lerp(vis.x, vis.y, f.x), f.y);
        }
    }
    visibility = lit / 9.0;
#endif
    return true;
}

// Sun-shadow visibility for an (origin-relative) world position — 1 = fully lit, 0 = fully
// occluded. CASCADED: the smallest (sharpest) cascade containing the sample wins, so quality
// scales with closeness to the camera; the far cascade reaches the full render distance. All
// cascades disabled (params.x == 0) returns 1.0, keeping the scene pixel-identical to the
// pre-shadow renderer.
float ShadowFactor(float3 worldPos)
{
    float visibility;
    if (TryCascadeShadow(uShadowMatrix0, uShadowParams0, worldPos, visibility)) return visibility;
    if (TryCascadeShadow(uShadowMatrix1, uShadowParams1, worldPos, visibility)) return visibility;
    if (TryCascadeShadow(uShadowMatrix2, uShadowParams2, worldPos, visibility)) return visibility;
    if (TryCascadeShadow(uShadowMatrix3, uShadowParams3, worldPos, visibility)) return visibility;
    return 1.0;
}

#endif // SHADOW_SAMPLING_HLSLI
