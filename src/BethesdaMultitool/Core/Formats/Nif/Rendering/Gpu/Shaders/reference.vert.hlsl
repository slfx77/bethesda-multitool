// v3 Phase 3 placed-object vertex shader. Projects mesh-local vertices through the per-draw
// world matrix and per-frame viewProj. Passes the world-space normal + UV + vertex color to
// the pixel shader for Lambert + texture sampling.
//
// Same matrix-byte convention as terrain: System.Numerics is row-major in memory; HLSL
// cbuffer reads column-major; CPU side does NOT transpose, so `mul(M, v)` in HLSL produces
// the same result as `M * v` on the CPU. This means a CPU-side row-vector world matrix
// composed as `S * Rx * Ry * Rz * T` is consumed in HLSL as a column-vector matrix doing
// `T * Rz * Ry * Rx * S` applied to (col-vector) position — same end transform.

cbuffer PerFrame : register(b0)
{
    float4x4 uViewProj;
};

// Shared atmosphere CB (b3). References no longer read uCameraOrigin here: the render origin is folded
// into uWorld's translation on the CPU (ReferenceRenderer12.Render's renderOrigin), so mul(uWorld, pos)
// already yields the camera-relative position — computing it that way keeps float32 precision far from
// the world origin (subtracting a ~52,000 absolute position AFTER the multiply lost it, Z-fighting distant
// architecture). The cbuffer is still declared for b3 layout parity (terrain/water read uCameraOrigin);
// the leading 8 float4 are the sun/sky/fog/camera fields this VS does not use.
cbuffer Atmosphere : register(b3)
{
    float4 uAtmospherePad[8];
    float4 uCameraOrigin; // retained for CB layout parity; references fold the origin CPU-side instead
};

cbuffer PerDraw : register(b1)
{
    float4x4 uWorld;
    // uAlphaState: x = alpha-test threshold, y = alpha-test function, z = material alpha,
    // w = blended-mode flag. Unused in VS; kept for PerDraw cbuffer layout parity with PS.
    float4 uAlphaState;
    // uRenderState: x = double-sided, y = has bump map, z = bump strength, w = unlit/emissive.
    // Unused in VS; cull state is handled on the CPU side.
    float4 uRenderState;
    // uTextureState: x = BC5/ATI2 normal map, yzw reserved.
    float4 uTextureState;
    // 4a — bindless TexIndices: .x diffuse slot, .y normal slot, .zw reserved. Mirrors
    // the instanced VS's per-instance struct; the PS reads vTexIndices regardless of path.
    uint4  uTexIndices;
    // 1A — specular: xyz = tint, w = Phong exponent (0 = no specular). Matches PerDrawConstants.
    float4 uSpecular;
    // Camera world-space basis for per-card leaf billboards (same source as the instanced VS). Only read
    // when uTextureState.y marks a leaf submesh — used by baked particle clouds drawn on the blended path.
    float4 uCameraRight;
    float4 uCameraUp;
};

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

VSOutput main(VSInput input)
{
    VSOutput o;
    float4 worldPos;
    if (uTextureState.y > 0.5)
    {
        // Per-card leaf billboard (baked particle quads): the vertex carries the card CENTER (aTangent)
        // and the signed 2D card-space offset (aBitangent.xy). Rebuild the quad facing the camera around
        // the world-space center, scaled by the uniform REFR scale. Same math as reference_instanced.vert.hlsl;
        // particles have no wind weight so the sway term is omitted.
        // uWorld already carries the CPU-folded camera-relative origin, so this center is camera-relative.
        float3 worldCenter = mul(uWorld, float4(input.aTangent, 1.0)).xyz;
        float scale = length(float3(uWorld[0].x, uWorld[0].y, uWorld[0].z));
        worldPos = float4(
            worldCenter
                + uCameraRight.xyz * (input.aBitangent.x * scale)
                + uCameraUp.xyz    * (input.aBitangent.y * scale),
            1.0);
    }
    else
    {
        // uWorld's translation is CPU-folded to the render origin, so this is already the camera-relative
        // position (absolute when renderOrigin == 0). The prior post-multiply "-= uCameraOrigin" is gone.
        worldPos = mul(uWorld, float4(input.aPosition, 1.0));
    }
    o.Position = mul(uViewProj, worldPos);
    o.vWorldPos = worldPos.xyz; // camera-relative world pos (matches the shader camera = 0 for fog/spec)
    // Uniform scale only — pass the normal through the world rotation (3x3 sub-matrix). For
    // non-uniform scale we'd want the inverse-transpose, but Bethesda REFR.Scale is uniform.
    o.vWorldNormal = mul((float3x3)uWorld, input.aNormal);
    o.vTexCoord = input.aTexCoord;
    o.vVertexColor = input.aVertexColor;
    o.vTangent = mul((float3x3)uWorld, input.aTangent);
    o.vBitangent = mul((float3x3)uWorld, input.aBitangent);
    o.vAlphaState = uAlphaState;
    o.vRenderState = uRenderState;
    o.vTextureState = uTextureState;
    o.vTexIndices = uTexIndices;
    o.vSpecular = uSpecular;
    return o;
}
