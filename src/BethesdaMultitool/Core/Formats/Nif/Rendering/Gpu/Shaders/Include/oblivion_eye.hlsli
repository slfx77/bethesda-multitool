#ifndef OBLIVION_EYE_HLSLI
#define OBLIVION_EYE_HLSLI

#include "atmosphere.hlsli"

// Existing 256-byte PerDrawConstants ABI. Only this dedicated eye pass assigns the bound and
// fade lanes below; ordinary material draws keep their existing EnvMap/UvScroll interpretation.
cbuffer EyeDraw : register(b1)
{
    float4x4 uEyeWorld : packoffset(c0);
    uint4 uEyeTextures : packoffset(c7);
    float4 uEyeBound : packoffset(c13); // xyz = assembled world-bound center, w = radius
    float4 uEyeLod : packoffset(c14);   // x = eye distance fade
};

struct EyeVarying
{
    float4 Position : SV_Position;
    centroid float4 NormalFacing : TEXCOORD0;
    centroid float3 Eye : TEXCOORD1;
};

#endif
