// Placed-object instancing vertex shader. Per-reference world matrices are bound as a
// root SRV at t8; per-batch material/texture state rides in the InstanceDraw cbuffer
// (identical across a batch — uploading it per instance was pure redundancy). Material
// textures use the shared bindless pixel-shader table.

cbuffer PerFrame : register(b0)
{
    float4x4 uViewProj;
};

// Per-batch (one DrawIndexedInstanced) constants. TextureState.x marks BC5/ATI2 normal
// decode; TexIndices.x = diffuse bindless slot, .y = normal bindless slot. uInstanceBase
// is the start offset of this batch's worlds inside the shared instance buffer.
cbuffer InstanceDraw : register(b1)
{
    float4 uAlphaState;
    float4 uRenderState;
    float4 uTextureState;
    uint4  uTexIndices;
    uint   uInstanceBase;
    uint3  uInstanceDrawPad;
};

// Per-instance data is now JUST the world matrix (64 bytes). Everything else is per-batch.
StructuredBuffer<float4x4> uInstanceWorlds : register(t8);

struct VSInput
{
    float3 aPosition    : TEXCOORD0;
    float3 aNormal      : TEXCOORD1;
    float2 aTexCoord    : TEXCOORD2;
    float4 aVertexColor : TEXCOORD3;
    float3 aTangent     : TEXCOORD4;
    float3 aBitangent   : TEXCOORD5;
};

struct VSOutput
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
};

VSOutput main(VSInput input, uint instanceId : SV_InstanceID)
{
    float4x4 world = uInstanceWorlds[uInstanceBase + instanceId];

    VSOutput o;
    float4 worldPos = mul(world, float4(input.aPosition, 1.0));
    o.Position = mul(uViewProj, worldPos);
    o.vWorldNormal = mul((float3x3)world, input.aNormal);
    o.vTexCoord = input.aTexCoord;
    o.vVertexColor = input.aVertexColor;
    o.vTangent = mul((float3x3)world, input.aTangent);
    o.vBitangent = mul((float3x3)world, input.aBitangent);
    o.vAlphaState = uAlphaState;
    o.vRenderState = uRenderState;
    o.vTextureState = uTextureState;
    o.vTexIndices = uTexIndices;
    return o;
}
