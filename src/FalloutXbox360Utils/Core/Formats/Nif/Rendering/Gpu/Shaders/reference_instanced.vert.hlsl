// Placed-object instancing vertex shader. Per-reference world/render state is bound as
// a root SRV at t8, while material textures use the shared bindless pixel-shader table.

cbuffer PerFrame : register(b0)
{
    float4x4 uViewProj;
};

cbuffer InstanceDraw : register(b1)
{
    uint uInstanceBase;
    float3 uInstanceDrawPad;
};

// Per-instance struct is 128 bytes. TextureState.x marks BC5/ATI2 normal decode;
// TexIndices.x = diffuse bindless slot and .y = normal bindless slot.
struct ReferenceInstance
{
    float4x4 World;
    float4 AlphaState;
    float4 RenderState;
    float4 TextureState;
    uint4  TexIndices;
};

StructuredBuffer<ReferenceInstance> uInstances : register(t8);

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
    ReferenceInstance instance = uInstances[uInstanceBase + instanceId];

    VSOutput o;
    float4 worldPos = mul(instance.World, float4(input.aPosition, 1.0));
    o.Position = mul(uViewProj, worldPos);
    o.vWorldNormal = mul((float3x3)instance.World, input.aNormal);
    o.vTexCoord = input.aTexCoord;
    o.vVertexColor = input.aVertexColor;
    o.vTangent = mul((float3x3)instance.World, input.aTangent);
    o.vBitangent = mul((float3x3)instance.World, input.aBitangent);
    o.vAlphaState = instance.AlphaState;
    o.vRenderState = instance.RenderState;
    o.vTextureState = instance.TextureState;
    o.vTexIndices = instance.TexIndices;
    return o;
}
