// Oblivion (TES4) grass pixel shader — a transcription of retail GRASS2002.pso.
// Paired with reference_grass_oblivion.vert.hlsl; see that file's header for the full provenance,
// the normal-decode explanation, and the list of deliberate deviations.
//
// RETAIL (GRASS2002.pso), all 18 instructions, with a0=oT0 uv, a4=oT4 ambient, a5=oT5
// (diffuse.rgb, envelope.w), v0=oD0 (fog colour, fog amount):
//     texld r0, a0, s0
//     mov r1.xyz, a5
//     add r1.xyz, r1, a4          ; lit = oT5.rgb + oT4.rgb        <-- ADDITIVE, not multiplicative
//     mad r2.xyz, r1, -r0, v0     ; fogColour - lit*tex
//     add r0.w, -r0.w, c3.x       ; alphaRef - tex.a
//     mul r2.xyz, r2, v0.w        ; * fog amount
//     cmp r0.w, r0.w, c0.x, c0.y  ; (alphaRef - tex.a >= 0) ? 0 : 1   <-- HARD BINARY cutout
//     mad r0.xyz, r1, r0, r2      ; lit*tex + fogged remainder
//     mul r0.w, r0.w, a5.w        ; * the smooth distance envelope
//     mov oC0, r0
//
// The one line that matters most is `add r1.xyz, r1, a4`. Our shared shader composes
// `sample.rgb * shade * vertexRgb` — purely multiplicative — whereas retail multiplies the texture
// by (diffuse PLUS ambient). No uniform, constant or profile field can turn a multiply into an add,
// which is precisely why grass needs its own shader rather than another parameter.
//
// ALPHA IS DELIBERATELY UNCHANGED FROM THE SHARED PATH. Retail binarizes the mask and multiplies a
// smooth distance envelope; we keep the shared alpha test plus material alpha and leave the
// envelope to the viewer's existing CPU hard end. That preserves the soft blade silhouettes the
// user validated against the oracles on 2026-07-26 — this shader changes LIGHTING ONLY. (The
// 2026-07-25 alpha-to-coverage reroute, which did change silhouettes, was reverted; the binary
// cutout above is real but is not what made grass read as "a couple triangles with harsh cutoffs",
// and re-litigating it belongs after the lighting is confirmed.)

Texture2D    textures[]   : register(t0, space1);
SamplerState sDiffuse     : register(s0);
SamplerState sClampUWrapV : register(s4);
SamplerState sWrapUClampV : register(s5);
SamplerState sClampUV     : register(s6);

cbuffer Atmosphere : register(b3)
{
    float4 uSunDirIntensity;
    float4 uSunColorLighting;
    float4 uAmbientColor;
    float4 uSkyTopSkyEnabled;
    float4 uSkyHorizon;
    float4 uFogColorFogEnabled; // rgb = fog colour, w = fogEnabled
    float4 uAtmosphereParams;   // y = fogNear, z = fogFar
    float4 uCameraPosFogPower;  // xyz = camera world pos, w = fog power
    float4 uFogFarColorMax;     // rgb = far-fog colour, w = max powered fog amount
};

struct PSInput
{
    float4 Position : SV_Position;
    float2 vTexCoord : TEXCOORD0;
    nointerpolation float3 vGrassAmbient : TEXCOORD1;
    float3 vGrassDiffuse : TEXCOORD2;
    nointerpolation float4 vAlphaState   : TEXCOORD3;
    nointerpolation float4 vTextureState : TEXCOORD4;
    nointerpolation uint4  vTexIndices   : TEXCOORD5;
    float3 vWorldPos : TEXCOORD6;
};

// Verbatim from reference.frag.hlsl. Duplicated rather than shared through an .hlsli because
// `#include` is not wired up yet: every compile site still passes `include: null!`, and the first
// directive would fail with X1507 — which the GUI degrades to a log line and a blank viewport.
// Promoting these into a common header is the first job of the include-handler phase.
float3 ApplyFog(float3 color, float3 worldPos)
{
    if (uFogColorFogEnabled.w < 0.5)
    {
        return color;
    }

    float dist = length(worldPos - uCameraPosFogPower.xyz);
    float q = saturate((dist - uAtmosphereParams.y) / max(uAtmosphereParams.z - uAtmosphereParams.y, 1.0));
    float amount = min(pow(q, max(uCameraPosFogPower.w, 0.01)), saturate(uFogFarColorMax.w));
    float3 fogRgb = lerp(uFogColorFogEnabled.rgb, uFogFarColorMax.rgb, amount);
    return lerp(color, fogRgb, amount);
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

float4 SampleGrassDiffuse(uint slot, float2 uv, float packedState)
{
    uint addressing = (((uint)packedState) >> 1u) & 3u;
    if (addressing == 1u)
        return textures[NonUniformResourceIndex(slot)].Sample(sClampUWrapV, uv);
    if (addressing == 2u)
        return textures[NonUniformResourceIndex(slot)].Sample(sWrapUClampV, uv);
    if (addressing == 3u)
        return textures[NonUniformResourceIndex(slot)].Sample(sClampUV, uv);
    return textures[NonUniformResourceIndex(slot)].Sample(sDiffuse, uv);
}

float4 main(PSInput input) : SV_Target
{
    float4 sample = SampleGrassDiffuse(input.vTexIndices.x, input.vTexCoord, input.vTextureState.z);

    // The blade mask is the RAW texture alpha. It must NOT be modulated by vertex alpha: Oblivion
    // grass authors that channel as a WIND-SWAY WEIGHT (measured 0.0 at every ground vertex, rising
    // to 0.37-0.65 at the tips), so testing against it would discard the bottom of every blade and
    // leave the tufts floating.
    if (!PassAlphaTest(sample.a, input.vAlphaState.x, input.vAlphaState.y))
    {
        discard;
    }

    // GRASS2002's `add r1.xyz, r1, a4` then `mad r0.xyz, r1, r0, ...`.
    float3 lit = sample.rgb * (input.vGrassDiffuse + input.vGrassAmbient);
    float outAlpha = saturate(sample.a * input.vAlphaState.z);
    return float4(ApplyFog(lit, input.vWorldPos), outAlpha);
}
