// Installed ordinary Lighting pass 0x187: SLS2039.vso. Stock actor eyes have no authored
// vertex colors, and ObjToCubeSpace is the assembled affine world transform for this cohort.
#include "oblivion_eye.hlsli"

cbuffer PerFrame : register(b0)
{
    float4x4 uViewProj;
};

struct EyeInput
{
    float3 Position : TEXCOORD0;
};

EyeVarying main(EyeInput input)
{
    EyeVarying output;
    float4 worldPosition = mul(uEyeWorld, float4(input.Position, 1.0));
    output.Position = mul(uViewProj, worldPosition);
    // BoundCenter's fourth component is zero-initialized and the pass updates XYZ only.
    // Preserve SLS2039's dp4: the affine position W therefore participates in its length.
    float4 fromCenter = worldPosition - float4(uEyeBound.xyz, 0.0);
    float3 normal = fromCenter.xyz * rsqrt(dot(fromCenter, fromCenter));
    float3 eye = normalize(uCameraPosFogPower.xyz - worldPosition.xyz);
    float facing = saturate((dot(normal, eye) * rsqrt(dot(normal, normal)) - 0.800000012) * 6.66666651);
    output.NormalFacing = float4(normal * 0.5 + 0.5, facing * facing * (3.0 - 2.0 * facing));
    output.Eye = eye; // SLS2039 normalizes here, before interpolation. SM3012 does not.
    return output;
}
