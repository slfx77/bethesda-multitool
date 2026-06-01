// v3 Phase 2a terrain vertex shader — projects heightmap vertices to clip space and
// passes world-space normal + vertex color through to the pixel shader for simple Lambert
// lighting. Input layout matches GpuMeshUploader.InputElements so TerrainRenderer can
// reuse the same input layout binding without diverging from the NIF sprite path.
//
// Matrix bytes: System.Numerics.Matrix4x4 stores row-major in CPU memory; HLSL cbuffers
// interpret matrix bytes column-major by default. The CPU side does NOT transpose, so
// the in-shader matrix is the transpose of the C# matrix, and `mul(M, v)` produces the
// same result as `M * v` on the CPU (same convention as cellgrid.vert.hlsl + skin.vert.hlsl).

cbuffer Uniforms : register(b0)
{
    float4x4 uViewProj;
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
    float4 vVertexColor : TEXCOORD1;
};

VSOutput main(VSInput input)
{
    VSOutput o;
    o.Position = mul(uViewProj, float4(input.aPosition, 1.0));
    o.vWorldNormal = input.aNormal;
    o.vVertexColor = input.aVertexColor;
    return o;
}
