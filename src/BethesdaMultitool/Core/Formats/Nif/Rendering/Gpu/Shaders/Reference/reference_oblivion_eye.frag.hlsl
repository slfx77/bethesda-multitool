// Installed ordinary Lighting pass 0x187: SLS2045.pso, bounded missing-normal stock eye arm.
#include "oblivion_eye.hlsli"

TextureCube cubemaps[] : register(t0, space2);
SamplerState sCube : register(s2);

float4 main(EyeVarying input) : SV_Target
{
    // Retail deliberately does not normalize the interpolated normal before this reflection.
    float3 normal = 2.0 * (input.NormalFacing.xyz - 0.5);
    float3 eye = normalize(input.Eye);
    float3 reflected = 2.0 * dot(normal, eye) * normal - dot(normal, normal) * eye;
    float3 reflection = cubemaps[NonUniformResourceIndex(uEyeTextures.z)].Sample(sCube, reflected).rgb;
    // The normal binder's RGBA(128,128,255,64) fallback contributes only its alpha. Stock
    // vertex color and material/pass alpha are one. The studio directional light supplies
    // the ordinary eye writer's diffuse bank, matching its one-directional-light branch.
    float3 color = reflection * (64.0 / 255.0) * uEyeLod.x;
    color *= uSunColorLighting.rgb * uSunDirIntensity.w;
    color *= input.NormalFacing.w;
    color *= 3.0;
    return float4(color, 1.0);
}
