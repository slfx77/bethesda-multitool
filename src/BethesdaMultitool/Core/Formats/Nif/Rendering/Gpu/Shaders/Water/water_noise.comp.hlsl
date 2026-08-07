// Fallout 3 / Fallout: New Vegas water-noise prepass, recovered from the retail PC
// shader package:
//   ISNOISESCROLLANDBLEND.pso -> mainScrollBlend
//   ISNOISENORMALMAP.pso      -> mainNormal
//
// The shipping pipeline renders a 256x256 scalar height tile, then reconstructs a
// normal tile with a fixed one-texel Sobel stencil. WATER000 samples the finished
// normal once. Keeping these as two explicit compute passes preserves that topology
// without disturbing the scene render targets currently bound by the graphics pass.

cbuffer WaterNoiseFrame : register(b0)
{
    uint  uSourceIndex;
    uint  uBlendIndex;
    float uNoiseScale;
    float uPadding0;
    float4 uTexScaleAmplitude; // xyz = max(1, ceil(fHeightUVScale * .01)); w unused
    float4 uScroll0;           // xy = accumulated scroll; zw unused
    float4 uScroll1;
    float4 uScroll2;
    float4 uAmplitude;         // xyz = fAmplitude[0..2]; w unused
};

Texture2D<float4> gWaterNoiseTextures[] : register(t0, space1);
RWTexture2D<float4> gWaterNoiseOutput : register(u0);
SamplerState gWaterNoiseSampler : register(s0);

static const uint kNoiseDimension = 256;

[numthreads(8, 8, 1)]
void mainScrollBlend(uint3 dispatchId : SV_DispatchThreadID)
{
    if (dispatchId.x >= kNoiseDimension || dispatchId.y >= kNoiseDimension)
    {
        return;
    }

    // The image-space VS supplies normalized texcoords at pixel centers. Each layer samples a
    // DIFFERENT source channel: layer 0 -> B, layer 1 -> G, layer 2 -> R. The PS unpacks each
    // scalar to [-1,1], applies the authored amplitude, then repacks the sum to [0,1].
    float2 uv = (float2(dispatchId.xy) + 0.5) * (1.0 / 256.0);
    uint sourceIndex = NonUniformResourceIndex(uSourceIndex);
    float layer0 = gWaterNoiseTextures[sourceIndex].SampleLevel(gWaterNoiseSampler,
        uv * uTexScaleAmplitude.x + uScroll0.xy, 0).b * 2.0 - 1.0;
    float layer1 = gWaterNoiseTextures[sourceIndex].SampleLevel(gWaterNoiseSampler,
        uv * uTexScaleAmplitude.y + uScroll1.xy, 0).g * 2.0 - 1.0;
    float layer2 = gWaterNoiseTextures[sourceIndex].SampleLevel(gWaterNoiseSampler,
        uv * uTexScaleAmplitude.z + uScroll2.xy, 0).r * 2.0 - 1.0;
    float height = (layer0 * uAmplitude.x + layer1 * uAmplitude.y + layer2 * uAmplitude.z)
                 * 0.5 + 0.5;
    gWaterNoiseOutput[dispatchId.xy] = float4(height, 0.0, 0.0, 1.0);
}

[numthreads(8, 8, 1)]
void mainNormal(uint3 dispatchId : SV_DispatchThreadID)
{
    if (dispatchId.x >= kNoiseDimension || dispatchId.y >= kNoiseDimension)
    {
        return;
    }

    // ISNOISENORMALMAP uses a literal 1/256 offset and absolute scalar samples. Written out from
    // the ps_2_1 register flow, this is the standard 3x3 Sobel pair below (with the engine's signs),
    // scaled by DNAM fNoiseScale and normalized with z=1 before repacking to [0,1].
    float2 uv = (float2(dispatchId.xy) + 0.5) * (1.0 / 256.0);
    const float d = 1.0 / 256.0;
    uint blendIndex = NonUniformResourceIndex(uBlendIndex);

    float w  = abs(gWaterNoiseTextures[blendIndex].SampleLevel(gWaterNoiseSampler, uv + float2(-d,  0), 0).r);
    float e  = abs(gWaterNoiseTextures[blendIndex].SampleLevel(gWaterNoiseSampler, uv + float2( d,  0), 0).r);
    float n  = abs(gWaterNoiseTextures[blendIndex].SampleLevel(gWaterNoiseSampler, uv + float2( 0,  d), 0).r);
    float s  = abs(gWaterNoiseTextures[blendIndex].SampleLevel(gWaterNoiseSampler, uv + float2( 0, -d), 0).r);
    float nw = abs(gWaterNoiseTextures[blendIndex].SampleLevel(gWaterNoiseSampler, uv + float2(-d,  d), 0).r);
    float ne = abs(gWaterNoiseTextures[blendIndex].SampleLevel(gWaterNoiseSampler, uv + float2( d,  d), 0).r);
    float sw = abs(gWaterNoiseTextures[blendIndex].SampleLevel(gWaterNoiseSampler, uv + float2(-d, -d), 0).r);
    float se = abs(gWaterNoiseTextures[blendIndex].SampleLevel(gWaterNoiseSampler, uv + float2( d, -d), 0).r);
    float centerGreen = abs(gWaterNoiseTextures[blendIndex].SampleLevel(gWaterNoiseSampler, uv, 0).g);

    float3 normal = normalize(float3(
        (2.0 * w + nw + sw - 2.0 * e - ne - se) * uNoiseScale,
        (2.0 * n + nw + ne - 2.0 * s - sw - se) * uNoiseScale,
        1.0));
    // The retail pass forwards abs(center.g) as alpha. The preceding blend pass writes G=0,
    // but preserving the instruction keeps this entry point independently faithful.
    gWaterNoiseOutput[dispatchId.xy] = float4(normal * 0.5 + 0.5, centerGreen);
}

// Builds one mip level of the finished normal tile. The authored NNAM DDS the retail shader
// samples ships WITH a mip chain; regenerating only mip 0 every frame and sampling it mipless
// at the macro tile makes each grazing-view pixel an uncorrelated per-frame draw across many
// texels — visible as water glint shimmer at a static camera.
//
// One dispatch per level: uSourceIndex = a single-mip SRV over the PREVIOUS level, u0 = this
// level's UAV, uPadding0 = this level's texel dimension. Normals are decoded, 2x2 box-averaged
// and RE-NORMALIZED — averaging the packed bytes would shorten the vector and read as flattened
// rather than filtered ripples. z keeps its positive bias, so the average cannot degenerate.
[numthreads(8, 8, 1)]
void mainDownsample(uint3 dispatchId : SV_DispatchThreadID)
{
    uint destDimension = (uint)uPadding0;
    if (dispatchId.x >= destDimension || dispatchId.y >= destDimension)
    {
        return;
    }

    uint sourceIndex = NonUniformResourceIndex(uSourceIndex);
    uint2 src = dispatchId.xy * 2;
    float4 s00 = gWaterNoiseTextures[sourceIndex][src];
    float4 s10 = gWaterNoiseTextures[sourceIndex][src + uint2(1, 0)];
    float4 s01 = gWaterNoiseTextures[sourceIndex][src + uint2(0, 1)];
    float4 s11 = gWaterNoiseTextures[sourceIndex][src + uint2(1, 1)];

    float3 average = (s00.xyz + s10.xyz + s01.xyz + s11.xyz) * 0.25 * 2.0 - 1.0;
    float alpha = (s00.w + s10.w + s01.w + s11.w) * 0.25;
    gWaterNoiseOutput[dispatchId.xy] = float4(normalize(average) * 0.5 + 0.5, alpha);
}
