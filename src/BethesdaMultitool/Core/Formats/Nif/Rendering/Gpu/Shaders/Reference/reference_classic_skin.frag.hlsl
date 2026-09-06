// Native-viewer Oblivion FaceGen specialization, grounded in retail SKIN2000.pso. The current
// asset path supplies its already-precomposed BaseMap/FaceGenMap0/FaceGenMap1 albedo; this shader
// reproduces the source-proven full normal decode and lighting/output order.

Texture2D textures[] : register(t0, space1);
SamplerState sDiffuse : register(s0);
SamplerState sNormalMap : register(s1);
SamplerState sClampUWrapV : register(s4);
SamplerState sWrapUClampV : register(s5);
SamplerState sClampUV : register(s6);

#include "atmosphere.hlsli"
#include "fog.hlsli"

struct PSInput
{
    float4 Position     : SV_Position;
    float3 vWorldNormal : TEXCOORD0;
    float2 vTexCoord    : TEXCOORD1;
    float4 vVertexColor : TEXCOORD2;
    float3 vTangent     : TEXCOORD3;
    float3 vBitangent   : TEXCOORD4;
    nointerpolation float4 vAlphaState  : TEXCOORD5;
    nointerpolation float4 vRenderState : TEXCOORD6;
    nointerpolation float4 vTextureState : TEXCOORD7;
    nointerpolation uint4  vTexIndices  : TEXCOORD8;
    float3 vWorldPos    : TEXCOORD9;
    nointerpolation float4 vSpecular   : TEXCOORD10;
    nointerpolation float4 vEffectTint    : TEXCOORD11;
    nointerpolation float4 vEffectFalloff : TEXCOORD12;
    nointerpolation float4 vEnvMap        : TEXCOORD13;
    nointerpolation float4 vSoftParticle  : TEXCOORD14;
    nointerpolation float vSpecularLodFade : TEXCOORD15;
    float3 vFnvActiveAdtBaseLight : TEXCOORD16;
    nointerpolation float4 vHeatmap : TEXCOORD17;
    centroid float3 vClassicSkinLight : TEXCOORD18;
    centroid float3 vClassicSkinEye : TEXCOORD19;
    bool IsFrontFace : SV_IsFrontFace;
};

uint MaterialTextureFlags(float packedState)
{
    return (uint)round(packedState);
}

float4 SampleClassicSkinTexture(uint slot, float2 uv, float packedState, bool normalMap)
{
    uint addressing = (MaterialTextureFlags(packedState) >> 1u) & 3u;
    if (addressing == 1u)
        return textures[NonUniformResourceIndex(slot)].Sample(sClampUWrapV, uv);
    if (addressing == 2u)
        return textures[NonUniformResourceIndex(slot)].Sample(sWrapUClampV, uv);
    if (addressing == 3u)
        return textures[NonUniformResourceIndex(slot)].Sample(sClampUV, uv);
    return normalMap
        ? textures[NonUniformResourceIndex(slot)].Sample(sNormalMap, uv)
        : textures[NonUniformResourceIndex(slot)].Sample(sDiffuse, uv);
}

float3 ApplyClassicSkinFog(float3 color, float3 worldPosition)
{
    if (uFogColorFogEnabled.w < 0.5)
    {
        return color;
    }

    float amount = FogAmountAtDistance(length(worldPosition - uCameraPosFogPower.xyz));
    return lerp(color, FogColorForAmount(amount), amount);
}

