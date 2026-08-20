// FO3/FNV grass pixel shader — a transcription of retail GRASS2002.pso (shaderpackage019.sdp).
// Paired with reference_grass_fnv.vert.hlsl; see that file's header for the full provenance and
// the list of deliberate deviations.
//
// RETAIL (GRASS2000.pso, the base permutation; a0 = oT0 uv, a4 = oT4 ambient, a5 = oT5 sun):
//     texld r0, a0, s0
//     mov  r1.xyz, a5
//     add  r1.xyz, r1, a4          ; lit = sun + ambient          <-- ADDITIVE
//     mul  r2.xyz, r0, r1          ; tex * lit
//     mad  r0.xyz, r1, -r0, v0     ; fogColor - tex*lit
//     add  r0.w, -r0.w, c3.x
//     cmp  r0.w, r0.w, 0, 1        ; binary alpha test
//     mul  r1.w, r0.w, a5.w        ; * the distance envelope (CPU-side here)
//     mad  r1.xyz, v0.w, r0, r2    ; fog lerp
//
// GRASS2002.pso adds the sun-shadow permutation, and it is specific about WHERE the shadow lands:
//     shadowFactor = 1 + mask * (ShadowMap(...) - 1)
//     lit          = oT5.rgb * shadowFactor + oT4.rgb
// The AMBIENT term is never attenuated — shadowed grass falls to its ambient floor rather than to
// black. That is the one behavioural difference from the shared reference shader, which applies
// its cascade term to the whole shade.
//
// ALPHA: the retail binary cutout, plus an MSAA alpha-to-coverage variant that only SHARPENS the
// silhouette. It deliberately omits the shared path's mip alpha-coverage compensation — that
// technique keeps alpha-tested foliage from thinning with distance, which is the opposite of what
// retail grass does (binary cutout + a distance envelope that fades grass OUT). Inflating coverage
// made a sparse field read as a solid bright mat against the sand.

Texture2D    textures[]   : register(t0, space1);
SamplerState sDiffuse     : register(s0);
SamplerState sShadowPoint : register(s3); // CLAMP, point — sun-shadow-map depth taps (PCF)
SamplerState sClampUWrapV : register(s4);
SamplerState sWrapUClampV : register(s5);
SamplerState sClampUV     : register(s6);

#include "atmosphere.hlsli"
#include "fog.hlsli"
#include "shadow_sampling.hlsli"

struct PSInput
{
    float4 Position                      : SV_Position;
    float2 vTexCoord                     : TEXCOORD0;
    float3 vGrassSun                     : TEXCOORD1;
    nointerpolation float3 vGrassAmbient : TEXCOORD2;
    nointerpolation float4 vAlphaState   : TEXCOORD3;
    nointerpolation float4 vTextureState : TEXCOORD4;
    nointerpolation uint4  vTexIndices   : TEXCOORD5;
    float3 vWorldPos                     : TEXCOORD6;
};

uint MaterialTextureFlags(float packedState)
{
    return (uint)round(packedState);
}

// Bits 1/2 of the packed material state select the authored texture addressing mode; grass cards
// are clamped on both axes (TallGrassShader::PresetStages → CLAMP_S_CLAMP_T), which is what keeps
// their intentionally overshooting corner UVs off the neighbouring atlas islands.
float4 SampleGrassDiffuse(uint slot, float2 uv, float packedState)
{
    uint addressing = (MaterialTextureFlags(packedState) >> 1u) & 3u;
    if (addressing == 1u)
        return textures[NonUniformResourceIndex(slot)].Sample(sClampUWrapV, uv);
    if (addressing == 2u)
        return textures[NonUniformResourceIndex(slot)].Sample(sWrapUClampV, uv);
    if (addressing == 3u)
        return textures[NonUniformResourceIndex(slot)].Sample(sClampUV, uv);
    return textures[NonUniformResourceIndex(slot)].Sample(sDiffuse, uv);
}

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

float4 main(PSInput input) : SV_Target
{
    float4 sample = SampleGrassDiffuse(input.vTexIndices.x, input.vTexCoord, input.vTextureState.z);
    float testAlpha = sample.a;

#if ALPHA_TO_COVERAGE
    float a2cCoverage = 1.0;
    bool a2cActive = false;
    int a2cFn = (int)round(input.vAlphaState.y);
    if (input.vAlphaState.y >= 0.0 && (a2cFn == 4 || a2cFn == 6))
    {
        // NO mip alpha-coverage compensation here. The shared reference path scales alpha up with
        // mip level (Castano) so alpha-tested foliage does not thin out with distance — but retail
        // FO3/FNV grass does the OPPOSITE: a plain binary cutout plus a distance envelope that
        // fades grass out. Inflating coverage turns sparse blades into a near-solid mat when
        // looking down at a field, which reads as "grass far brighter than the terrain" even
        // though the per-pixel shade is correct (the grass:terrain lit ratio computes to
        // 0.78-1.04). Keep only the screen-space threshold sharpening, which antialiases the
        // silhouette without adding coverage.
        float a2cAlpha = testAlpha;
        a2cCoverage = saturate(
            (a2cAlpha - input.vAlphaState.x) / max(fwidth(a2cAlpha), 1e-4) + 0.5);
        a2cActive = true;
        if (a2cCoverage <= 0.0) discard;
    }
    else if (!PassAlphaTest(testAlpha, input.vAlphaState.x, input.vAlphaState.y))
    {
        discard;
    }
#else
    if (!PassAlphaTest(testAlpha, input.vAlphaState.x, input.vAlphaState.y)) discard;
#endif

    // GRASS2002.pso: the cascade term multiplies the SUN contribution only. Ambient is never
    // shadowed, so grass in shadow settles to L * AmbientColor instead of going black.
    // Bit 12 of the packed texture state (RuntimeFnvGrassNoSunShadowFlag) is the video-settings
    // "Shadows on grass" OFF gate — the renderer ORs it per draw; retail ini bShadowsOnGrass.
    bool grassShadowsOff = (MaterialTextureFlags(input.vTextureState.z) & 4096u) != 0u;
    float sunShadow = uSunColorLighting.w >= 0.5 && !grassShadowsOff
        ? ShadowFactor(input.vWorldPos) : 1.0;
    float3 lit = input.vGrassSun * sunShadow + input.vGrassAmbient;

    // NO per-sample ceiling here. GRASS2002.pso composes lit without a saturate, and the terrain
    // this grass has to sit beside (terrain_textured.frag.hlsl) writes the same float target with no
    // ceiling either — clamping only grass is an asymmetry we invented, and it whitens the blade
    // (retail's own midday grass light is warm, ~(1.04, 1.01, 0.94), with blue still under 1). The
    // real over-brightness was the sun term carrying the imagespace SunlightDimmer that retail never
    // applies to grass, plus a hardcoded 1.5 where the engine uses the GrassDimmer; both are fixed
    // in the vertex program, so the clamp has nothing left to mask.

    float outAlpha = saturate(sample.a * input.vAlphaState.z);
#if ALPHA_TO_COVERAGE
    if (a2cActive) outAlpha = a2cCoverage;
#endif

    return float4(ApplyFog(sample.rgb * lit, input.vWorldPos), outAlpha);
}
