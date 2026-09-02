// Sun-shadow depth pass pixel shader — ALPHA-TESTED batches only (foliage, fences, leaf
// cards). Opaque batches render depth with a null pixel shader; this variant exists solely
// to discard the transparent texels of cutout materials so a tree casts a leaf-shaped
// shadow instead of a card-shaped one. Pairs with reference_instanced.vert.hlsl (the input
// struct mirrors its full VSOutput so signature linkage never depends on subset matching).
//
// The alpha-test logic mirrors reference.frag.hlsl's: same PassAlphaTest comparison
// functions, and the same "blended + tested foliage tests the TEXTURE alpha alone" rule
// (NVSeaPlant02-style depth-writing blends) — minus the Castaño mip boost, which exists to
// keep distant leaves VISIBLE and has no depth-pass equivalent worth its cost.

Texture2D    textures[]   : register(t0, space1);
SamplerState sDiffuse     : register(s0);
SamplerState sClampUWrapV : register(s4);
SamplerState sWrapUClampV : register(s5);
SamplerState sClampUV     : register(s6);

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
};

bool PassAlphaTest(float alpha, float threshold, float functionId)
{
    if (functionId < 0.0) return true;

    int fn = (int)round(functionId);
    if (fn == 0) return true;                  // ALWAYS
    if (fn == 1) return alpha < threshold;     // LESS
    if (fn == 2) return abs(alpha - threshold) <= (0.5 / 255.0); // EQUAL
    if (fn == 3) return alpha <= threshold;    // LEQUAL
    if (fn == 4) return alpha > threshold;     // GREATER
    if (fn == 5) return abs(alpha - threshold) > (0.5 / 255.0);  // NOTEQUAL
    if (fn == 6) return alpha >= threshold;    // GEQUAL
    return false;                              // NEVER / invalid
}

float SampleMaterialAlpha(uint slot, float2 uv, float packedState)
{
    uint addressing = ((uint)round(packedState) >> 1u) & 3u;
    if (addressing == 1u)
        return textures[NonUniformResourceIndex(slot)].Sample(sClampUWrapV, uv).a;
    if (addressing == 2u)
        return textures[NonUniformResourceIndex(slot)].Sample(sWrapUClampV, uv).a;
    if (addressing == 3u)
        return textures[NonUniformResourceIndex(slot)].Sample(sClampUV, uv).a;
    return textures[NonUniformResourceIndex(slot)].Sample(sDiffuse, uv).a;
}

float SampleMaterialRed(uint slot, float2 uv, float packedState)
{
    uint addressing = ((uint)round(packedState) >> 1u) & 3u;
    if (addressing == 1u)
        return textures[NonUniformResourceIndex(slot)].Sample(sClampUWrapV, uv).r;
    if (addressing == 2u)
        return textures[NonUniformResourceIndex(slot)].Sample(sWrapUClampV, uv).r;
    if (addressing == 3u)
        return textures[NonUniformResourceIndex(slot)].Sample(sClampUV, uv).r;
    return textures[NonUniformResourceIndex(slot)].Sample(sDiffuse, uv).r;
}

void main(PSInput input)
{
    bool starfieldOpacity = (((uint)round(input.vTextureState.z)) & 32768u) != 0u;
    bool starfieldMaterialLerp = input.vTextureState.w == -2.0 || input.vTextureState.w == -3.0;
    float alpha = starfieldOpacity
        ? SampleMaterialRed(input.vTexIndices.z, input.vTexCoord, input.vTextureState.z)
        : SampleMaterialAlpha(input.vTexIndices.x, input.vTexCoord, input.vTextureState.z);
    float testAlpha = starfieldOpacity
        ? alpha
        : (input.vAlphaState.w > 0.5 || starfieldMaterialLerp)
            ? alpha
            : saturate(alpha * input.vVertexColor.a);
    if (!PassAlphaTest(testAlpha, input.vAlphaState.x, input.vAlphaState.y)) discard;
}
