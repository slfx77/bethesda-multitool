// Tonemap resolve pass. Samples the HDR scene color (R16G16B16A16_FLOAT, values may exceed 1 —
// emissive glow, sun specular, imagespace scales) and maps it to the 8-bit display range with an
// exposure multiply + a filmic curve, preserving hue while rolling highlights off and lifting
// midtones. This is the HDR/imagespace stage the viewer previously skipped (an 8-bit render target
// clipped everything > 1 to flat white and left midtones dark — the "too dark, but neon/goo blow to
// white" signature).
//
// Operator: the ACES filmic approximation (Narkowicz 2015) — a single rational curve with a natural
// toe (lifts shadows) and shoulder (rolls highlights). uParams.x = exposure (linear multiply before
// the curve; 1 = neutral). uParams.y = enable (0 → passthrough clamp, so OFF is bit-identical to the
// old LDR path for regression A/Bs). uParams.z/w reserved for per-game imagespace scale hooks.

Texture2D    uHdr    : register(t0);
SamplerState uSampler : register(s0);

cbuffer TonemapParams : register(b0)
{
    float4 uParams; // x = exposure, y = enabled (>=0.5), zw reserved
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

float4 main(PSInput input) : SV_Target
{
    float4 hdr = uHdr.Sample(uSampler, input.vUv);

    // OFF: passthrough (saturate reproduces the old UNORM clamp exactly) so the toggle is a clean
    // regression control.
    if (uParams.y < 0.5)
    {
        return float4(saturate(hdr.rgb), hdr.a);
    }

    float3 color = hdr.rgb * uParams.x;   // exposure
    color = AcesFilmic(color);            // filmic curve (already saturated inside)
    return float4(color, hdr.a);
}
