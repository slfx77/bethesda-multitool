// Placed-object instancing vertex shader. Per-reference world matrices are bound as a
// root SRV at t8; per-batch material/texture state rides in the InstanceDraw cbuffer
// (identical across a batch — uploading it per instance was pure redundancy). Material
// textures use the shared bindless pixel-shader table.

cbuffer PerFrame : register(b0)
{
    float4x4 uViewProj;
};

// 1G — camera-relative render origin (xyz) from the shared atmosphere CB (b3). Subtracted from each
// world vertex before projection (kills the worldspace-edge wobble). Zero when camera-relative is off;
// the leading 8 float4 are the sun/sky/fog/camera fields this VS does not use.
cbuffer Atmosphere : register(b3)
{
    float4 uAtmospherePad[8];
    float4 uCameraOrigin;
};

// Per-batch (one DrawIndexedInstanced) constants. TextureState.x marks BC5/ATI2 normal
// decode; TexIndices.x = diffuse bindless slot, .y = normal bindless slot. uInstanceBase
// is the start offset of this batch's worlds inside the shared instance buffer.
cbuffer InstanceDraw : register(b1)
{
    float4 uAlphaState;
    float4 uRenderState;
    float4 uTextureState; // .x = BC5 normal decode, .y = leaf-billboard mode (>0.5)
    uint4  uTexIndices;
    uint   uInstanceBase;
    uint3  uInstanceDrawPad;
    float4 uSpecular; // xyz = specular tint, w = Phong exponent (0 = no specular highlight)
    // Camera world-space basis for per-card leaf billboards (from the inverse view matrix, same source
    // as SkyBillboardRenderer12). Only read when uTextureState.y marks a leaf submesh.
    float4 uCameraRight;
    float4 uCameraUp;
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
    float3 vWorldPos    : TEXCOORD9;  // world-space position for per-pixel distance fog
    nointerpolation float4 vSpecular   : TEXCOORD10; // xyz = tint, w = Phong exponent
};

VSOutput main(VSInput input, uint instanceId : SV_InstanceID)
{
    float4x4 world = uInstanceWorlds[uInstanceBase + instanceId];

    VSOutput o;
    float4 worldPos;
    if (uTextureState.y > 0.5)
    {
        // Per-card leaf billboard: the vertex carries the card CENTER (aTangent) and the signed 2D
        // card-space offset (aBitangent.xy). Rebuild the quad facing the camera around the world-space
        // center, scaled by the instance's uniform REFR scale. (SpeedTree builds leaf cards CPU-side as
        // flat 2D offsets around a center and re-faces them to the camera each frame — CLeafGeometry; we
        // do that same transform here in the VS.)
        float3 worldCenter = mul(world, float4(input.aTangent, 1.0)).xyz - uCameraOrigin.xyz;
        float scale = length(float3(world[0].x, world[0].y, world[0].z)); // uniform REFR scale
        worldPos = float4(
            worldCenter
                + uCameraRight.xyz * (input.aBitangent.x * scale)
                + uCameraUp.xyz    * (input.aBitangent.y * scale),
            1.0);
        // Card faces the camera; light it from world-up for a natural canopy look (leaves are flat cards).
        o.vWorldNormal = float3(0.0, 0.0, 1.0);
    }
    else
    {
        worldPos = mul(world, float4(input.aPosition, 1.0));
        worldPos.xyz -= uCameraOrigin.xyz; // camera-relative shift before projection (1G); 0 when off
        o.vWorldNormal = mul((float3x3)world, input.aNormal);
    }

    o.Position = mul(uViewProj, worldPos);
    o.vWorldPos = worldPos.xyz; // camera-relative world pos (matches the shader camera = 0 for fog/spec)
    o.vTexCoord = input.aTexCoord;
    o.vVertexColor = input.aVertexColor;
    o.vTangent = mul((float3x3)world, input.aTangent);
    o.vBitangent = mul((float3x3)world, input.aBitangent);
    o.vAlphaState = uAlphaState;
    o.vRenderState = uRenderState;
    o.vTextureState = uTextureState;
    o.vTexIndices = uTexIndices;
    o.vSpecular = uSpecular;
    return o;
}
