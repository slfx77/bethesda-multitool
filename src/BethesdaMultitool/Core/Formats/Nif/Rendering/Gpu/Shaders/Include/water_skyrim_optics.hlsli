// Skyrim LE's recovered above-water depth/fog slice (retail PS flags0x1A and0x15E).
// This header is included only in SKYRIM_OPAQUE_REFRACTION. It does not define FNV inputs.
// CPU union: SkyrimWaterOpticsConstants -> Fo4Spec=DepthControl, Fo4Ranges=(far,span,power,enabled).
// Remaining approximation: source UV/scroll and base-normal assembly, Fresnel's VS-distance gate,
// projected refraction distortion, reflection/camera exposure, and the scene-snapshot fog history.
#ifndef WATER_SKYRIM_OPTICS_HLSLI
#define WATER_SKYRIM_OPTICS_HLSLI

bool SkyrimHasAuthoredOptics()
{
    return uFo4Ranges.w == 1.0;
}

float SkyrimSlantDepthFraction(float slantColumn)
{
    return saturate(slantColumn / uFo4Ranges.x);
}

float4 SkyrimDepthFactors(float2 columns)
{
    // Raw PS: mul_sat r4, 1/FogParam.z, columns.xxzz; mad r1,DepthControl,r4-1,1.
    float4 depthFractions = saturate(columns.xxyy / uFo4Ranges.x);
    return 1.0 + uFo4Spec * (depthFractions - 1.0);
}

float3 SkyrimApplyNormalDepth(float3 normalizedBaseNormal, float normalFactor)
{
    const float3 up = float3(0.0, 0.0, 1.0);
    return normalize(up + normalFactor * (normalizedBaseNormal - up));
}

float SkyrimBodyFogWeight(float slantColumn)
{
    float slantDepth = SkyrimSlantDepthFraction(slantColumn);
    float clearFraction = saturate(uFo4Ranges.x * (1.0 - slantDepth) / uFo4Ranges.y);
    return 1.0 - pow(clearFraction, uFo4Ranges.z);
}

#endif
