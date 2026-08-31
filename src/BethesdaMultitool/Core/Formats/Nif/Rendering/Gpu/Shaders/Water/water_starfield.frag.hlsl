// Starfield WATR source-backed water approximation.
//
// This is NOT a recovered Creation Engine 2 Water shader. It improves on the old textureless flat
// plane only where retail evidence is direct: exact WATR noise layers, roughness, normal magnitude,
// depth, NAM0 velocity and flags reach this program, and the sampled files are shipped global water
// assets. Their slot assignment, the normal/falloff composition below, the generic roughness lobe,
// and the 0.6 viewer coverage remain explicitly approximate. CUR3 optics, WaterPlaceholder.mat /
// materialsbeta.cdb constants, surface colour, transmission and refraction stay neutral until their
// binding/evaluation equations are recovered.

#include "water_common.hlsli"

static const uint kStarfieldEnableFlowmap = 1u << 3;
static const uint kStarfieldBlendNormals = 1u << 4;
static const uint kNoTexture = 0xFFFFFFFFu;

float2 StarfieldDirection(float degreesValue)
{
    float angle = radians(degreesValue);
    return float2(sin(angle), cos(angle));
}

float3 StarfieldSampleNormal(uint textureIndex, float2 uv)
{
    if (textureIndex == kNoTexture)
    {
        return float3(0.0, 0.0, 1.0);
    }

    float3 encoded = gWaterTextures[NonUniformResourceIndex(textureIndex)]
        .Sample(gWaterSampler, uv).xyz * 2.0 - 1.0;
    return normalize(float3(encoded.xy, max(abs(encoded.z), 1e-4)));
}

float2 StarfieldLayerUv(float2 worldXy, float4 layer, float2 currentWorld, float2 flowUv, float time)
{
    // WATR names establish the inputs but not the CE2 coordinate equation. Treat UV scale as a
    // world-space tile size and NAM0 as world-space current; wind speed remains UV/second. Keeping
    // the assumption here (and in telemetry) makes replacement straightforward when DXIL recovers it.
    float tile = max(abs(layer.x), 1e-3);
    return (worldXy + currentWorld * time) / tile +
        StarfieldDirection(layer.y) * (layer.z * time) + flowUv;
}

