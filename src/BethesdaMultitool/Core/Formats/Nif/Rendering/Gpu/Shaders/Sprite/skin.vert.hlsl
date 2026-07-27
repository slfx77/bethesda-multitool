// SM 5.0 vertex shader — port of skin.vert.glsl.
// Transforms vertices by orthographic view-projection and passes
// interpolated attributes to the pixel shader.
//
// Matrix conventions: System.Numerics.Matrix4x4 stores row-major in CPU memory.
// HLSL cbuffers interpret matrix bytes column-major by default, which equals the
// transpose of the CPU layout. The CPU code already accounts for this (it builds
// the view matrix with basis vectors in rows of the System.Numerics matrix so the
// shader sees them as columns after reinterpretation). `mul(M, v)` therefore
// produces the same result as the original GLSL `M * v`.

cbuffer Uniforms : register(b0)
{
    float4x4 uViewProj;
    float4x4 uView;       // 3x3 view rotation (used for normals/tangents)
    float4 uLightDir;     // xyz = normalized light direction, w = unused
    float4 uHalfVec;      // xyz = half vector, w = HdotNegL
    float4 uAmbient;      // x = skyAmbient, y = groundAmbient, z = lightIntensity, w = bumpStrength
    float4 uMaterial;     // x = materialAlpha, y = envMapScale, z = alphaTestThreshold, w = alphaTestFunc
    float4 uTintColor;    // rgb = tint, a = unused
    float4 uFlags;        // x = bitfield (passed as float, cast to uint in pixel shader)
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
    float  vDepth       : TEXCOORD5;
};

VSOutput main(VSInput input)
{
    VSOutput o;
    o.Position = mul(uViewProj, float4(input.aPosition, 1.0));

    // Rotate normals/tangents/bitangents into view space using the 3x3 part of uView.
    // Matches the CPU renderer which pre-rotates all geometry into view space, so
    // the lighting computation (in view space) produces identical results.
    float3x3 viewRot = (float3x3)uView;
    o.vWorldNormal = mul(viewRot, input.aNormal);
    o.vTangent     = mul(viewRot, input.aTangent);
    o.vBitangent   = mul(viewRot, input.aBitangent);

    o.vTexCoord    = input.aTexCoord;
    o.vVertexColor = input.aVertexColor;
    o.vDepth       = input.aPosition.z; // For back-to-front sorting diagnostics
    return o;
}
