// Tonemap resolve pass. Samples the HDR scene color (R16G16B16A16_FLOAT, values may exceed 1 —
// emissive glow, sun specular) and maps it to the 8-bit display range.
//
// Five operators (uParams0.z = mode):
//   0 LegacyClamp — plain saturate in this composite. Morrowind and the static
//     FALLOUT_VIEWER_HDR=0 8-bit scene-target kill-switch land here.
//   1 GammaAces — decode 2.2 -> exposure -> ACES filmic -> encode 1/2.2. The scene renders in
//     gamma space (engine-faithful for these D3D9-era games; no sRGB SRVs anywhere), so the
//     curve must run in display-linear and re-encode — running ACES directly on gamma values
//     lifted midtones and desaturated ("washed out"). Stand-in for Skyrim/FO4/FO76 until their
//     imagespace stage is ported.
//   2 EngineFo3Fnv — the FO3/FNV engine HDR stage, decompile-grounded from the shipped ISHDR*
//     SM3 shaders + ImageSpaceEffectHDR (docs/research/fnv_engine_hdr_imagespace.md):
//       L = sum(adaptedAvgSceneColor.rgb)   (avg pass below; steady-state = fully adapted eye)
//       denom = max(L, TargetLUM)                     // eye-adapt exposure, never brightens
//       c = scene * (TargetLUM/denom) + bloom * (0.5/denom)   // ISHDRBLENDINSHADER composite
//       cinematic: saturation -> tint -> contrast/brightness (IMGS cinematic block)
//     bloom (t2) = the BrightPassBlur chain output (bloom.frag.hlsl); uParams3.z gates the term.
//     Operates on gamma-space values exactly like the engine — no decode/encode by design.
//   3 CreationModern — default-off evidence-backed increment. FO4 auto exposure (authored
//     min/max/middle-gray), cinematic, tint and scene sunlight/sky scales are active. Tonemap-E,
//     Skyrim White/EyeAdaptStrength, LUT grading and modern bloom topology remain neutral until
//     their shader/resource oracles are recovered.
//   4 CinematicFo3Fnv — standalone non-HDR FO3/FNV cinematic grade over a clamped LDR scene;
//     no exposure, adapted-average sample, or bloom.
//   5 ClassicSdrBloom — the classic launcher's middle "Bloom" state: SDR clamp + bright-pass
//     bloom at neutral exposure + classic grade. Stand-in for the unrecovered LDR [BlurShader].
//
// mainAdapt consumes the classic recursive DownSample16 chain's 1x1 result and applies ADAPT.
// mainAvg retains the sparse-grid average only for the default-off modern path, whose reduction
// topology has not been recovered.

Texture2D    uHdr    : register(t0);
Texture2D    uAvgLum : register(t1);
Texture2D    uBloom  : register(t2);
SamplerState uSampler : register(s0);

cbuffer TonemapParams : register(b0)
{
    float4 uParams0; // x = exposure, y = enabled (>=0.5), z = mode, w = TargetLUM
    float4 uParams1; // x = Saturation, y = ContrastAvgLum, z = Contrast, w = Brightness
    float4 uParams2; // xyz = Tint color, w = TintAmount
    float4 uParams3; // x = UpperLUMClamp, y = AdaptFactor(current weight), z = bloom, w = retained cinematic flags (classic shader does not consume)
    float4 uParams4; // modern: x/y exposure min/max, z middle gray, w retained Tonemap-E
    float4 uParams5; // modern: x white, y eye strength, z receive-bloom threshold, w family (0 Skyrim/1 FO4)
};

// ACES filmic tonemap (Krzysztof Narkowicz's fitted approximation of the ACES RRT+ODT).
float3 AcesFilmic(float3 x)
{
    const float a = 2.51;
    const float b = 0.03;
    const float c = 2.43;
    const float d = 0.59;
    const float e = 0.14;
    return saturate((x * (a * x + b)) / (x * (c * x + d) + e));
}

struct PSInput
{
    float4 Position : SV_Position;
    float2 vUv      : TEXCOORD0;
};

float3 ApplyClassicCinematic(float3 c)
{
    // Both shipped FO3/FNV cinematic shaders execute every authored term; the retained operation
    // mask is manager metadata and is not bound/read by either pixel shader.
    float luma = dot(c, float3(0.299, 0.587, 0.114));
    c = lerp(luma.xxx, c, uParams1.x);
    c = lerp(c, luma * uParams2.xyz, uParams2.w);
    return uParams1.z * (uParams1.w * c - uParams1.y) + uParams1.y;
}

