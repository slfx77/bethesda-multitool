// BrightPassBlur — the FO3/FNV engine bloom stage (ISBPBLUR3..15 SM3 shaders, decompile-grounded:
// docs/research/fnv_engine_hdr_imagespace.md §1). Bright-pass and blur are FUSED exactly like the
// engine: every tap is thresholded and scaled before weighting, per tap
//   max(src − BrightClamp, 0) · BrightScale
// with a 1D kernel of 2n+1 taps (n = ceil(BlurRadius) clamped 1..7 → the engine's 3..15 tap
// variants). One pass per IMGS BlurPasses, alternating direction per pass (shipped passes = 2
// reads as an H+V separable pair); passes after the first re-threshold already-thresholded data,
// matching the engine's single-shader chain.
//
// Labeled viewer approximations (engine-faithful otherwise):
//   - The bloom target is ¼×¼ scene resolution and pass 0 bilinear-samples the full-res scene
//     directly — standing in for one explicit DownSample16 (¼×¼ box) step of the engine chain.
//   - Tap weights use a normalized Gaussian (σ = n/2); the engine's CPU-side weight fill is not
//     decompiled yet. Revisit if the weight table is recovered.
//
// The engine's BPBLUR also writes sum(adaptedAvgColor.rgb) into out.a for the composite; our
// composite reads the adapted 1×1 average at t1 directly, so alpha here is unused (kept 1).

Texture2D    uSource  : register(t0);
SamplerState uSampler : register(s0);

cbuffer BloomParams : register(b0)
{
    float4 uBloom0; // x = BrightClamp, y = BrightScale, z = tap radius n (1..7), w = unused
    float4 uBloom1; // xy = dest texel size, zw = blur direction (1,0) or (0,1)
    float4 uBloom2; // reserved
    float4 uBloom3; // reserved
};

struct PSInput
{
    float4 Position : SV_Position;
    float2 vUv      : TEXCOORD0;
};

float4 main(PSInput input) : SV_Target
{
    int n = (int)uBloom0.z;
    float sigma = max(uBloom0.z * 0.5, 0.5);
    float2 step = uBloom1.xy * uBloom1.zw;

    float3 sum = 0.0;
    float weightSum = 0.0;
    [loop]
    for (int i = -n; i <= n; i++)
    {
        float w = exp(-(i * i) / (2.0 * sigma * sigma));
        float3 tap = uSource.SampleLevel(uSampler, input.vUv + i * step, 0).rgb;
        sum += w * max(tap - uBloom0.x, 0.0) * uBloom0.y;
        weightSum += w;
    }

    return float4(sum / weightSum, 1.0);
}
