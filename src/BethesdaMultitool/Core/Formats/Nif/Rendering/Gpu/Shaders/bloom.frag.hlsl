// FNV recovered bloom topology (shared with the other classic paths pending their binary oracles):
//   HDR scene -> recursive DownSample16 chain to 1x1 for ADAPT; retain the first /4 level for one
//   BrightPassBlur draw -> composite.
// BlurPasses is stored by the data formats but is not a repeated-pass counter in this shader chain.
// Bright-pass is applied per BPBLUR tap before its weight:
//   max(src - BrightClamp, 0) * BrightScale.
//
// The shipped BPBLUR3..15 programs consume a single compact row of 3, 5, ... 15 CPU constants.
// Their recovered tables use the same signed scalar for x and y, so this deliberately samples one
// diagonal row rather than evaluating a square (2r+1)^2 Gaussian.

Texture2D    uSource  : register(t0);
Texture2D    uAvgLum  : register(t1);
SamplerState uSampler : register(s0);

cbuffer BloomParams : register(b0)
{
    float4 uBloom0; // x = BrightClamp, y = BrightScale, z = BPBLUR radius (1..7), w = unused
    float4 uBloom1; // xy = source texel size, zw = unused
    float4 uBloom2;
    float4 uBloom3;
};

struct PSInput
{
    float4 Position : SV_Position;
    float2 vUv      : TEXCOORD0;
};

// Bit-exact weights recovered from the seven ImageSpaceEffectBlur tables in the retail FNV XEX.
// Keeping the original IEEE-754 payloads also avoids depending on a shader compiler's exp result.
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
            float2 offset = float2(tapIndex, tapIndex) * uBloom1.xy;
            float3 tap = uSource.SampleLevel(uSampler, input.vUv + offset, 0).rgb;
            sum += weight * max(tap - uBloom0.x, 0.0) * uBloom0.y;
        }
    }

    // The recovered shader consumes its authored weights directly; it does not renormalize them.
    // It routes the adapted RGB sum through bloom alpha for ISHDRBLENDINSHADER.
    float3 adapted = uAvgLum.SampleLevel(uSampler, float2(0.5, 0.5), 0).rgb;
    return float4(sum, adapted.r + adapted.g + adapted.b);
}
