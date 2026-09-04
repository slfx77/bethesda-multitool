// Recovered D3D9-era bloom topology. FNV and installed Oldrim 1.9.32 independently prove the same
// fused two-pass blur topology and all seven IEEE-754 kernel rows; Skyrim Special Edition is unverified:
//   HDR scene -> recursive DownSample16 chain to 1x1 for ADAPT; retain the first /4 level for a
//   vertical BrightPassBlur draw -> horizontal plain-blur draw -> composite.
// TES4 instead runs a separate BrightPass once, then cumulatively ping-pongs two plain blur axes
// abs(iNumBlurpasses) times. BlurPasses remains retained-only for the later fused shader chain.
// Bright-pass is applied per BPBLUR tap before its weight:
//   max(src - BrightClamp, 0) * BrightScale.
//
// ImageSpaceEffectBlur::UpdateParams uploads the same compact 3, 5, ... 15-tap offset/weight row to
// both shaders, but makes it one-dimensional through BlurScale: (0, 1/height) for BPBLUR first and
// (1/width, 0) for BLUR second. The two rows form the recovered separable 2-D Gaussian. Feeding both
// scale axes in one draw is not equivalent; it creates the conspicuous diagonal light streaks.

Texture2D    uSource  : register(t0);
Texture2D    uAvgLum  : register(t1);
SamplerState uSampler : register(s0);
SamplerState uPointSampler : register(s1);

cbuffer BloomParams : register(b0)
{
    float4 uBloom0; // x = BrightClamp, y = BrightScale, z = BPBLUR radius (1..7), w = unused
    float4 uBloom1; // xy = BlurScale (one axis is always zero), zw = unused
    float4 uBloom2;
    float4 uBloom3;
};

struct PSInput
{
    float4 Position : SV_Position;
    float2 vUv      : TEXCOORD0;
};

// Bit-exact weights recovered from the seven ImageSpaceEffectBlur tables in the retail FNV XEX and
// independently matched against Oldrim's aBlurWeights initializer. Keeping the original IEEE-754
// payloads also avoids depending on a shader compiler's exp result.
float ClassicBrightPassWeight(int radius, int distance)
{
    if (radius == 1)
    {
        return distance == 0 ? asfloat(0x3F4977E6u) : asfloat(0x3DDA2032u);
    }

    if (radius == 2)
    {
        if (distance == 0) return asfloat(0x3ECE2433u);
        if (distance == 1) return asfloat(0x3E7A0FF4u);
        return asfloat(0x3D5F2F68u);
    }

    if (radius == 3)
    {
        if (distance == 0) return asfloat(0x3E8A96DAu);
        if (distance == 1) return asfloat(0x3E5DF275u);
        if (distance == 2) return asfloat(0x3DE3E720u);
        return asfloat(0x3D160C3Eu);
    }

    if (radius == 4)
    {
        if (distance == 0) return asfloat(0x3E511048u);
        if (distance == 1) return asfloat(0x3E387F7Du);
        if (distance == 2) return asfloat(0x3DFD9B6Bu);
        if (distance == 3) return asfloat(0x3D87BEEEu);
        return asfloat(0x3CE25956u);
    }

    if (radius == 5)
    {
        if (distance == 0) return asfloat(0x3E27E706u);
        if (distance == 1) return asfloat(0x3E1AFE51u);
        if (distance == 2) return asfloat(0x3DF3D829u);
        if (distance == 3) return asfloat(0x3DA37425u);
        if (distance == 4) return asfloat(0x3D3ABB7Cu);
        return asfloat(0x3CB5C8DBu);
    }

    if (radius == 6)
    {
        if (distance == 0) return asfloat(0x3E0C4FB5u);
        if (distance == 1) return asfloat(0x3E04BA92u);
        if (distance == 2) return asfloat(0x3DE0B47Au);
        if (distance == 3) return asfloat(0x3DAA34D5u);
        if (distance == 4) return asfloat(0x3D66BC17u);
        if (distance == 5) return asfloat(0x3D0BF29Au);
        return asfloat(0x3C97E98Cu);
    }

    if (distance == 0) return asfloat(0x3DF10A7Fu);
    if (distance == 1) return asfloat(0x3DE7668Bu);
    if (distance == 2) return asfloat(0x3DCCBB66u);
    if (distance == 3) return asfloat(0x3DA6F002u);
    if (distance == 4) return asfloat(0x3D7AE64Au);
    if (distance == 5) return asfloat(0x3D2DC3F7u);
    if (distance == 6) return asfloat(0x3CDDD244u);
    return asfloat(0x3C827C32u);
}

float4 mainDownsample16(PSInput input) : SV_Target
{
    // The shipped path uses four authored +/-1 offsets with a linear sampler. Each fetch averages a
    // 2x2 neighborhood, producing the effective 4x4 / 16-texel box with the retail interpolation and
    // odd-dimension behavior (not sixteen independent point-center fetches).
    float2 texel = uBloom1.xy;
    float3 sum =
        uSource.SampleLevel(uSampler, input.vUv + float2(-1.0, -1.0) * texel, 0).rgb +
        uSource.SampleLevel(uSampler, input.vUv + float2( 1.0, -1.0) * texel, 0).rgb +
        uSource.SampleLevel(uSampler, input.vUv + float2( 1.0,  1.0) * texel, 0).rgb +
        uSource.SampleLevel(uSampler, input.vUv + float2(-1.0,  1.0) * texel, 0).rgb;
    return float4(sum * 0.25, 1.0);
}