float4 main(PSInput input) : SV_Target
{
    float4 hdr = uHdr.Sample(uSampler, input.vUv);

    // OFF / legacy: composite passthrough. Only the static infrastructure kill-switch also restores
    // the old 8-bit scene target and its per-sample clamp timing.
    if (uParams0.y < 0.5 || uParams0.z < 0.5)
    {
        return float4(saturate(hdr.rgb), hdr.a);
    }

    if (uParams0.z < 1.5)
    {
        // GammaAces: the curve expects linear light; the scene is gamma-encoded.
        float3 lin = pow(max(hdr.rgb, 0.0), 2.2);
        lin = AcesFilmic(lin * uParams0.x);
        return float4(pow(lin, 1.0 / 2.2), hdr.a);
    }

    // ClassicSdrBloom (mode 5) — the classic launcher's middle "Bloom" state: SDR clamp at
    // NEUTRAL exposure + the bright-pass bloom term + the classic grade (neutral for Oblivion).
    // Retail's LDR [BlurShader] topology is unrecovered; this stand-in reuses the recovered
    // bright-pass chain over the clamped scene with the engine composite's 0.5 bloom weight and
    // denom = 1 (no eye-adapt scaling — uAvgLum is deliberately never sampled here). Placed
    // before the mode-4 window so its extraction stays clean, and before the >=2.5 modern range
    // that would otherwise swallow mode 5.
    if (uParams0.z >= 4.5)
    {
        float3 sdrBloom = uParams3.z * uBloom.Sample(uSampler, input.vUv).rgb;
        float3 sdr = saturate(hdr.rgb) * uParams0.x + sdrBloom * 0.5;
        return float4(saturate(ApplyClassicCinematic(sdr)), hdr.a);
    }

    // ImageSpaceEffectCinematic is the FO3/FNV alternate selected when engine HDR is off. Its
    // source is the already-clamped LDR scene, and it does not sample the HDR exposure/bloom inputs.
    // Keep this branch before the numeric CreationModern dispatch: mode 4 would otherwise be
    // swallowed by the >=2.5 modern range.
    if (uParams0.z >= 3.5 && uParams0.z < 4.5)
    {
        return float4(saturate(ApplyClassicCinematic(saturate(hdr.rgb))), hdr.a);
    }

    if (uParams0.z >= 2.5)
    {
        float3 adapted = uAvgLum.Sample(uSampler, float2(0.5, 0.5)).rgb;
        float exposure = 1.0;
        if (uParams5.w > 0.5) // FO4 family; Skyrim exposure equation is not recovered.
        {
            float adaptedLuma = dot(adapted, float3(0.2126, 0.7152, 0.0722));
            float lo = min(uParams4.x, uParams4.y);
            float hi = max(uParams4.x, uParams4.y);
            exposure = clamp(uParams4.z / max(adaptedLuma, 1e-6), lo, hi);
        }
        float3 c = max(hdr.rgb, 0.0) * exposure * uParams0.x;
        return float4(saturate(ApplyClassicCinematic(c)), hdr.a);
    }

    // EngineFo3Fnv — ISHDRBLENDINSHADER[CIN] on gamma-space values.
    float3 adapted = uAvgLum.Sample(uSampler, float2(0.5, 0.5)).rgb;
    float4 bloomSample = uBloom.Sample(uSampler, input.vUv);
    // The active recovered path carries the adapted sum through the FP16 bloom alpha exactly like
    // BPBLUR. Bloom-off is a viewer diagnostic and falls back to the adapted RGB texture directly.
    float lum = uParams3.z > 0.5
        ? bloomSample.a
        : adapted.r + adapted.g + adapted.b;
    float denom = max(lum, uParams0.w);
    float3 bloom = uParams3.z * bloomSample.rgb;
    float3 c = (hdr.rgb * (uParams0.w / denom) + bloom * (0.5 / denom)) * uParams0.x;

    return float4(saturate(ApplyClassicCinematic(c)), hdr.a);
}

// ADAPT temporally blends against the PREVIOUS adapted average (t1, the other ping-pong 1x1 target),
// then applies the recovered vector-length upper clamp. uParams3.y is this frame's CURRENT weight;
// a value greater than one marks a reset frame whose previous texture must not be sampled.
float4 ApplyAdapt(float3 averageColor)
{
    float3 adapted = averageColor;
    if (uParams3.y <= 1.0)
    {
        float3 previous = uAvgLum.SampleLevel(uSampler, float2(0.5, 0.5), 0).rgb;
        adapted = lerp(previous, adapted, saturate(uParams3.y));
    }

    // ISHDRADAPT does not raise vectors shorter than 0.01. Both numerator and denominator use the
    // same 0.01 floor, so the low-length scale is one unless UpperLUMClamp is itself lower.
    float len = length(adapted);
    float safeLen = max(len, 0.01);
    float clamped = min(safeLen, uParams3.x);
    adapted *= clamped / safeLen;
    return float4(adapted, 1.0);
}

// Classic path: t0 is the final 1x1 result of the recursive /4 DownSample16 chain.
float4 mainAdapt(PSInput input) : SV_Target
{
    float3 averageColor = uHdr.SampleLevel(uSampler, float2(0.5, 0.5), 0).rgb;
    return ApplyAdapt(averageColor);
}

// Modern stand-in only: sparse 16x16 grid mean until the Creation-era reduction is recovered.
float4 mainAvg(PSInput input) : SV_Target
{
    const int GridSize = 16;
    float3 sum = 0.0;
    [loop]
    for (int y = 0; y < GridSize; y++)
    {
        [loop]
        for (int x = 0; x < GridSize; x++)
        {
            float2 uv = (float2(x, y) + 0.5) / GridSize;
            sum += uHdr.SampleLevel(uSampler, uv, 0).rgb;
        }
    }

    return ApplyAdapt(sum / (GridSize * GridSize));
}