float4 main(PSInput input) : SV_Target
{
    // The specialization is ordinary main-scene opaque geometry, so the host binds the neutral
    // clip plane. Retain the shared ABI's clip operation for completeness.
    clip(dot(input.vWorldPos.xyz, uClipPlane.xyz) + uClipPlane.w);

    float4 baseMap = SampleClassicSkinTexture(
        input.vTexIndices.x, input.vTexCoord, input.vTextureState.z, false);
    float3 geometricNormal = normalize(input.vWorldNormal);
    bool flipTangentBasis = input.vRenderState.x > 0.5 && !input.IsFrontFace;
    if (flipTangentBasis)
    {
        geometricNormal = -geometricNormal;
    }

    float3 normal = geometricNormal;
    float3 tangentNormal = float3(0.0, 0.0, 1.0);
    if (input.vRenderState.y > 0.5)
    {
        float4 normalSample = SampleClassicSkinTexture(
            input.vTexIndices.y, input.vTexCoord, input.vTextureState.z, true);

        // Retail: normalize(2 * (NormalMap - 0.5)). BC5 SRVs expose only XY, so reconstruct Z while
        // preserving that same full-strength decode; there is deliberately no bump-strength scale.
        float3 mapNormal;
        if (input.vTextureState.x > 0.5)
        {
            float2 xy = input.vTextureState.x > 1.5
                ? normalSample.rg
                : normalSample.rg * 2.0 - 1.0;
            mapNormal = float3(xy, sqrt(saturate(1.0 - dot(xy, xy))));
        }
        else
        {
            mapNormal = normalSample.rgb * 2.0 - 1.0;
        }
        mapNormal = normalize(mapNormal);
        tangentNormal = mapNormal;

        float tangentLengthSquared = dot(input.vTangent, input.vTangent);
        float bitangentLengthSquared = dot(input.vBitangent, input.vBitangent);
        if (tangentLengthSquared > 1e-6 && bitangentLengthSquared > 1e-6)
        {
            float3 tangent = input.vTangent * rsqrt(tangentLengthSquared);
            float3 bitangent = input.vBitangent * rsqrt(bitangentLengthSquared);
            if (flipTangentBasis)
            {
                bitangent = -bitangent;
            }
            normal = normalize(
                mapNormal.x * tangent +
                mapNormal.y * bitangent +
                mapNormal.z * geometricNormal);
        }
    }

    // The native asset viewer binds a neutral 0.4 ambient + 0.6 key light and the live camera in
    // b3. The fallback keeps the specialization visible only if that per-frame upload cannot fit.
    bool sceneLighting = uSunColorLighting.w >= 0.5;
    float3 lightDirection = sceneLighting
        ? normalize(uSunDirIntensity.xyz)
        : normalize(float3(0.5, 0.5, 1.0));
    float3 lightColor = sceneLighting ? uSunColorLighting.rgb : 0.6.xxx;
    float ambientScale = uAmbientColor.w > 0.0001 ? uAmbientColor.w : 1.0;
    float3 ambientColor = sceneLighting
        ? uAmbientColor.rgb * ambientScale
        : 0.4.xxx;

    float3 eyeVector = uCameraPosFogPower.xyz - input.vWorldPos;
    float eyeLengthSquared = dot(eyeVector, eyeVector);
    float3 viewDirection = eyeLengthSquared > 1e-8
        ? eyeVector * rsqrt(eyeLengthSquared)
        : float3(0.0, 0.0, 1.0);

    // Keep the existing geometric fallback for absent normal maps or unusable projected inputs.
    // The admitted mapped route uses the complete recovered VS/PS pair: centroid light is used
    // directly; only the interpolated eye is normalized again by SKIN2000.pso. Its map normal is
    // tangent-space and has no pixel-stage TBN reconstruction or back-face normal flip.
    float NdotL = max(dot(normal, lightDirection), 0.0);
    float NdotV = max(dot(normal, viewDirection), 0.0);
    float classicEyeLengthSquared = dot(input.vClassicSkinEye, input.vClassicSkinEye);
    if (input.vRenderState.y > 0.5 &&
        all(isfinite(input.vClassicSkinLight)) && all(isfinite(input.vClassicSkinEye)) &&
        classicEyeLengthSquared > 0.0 && isfinite(classicEyeLengthSquared))
    {
        NdotL = max(dot(tangentNormal, input.vClassicSkinLight), 0.0);
        float3 interpolatedEye = normalize(input.vClassicSkinEye);
        NdotV = max(dot(tangentNormal, interpolatedEye), 0.0);
    }
    float oneMinusNdotV = 1.0 - NdotV;
    float3 rim = 0.5 * lightColor *
        oneMinusNdotV * oneMinusNdotV * oneMinusNdotV;
    float3 lighting = max(
        ambientColor + lightColor * NdotL + rim,
        0.0);

    // The retained vertex color is the current equivalent of retail Toggles.x. Apply it to the
    // precomposed albedo before lighting, then fog the lit result toward FogColor (retail v1/w).
    float3 albedo = baseMap.rgb * input.vVertexColor.rgb;
    float3 lit = albedo * lighting;
    float3 outputRgb = ApplyClassicSkinFog(lit, input.vWorldPos);

    // The shared atmosphere ABI uses AmbientColor.w for host ambient scale, so the unavailable
    // retail AmbientColor.a lane is conservatively neutral. BaseMap alpha remains observable.
    return float4(outputRgb, baseMap.a);
}