// Skyrim ImageSpaceEffectHDR slot 5 when BSShaderManager::bFPFiltering is true. The recovered
// fDownSample1024 table is {(-1,-1),(-1,+1),(+1,-1),(+1,+1)}, with weight 0x3E800000.
// Unlike the RGB slot-4 pass, this stage projects each decoded fetch to retail luminance.
float4 mainSkyrimLuminance4(PSInput input) : SV_Target
{
    const float3 SkyrimLuma = float3(0.2125, 0.7154, 0.0721);
    float weight = asfloat(0x3E800000u);
    float2 texel = uBloom1.xy;
    float luminance =
        dot(uSource.SampleLevel(uSampler, input.vUv + float2(-1.0,  1.0) * texel, 0).rgb, SkyrimLuma) * weight;
    luminance +=
        dot(uSource.SampleLevel(uSampler, input.vUv + float2(-1.0, -1.0) * texel, 0).rgb, SkyrimLuma) * weight;
    luminance +=
        dot(uSource.SampleLevel(uSampler, input.vUv + float2( 1.0, -1.0) * texel, 0).rgb, SkyrimLuma) * weight;
    luminance +=
        dot(uSource.SampleLevel(uSampler, input.vUv + float2( 1.0,  1.0) * texel, 0).rgb, SkyrimLuma) * weight;
    return float4(luminance.xxx, 1.0);
}

// Skyrim slot 6: the fDownSample1024NoFiltering table is the row-major 4x4 grid whose axes are
// {-1.5,-0.5,+0.5,+1.5}; every exact table weight is 0x3D800000 (1/16). Retail binds
// FILTER_NEAREST for this slot (unlike filtered slots 5 and 9). That distinction is observable on
// the odd ceil(/4) stages, whose output UVs do not map every half-offset to an exact source center.
float SkyrimDownsample16Scalar(float2 uv, float2 texel)
{
    float weight = asfloat(0x3D800000u);
    float luminance = 0.0;
    [unroll]
    for (int y = 0; y < 4; y++)
    {
        [unroll]
        for (int x = 0; x < 4; x++)
        {
            float2 offset = float2((float)x - 1.5, (float)y - 1.5);
            luminance += uSource.SampleLevel(uPointSampler, uv + offset * texel, 0).r * weight;
        }
    }
    return luminance;
}

float4 mainSkyrimDownsample16(PSInput input) : SV_Target
{
    float luminance = SkyrimDownsample16Scalar(input.vUv, uBloom1.xy);
    return float4(luminance.xxx, 1.0);
}

float4 main(PSInput input) : SV_Target
{
    int radius = clamp((int)uBloom0.z, 1, 7);
    float3 sum = 0.0;

    [loop]
    for (int tapIndex = -7; tapIndex <= 7; tapIndex++)
    {
        if (abs(tapIndex) <= radius)
        {
            float weight = ClassicBrightPassWeight(radius, abs(tapIndex));
            float2 offset = tapIndex * uBloom1.xy;
            float3 tap = uSource.SampleLevel(uSampler, input.vUv + offset, 0).rgb;
            sum += weight * max(tap - uBloom0.x, 0.0) * uBloom0.y;
        }
    }

    // The recovered shader consumes its authored weights directly; it does not renormalize them.
    // It routes the adapted RGB sum through bloom alpha for ISHDRBLENDINSHADER.
    float3 adapted = uAvgLum.SampleLevel(uSampler, float2(0.5, 0.5), 0).rgb;
    return float4(sum, adapted.r + adapted.g + adapted.b);
}

// TES4 HDR005: bright extraction is its own draw before the cumulative blur loop. Shipped programs
// write one to alpha; the final HDR004 composite reads adapted RGB through a separate sampler.
float4 mainTes4BrightPass(PSInput input) : SV_Target
{
    float3 source = uSource.SampleLevel(uSampler, input.vUv, 0).rgb;
    return float4(max(source - uBloom0.x, 0.0) * uBloom0.y, 1.0);
}

float3 ClassicBlurRgb(PSInput input, out float lastAlpha)
{
    int radius = clamp((int)uBloom0.z, 1, 7);
    float3 sum = 0.0;
    lastAlpha = 0.0;

    [loop]
    for (int tapIndex = -7; tapIndex <= 7; tapIndex++)
    {
        if (abs(tapIndex) <= radius)
        {
            float weight = ClassicBrightPassWeight(radius, abs(tapIndex));
            float2 offset = tapIndex * uBloom1.xy;
            float4 tap = uSource.SampleLevel(uSampler, input.vUv + offset, 0);
            sum += weight * tap.rgb;
            lastAlpha = tap.a;
        }
    }

    return sum;
}

float4 mainBlur(PSInput input) : SV_Target
{
    float lastAlpha;
    float3 sum = ClassicBlurRgb(input, lastAlpha);

    // ISBLUR3..15 filters RGB only. Its output alpha is the final (+radius) sample's alpha; the
    // preceding BPBLUR made that alpha spatially constant, so the adapted RGB sum is preserved.
    return float4(sum, lastAlpha);
}

float4 mainTes4Blur(PSInput input) : SV_Target
{
    float ignoredAlpha;
    float3 sum = ClassicBlurRgb(input, ignoredAlpha);
    // TES4 HDR000/1/2 write one to alpha on every axis draw.
    return float4(sum, 1.0);
}
