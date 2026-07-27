// Opt-in FO4/FO76 dynamic-water inputs. The resource topology is recovered from shipped group-5
// water shaders (t0 body/coverage, t1 composited normal, t2 gloss/flow, t15 depth LUT). The exact
// TESWaterNormals and LUT-generator shaders are still unavailable, so their open portions use
// explicit neutral inputs: t0 is the authored shallow endpoint and t2 is (1,1). No tuned constants
// are introduced; the pixel shader maps those neutral gloss samples back to authored WATR values.

cbuffer ModernWaterPrepass : register(b0)
{
    uint4 uModernSources;       // xyz = ordered authored normal-map bindless indices
    float4 uShallowCoverage;    // rgb = shallow color, a = shallow coverage
    float4 uDeepCoverage;       // rgb = deep color, a = deep coverage
    float4 uModernLayer1;       // UV scale, compass wind degrees, speed, amplitude
    float4 uModernLayer2;
    float4 uModernLayer3;
    float4 uModernNormalParams; // x = world tile, y = normal magnitude, z = depth amount, w = time
    float4 uModernRanges;       // color shallow/deep, alpha shallow/deep (authored units)
};

Texture2D<float4> gModernSources[] : register(t0, space1);
RWTexture2D<float4> gModernOutput : register(u0);
SamplerState gModernWrap : register(s0);

static const uint kMissingTexture = 0xFFFFFFFFu;

float2 CompassScroll(float4 layer)
{
    float angle = radians(layer.y);
    return float2(sin(angle), cos(angle)) * (layer.z * uModernNormalParams.w);
}

float3 SampleAuthoredNormal(uint index, float2 uv)
{
    if (index == kMissingTexture)
    {
        return float3(0.0, 0.0, 1.0);
    }

    float2 xy = gModernSources[NonUniformResourceIndex(index)].SampleLevel(gModernWrap, uv, 0).xy * 2.0 - 1.0;
    return float3(xy, sqrt(saturate(1.0 - dot(xy, xy))));
}

float RelativeLayerScale(float4 layer)
{
    // Modern absolute prepass scale is still open. Normalize by layer 1 so authored RELATIVE
    // frequencies survive without importing the classic engine's unrelated 0.01 conversion.
    float baseScale = max(abs(uModernLayer1.x), 1.0);
    return max(abs(layer.x), 1.0) / baseScale;
}

float RangeAmount(float value, float start, float end)
{
    float span = end - start;
    return abs(span) < 1e-6 ? 0.0 : saturate((value - start) / span);
}

[numthreads(8, 8, 1)]
void mainBodyCoverage(uint3 dispatchId : SV_DispatchThreadID)
{
    if (dispatchId.x >= 256 || dispatchId.y >= 256) return;

    // Neutral t0 while the refraction/body generator is unrecovered. It still updates from the
    // active WATR every frame and carries the authored alpha-test coverage without guessing a UV.
    gModernOutput[dispatchId.xy] = uShallowCoverage;
}

[numthreads(8, 8, 1)]
void mainNormal(uint3 dispatchId : SV_DispatchThreadID)
{
    if (dispatchId.x >= 256 || dispatchId.y >= 256) return;

    float2 uv = (float2(dispatchId.xy) + 0.5) / 256.0;
    float3 n1 = SampleAuthoredNormal(uModernSources.x,
        uv * RelativeLayerScale(uModernLayer1) + CompassScroll(uModernLayer1));
    float3 n2 = SampleAuthoredNormal(uModernSources.y,
        uv * RelativeLayerScale(uModernLayer2) + CompassScroll(uModernLayer2));
    float3 n3 = SampleAuthoredNormal(uModernSources.z,
        uv * RelativeLayerScale(uModernLayer3) + CompassScroll(uModernLayer3));

    float3 combined = n1 * uModernLayer1.w + n2 * uModernLayer2.w + n3 * uModernLayer3.w;
    combined.xy *= max(uModernNormalParams.y, 0.0);
    combined = normalize(float3(combined.xy, max(combined.z, 1e-4)));
    gModernOutput[dispatchId.xy] = float4(combined.xy * 0.5 + 0.5, 0.0, 1.0);
}

[numthreads(8, 8, 1)]
void mainGloss(uint3 dispatchId : SV_DispatchThreadID)
{
    if (dispatchId.x >= 256 || dispatchId.y >= 256) return;

    // The t2 generator is not recovered. Unit R/G samples let the recovered PS equations consume
    // explicit scales that mathematically reconstruct the WATR magnitude and power.
    gModernOutput[dispatchId.xy] = float4(1.0, 1.0, 0.0, 1.0);
}

[numthreads(8, 1, 1)]
void mainDepthLut(uint3 dispatchId : SV_DispatchThreadID)
{
    if (dispatchId.x >= 256) return;

    // The shipped PS samples the LUT in a 1/2.2 gamma depth domain. Build that domain directly;
    // exact row generation/vertex-color V selection remains isolated by using a one-row LUT.
    float gammaDepth = ((float)dispatchId.x + 0.5) / 256.0;
    float worldDepth = pow(gammaDepth, 2.2) * max(uModernNormalParams.z, 0.0);
    float colorT = RangeAmount(worldDepth, uModernRanges.x, uModernRanges.y);
    float alphaT = RangeAmount(worldDepth, uModernRanges.z, uModernRanges.w);
    gModernOutput[uint2(dispatchId.x, 0)] = float4(
        lerp(uShallowCoverage.rgb, uDeepCoverage.rgb, colorT),
        lerp(uShallowCoverage.a, uDeepCoverage.a, alphaT));
}
