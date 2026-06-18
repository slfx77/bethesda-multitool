// v3 textured-sky billboard pixel shader. Samples the bindless sun / moon texture and modulates it by
// the per-billboard tint + fade. The blend mode is chosen by the PSO — ADDITIVE for the sun disc/glare
// (they glow over the sky), ALPHA for the moon (a lit disc over the night sky) — so this shader just
// emits texel.rgb * tint.rgb with alpha = texel.a * tint.a * fade, and the fixed-function blend does
// the rest. Same bindless table (t0, space1) + static sampler (s0) the terrain/reference shaders use.

Texture2D    textures[] : register(t0, space1);
SamplerState sSky       : register(s0);

cbuffer Billboard : register(b0)
{
    float4x4 uViewProj;
    float4 uCenterDirRadius;
    float4 uRightHalfSize;
    float4 uUpFade;          // w = fade (0..1)
    float4 uCamPos;          // xyz = camera world pos, w unused
    float4 uTint;            // rgb = tint, a = base alpha
    uint4  uTexIndex;        // x = bindless texture index
};

struct PSInput
{
    float4 Position : SV_Position;
    float2 vUv      : TEXCOORD0;
};

float4 main(PSInput input) : SV_Target
{
    float4 texel = textures[NonUniformResourceIndex(uTexIndex.x)].Sample(sSky, input.vUv);
    float3 rgb = texel.rgb * uTint.rgb;
    float alpha = texel.a * uTint.a * uUpFade.w;
    return float4(rgb, alpha);
}
