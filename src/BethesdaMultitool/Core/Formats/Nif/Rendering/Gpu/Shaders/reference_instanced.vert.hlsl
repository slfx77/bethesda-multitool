// Placed-object instancing vertex shader. Per-reference world matrices are bound as a
// root SRV at t8; per-batch material/texture state rides in the InstanceDraw cbuffer
// (identical across a batch — uploading it per instance was pure redundancy). Material
// textures use the shared bindless pixel-shader table.

cbuffer PerFrame : register(b0)
{
    float4x4 uViewProj;
};

// Shared atmosphere CB (b3). References no longer read uCameraOrigin here: the render origin is folded
// into each instance world matrix's translation on the CPU (ReferenceRenderer12.Render's renderOrigin), so
// mul(world, pos) already yields the camera-relative position — keeping float32 precision far from the
// world origin (subtracting a ~52,000 absolute position AFTER the multiply lost it, Z-fighting distant
// architecture). The cbuffer is still declared for b3 layout parity (terrain/water read uCameraOrigin);
// the leading 8 float4 are the sun/sky/fog/camera fields this VS does not use.
cbuffer Atmosphere : register(b3)
{
    float4 uAtmospherePad[8];
    float4 uCameraOrigin; // retained for CB layout parity; references fold the origin CPU-side instead
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
    // SpeedTree wind: xy = horizontal wind direction, z = strength (0 disables), w = time (seconds).
    // The STLEAF/STB vertex shaders displace each vertex toward a per-frame wind-matrix-transformed
    // position by a per-vertex weight; we approximate the engine's 4 sway matrices with a time- and
    // world-position-phased gust (see reference_instanced.vert.hlsl leaf branch).
    float4 uWind;
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
        float4 worldCenterAbs = mul(world, float4(input.aTangent, 1.0)); // world is CPU-folded camera-relative
        float3 worldCenter = worldCenterAbs.xyz;
        float scale = length(float3(world[0].x, world[0].y, world[0].z)); // uniform REFR scale

        // SpeedTree wind sway. The STLEAF/STB VS does windedPos = lerp(pos, WindMatrix[idx]*pos,
        // windWeight); the engine builds 4 time-animated two-angle sway matrices per frame
        // (BSTreeManager::UpdateWindMatrices). We approximate that with a single gust whose phase varies
        // by absolute world position (so leaves sway out of sync) and by time (uWind.w), displacing the
        // card center along the horizontal wind direction (uWind.xy). Amplitude = strength (uWind.z) ×
        // per-leaf weight (aBitangent.z, height up the canopy) × card half-size (∝ tree size) × REFR
        // scale, so big trees sway more than bushes. uWind.z = 0 leaves the tree static.
        float windWeight = input.aBitangent.z;
        float sizeProxy = max(abs(input.aBitangent.x), abs(input.aBitangent.y));
        // Phase seed must stay anchored to the ABSOLUTE world position (so a fixed leaf sways with a
        // stable per-world-position phase as the camera moves). worldCenterAbs is now camera-relative
        // (world matrix is CPU-folded), so add uCameraOrigin — still bound in b3 — back to recover it.
        float3 worldCenterWorld = worldCenterAbs.xyz + uCameraOrigin.xyz;
        float phase = uWind.w * 0.7 + dot(worldCenterWorld, float3(0.03, 0.027, 0.05));
        float gust = sin(phase) + 0.25 * sin(phase * 2.9 + 1.7);
        worldCenter += float3(uWind.x, uWind.y, 0.0) * (gust * uWind.z * windWeight * sizeProxy * scale);

        worldPos = float4(
            worldCenter
                + uCameraRight.xyz * (input.aBitangent.x * scale)
                + uCameraUp.xyz    * (input.aBitangent.y * scale),
            1.0);
        // STLEAF shaders light each billboard from the per-leaf normal/leaf-base data, not a fixed up vector.
        o.vWorldNormal = normalize(mul((float3x3)world, input.aNormal));
    }
    else
    {
        // world's translation is CPU-folded to the render origin, so this is already the camera-relative
        // position (absolute when renderOrigin == 0). The prior post-multiply "-= uCameraOrigin" is gone.
        worldPos = mul(world, float4(input.aPosition, 1.0));
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
