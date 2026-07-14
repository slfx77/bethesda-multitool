// Tonemap resolve pass. Samples the HDR scene color (R16G16B16A16_FLOAT, values may exceed 1 —
// emissive glow, sun specular) and maps it to the 8-bit display range.
//
// Three operators (uParams0.z = mode):
//   0 LegacyClamp — plain saturate; bit-identical to the pre-HDR 8-bit pipeline. Morrowind
//     (pre-HDR engine) and the FALLOUT_VIEWER_HDR=0 kill-switch land here.
//   1 GammaAces — decode 2.2 -> exposure -> ACES filmic -> encode 1/2.2. The scene renders in
//     gamma space (engine-faithful for these D3D9-era games; no sRGB SRVs anywhere), so the
//     curve must run in display-linear and re-encode — running ACES directly on gamma values
//     lifted midtones and desaturated ("washed out"). Stand-in for Skyrim/FO4/FO76 until their
//     imagespace stage is ported.
//   2 EngineFo3Fnv — the FO3/FNV engine HDR stage, decompile-grounded from the shipped ISHDR*
//     SM3 shaders + ImageSpaceEffectHDR (docs/research/fnv_engine_hdr_imagespace.md):
//       L = sum(adaptedAvgSceneColor.rgb)   (avg pass below; steady-state = fully adapted eye)
//       exposure = TargetLUM / max(L, TargetLUM)      // darkens toward bright scenes, never brightens
//       c = scene * exposure                          // (bloom term = follow-up)
//       cinematic: saturation -> tint -> contrast/brightness (IMGS cinematic block)
//     Operates on gamma-space values exactly like the engine — no decode/encode by design.
//
// mainAvg is a second entry point rendered to a 1x1 float target first: a sparse grid average
// of the scene (stand-in for the engine's DownSample16 chain) with the engine's ADAPT length
// clamp applied (|avg| clamped to [0.01, UpperLUMClamp]).

Texture2D    uHdr    : register(t0);
Texture2D    uAvgLum : register(t1);
SamplerState uSampler : register(s0);

cbuffer TonemapParams : register(b0)
{
    float4 uParams0; // x = exposure, y = enabled (>=0.5), z = mode, w = TargetLUM
    float4 uParams1; // x = Saturation, y = ContrastAvgLum, z = Contrast, w = Brightness
    float4 uParams2; // xyz = Tint color, w = TintAmount
    float4 uParams3; // x = UpperLUMClamp, yzw reserved (bloom scale/clamp/radius follow-up)
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

    // OFF / legacy: passthrough (saturate reproduces the old UNORM clamp exactly) so the toggle
    // stays a clean regression control.
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

    // EngineFo3Fnv — ISHDRBLENDINSHADER[CIN] on gamma-space values.
    float3 adapted = uAvgLum.Sample(uSampler, float2(0.5, 0.5)).rgb;
    float lum = adapted.r + adapted.g + adapted.b;      // BPBLUR writes sum(AvgLum.rgb) into bloom.a
    float denom = max(lum, uParams0.w);
    float3 c = hdr.rgb * (uParams0.w / denom) * uParams0.x;

    // Cinematic block (ISHDRBLENDINSHADERCIN): saturation, tint, contrast/brightness around the
    // authored average-luminance pivot. {Brightness,Contrast} slot assignment is ambiguous in the
    // constant fill (identical at defaults, <0.04 divergence at FNV values); chosen as
    // Brightness*(Contrast*c - pivot) + pivot.
    float luma = dot(c, float3(0.299, 0.587, 0.114));
    c = lerp(luma.xxx, c, uParams1.x);
    c = lerp(c, luma * uParams2.xyz, uParams2.w);
    c = uParams1.w * (uParams1.z * c - uParams1.y) + uParams1.y;
    return float4(saturate(c), hdr.a);
}

// Average scene color for the engine exposure: sparse 16x16 grid mean (stand-in for the engine's
// DownSample16 box chain), then the engine ADAPT pass — temporal blend against the PREVIOUS adapted
// average (t1, the other ping-pong 1x1 target) followed by the length clamp. uParams3.y carries this
// frame's blend factor k = EyeAdaptSpeed^clamp(15*dt, 0, 1) (engine formula; 0 = instant, used for
// single-frame captures + the first live frame). The temporal blend is ALSO what stabilizes the
// sparse grid against camera motion — without it the 256-tap sample jitters the exposure as taps
// cross bright emissive edges (the "interior lighting flickers while moving" report).
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

    float3 avg = sum / (GridSize * GridSize);

    // ISHDRADAPT: new = k*prev + (1-k)*current, then length clamped to [0.01, UpperLUMClamp].
    float3 prev = uAvgLum.SampleLevel(uSampler, float2(0.5, 0.5), 0).rgb;
    avg = lerp(avg, prev, saturate(uParams3.y));
    float len = length(avg);
    float clamped = min(max(len, 0.01), uParams3.x);
    avg *= clamped / max(len, 0.0001);
    return float4(avg, 1.0);
}