float4 main(PSInput input) : SV_Target
{
    float time = uCamPosTime.w;
    uint flags = asuint(uStarfieldLayerFalloffsFlags.w);
    float2 currentWorld = uStarfieldLinearVelocity.w > 0.5
        ? uStarfieldLinearVelocity.xy
        : float2(0.0, 0.0);

    float2 flowUv = float2(0.0, 0.0);
    if ((flags & kStarfieldEnableFlowmap) != 0u && uNormalIndices.z != kNoTexture)
    {
        float flowTile = max(abs(uStarfieldDepthFlow.y), 1e-3);
        float2 flowSample = gWaterTextures[NonUniformResourceIndex(uNormalIndices.z)]
            .Sample(gWaterSampler, (input.vWorldPos.xy + currentWorld * time) / flowTile).xy * 2.0 - 1.0;
        // Slot identity is inferred and the CE2 flow strength is unrecovered. Keep the perturbation
        // bounded so a wrong slot cannot explode UVs while still making the authored flow flag visible.
        flowUv = flowSample * 0.05;
    }

    float2 uv1 = StarfieldLayerUv(
        input.vWorldPos.xy, uStarfieldLayer1, currentWorld, flowUv, time);
    float2 uv2 = StarfieldLayerUv(
        input.vWorldPos.xy, uStarfieldLayer2, currentWorld, flowUv, time);
    float2 uv3 = StarfieldLayerUv(
        input.vWorldPos.xy, uStarfieldLayer3, currentWorld, flowUv, time);
    float3 n1 = StarfieldSampleNormal(uNormalIndices.x, uv1);
    float3 n2 = StarfieldSampleNormal(uNormalIndices.y, uv2);
    // Retail evidence proves two global normal assets plus a flow asset, not three independent
    // normal slots. Re-sample the primary normal at layer 3's authored coordinates instead of
    // pretending defaultflow_normal is a third surface normal.
    float3 n3 = StarfieldSampleNormal(uNormalIndices.x, uv3);

    float depthT = 1.0;
    uint depthIndex = uDepthParams.x;
    if (depthIndex != kNoTexture)
    {
        float near = asfloat(uDepthParams.y);
        float far = asfloat(uDepthParams.z);
        uint depthSampleCount = max((uint)uRenderOrigin.w, 1u);
        float occluderNdc;
        float sceneNdc = LoadSceneDepth(
            depthIndex, (int2)input.Position.xy, depthSampleCount, input.Position.z, occluderNdc);
        float sceneDist = LinearizeDepth(sceneNdc, near, far);
        float waterDist = LinearizeDepth(input.Position.z, near, far);
#if !WATER_HARDWARE_OCCLUSION
        clip(LinearizeDepth(occluderNdc, near, far) - waterDist + asfloat(uDepthParams.w));
#endif
        float column = max(sceneDist - waterDist, 0.0);
        depthT = saturate(column / max(abs(uStarfieldDepthFlow.x), 1e-3));
    }

    float3 normalWeights = max(
        float3(uStarfieldLayer1.w, uStarfieldLayer2.w, uStarfieldLayer3.w),
        float3(0.0, 0.0, 0.0));
    // CE2's falloff equation is open. This monotonic viewer mapping preserves all authored values
    // and makes greater falloff reduce deep-water contribution without claiming engine identity.
    normalWeights /= 1.0 + max(uStarfieldLayerFalloffsFlags.xyz, 0.0) * depthT;
    float weightSum = normalWeights.x + normalWeights.y + normalWeights.z;
    float3 blended = ((flags & kStarfieldBlendNormals) != 0u && weightSum > 1e-5)
        ? (n1 * normalWeights.x + n2 * normalWeights.y + n3 * normalWeights.z) / weightSum
        : n1;
    float authoredFalloff = lerp(
        max(uStarfieldSurface.z, 0.0),
        max(uStarfieldSurface.w, 0.0),
        depthT);
    float normalStrength = max(uStarfieldSurface.y, 0.0) / (1.0 + authoredFalloff * depthT);
    float3 N = normalize(float3(blended.xy * normalStrength, max(blended.z, 1e-4)));

    float3 V = normalize(uCamPosTime.xyz - input.vWorldPos);
    bool lit = uSunColorLighting.w > 0.5;
    float3 sunDirection = lit ? normalize(uSunDirIntensity.xyz) : normalize(float3(0.5, 0.5, 1.0));
    float3 sunColor = lit ? uSunColorLighting.rgb * uSunDirIntensity.w : float3(1.0, 0.97, 0.9);
    float3 ambient = lit ? uAmbientColor.rgb : float3(1.0, 1.0, 1.0);
    float ndotl = saturate(dot(N, sunDirection));
    float ndotv = saturate(dot(N, V));
    float roughness = saturate(uStarfieldSurface.x);

    // Viewer-authored neutral surface colour; WATR absorption/concentration/CUR3 lanes are not a
    // proven surface-colour equation. The generic lobe merely exposes exact roughness visually.
    float3 body = uShallow.rgb * (ambient + sunColor * ndotl * 0.35);
    float3 reflectedDirection = reflect(-V, N);
    float3 reflection = SampleSkyReflection(input.Position.xy, N, reflectedDirection);
    float fresnel = 0.02 + 0.98 * pow(1.0 - ndotv, 5.0);
    float3 halfVector = normalize(V + sunDirection);
    float specularPower = lerp(256.0, 8.0, roughness);
    float specular = pow(saturate(dot(N, halfVector)), specularPower) * (1.0 - roughness);
    float3 color = lerp(body, reflection, fresnel * lerp(1.0, 0.35, roughness)) +
        sunColor * specular;

    // WATR ANAM is explicitly unused in Starfield. CPU always supplies the profile's labelled
    // viewer coverage here; it never projects the record byte onto alpha.
    float alpha = saturate(asfloat(uNoiseParams.w));
    return float4(ApplyFog(color, input.vWorldPos), alpha);
}
