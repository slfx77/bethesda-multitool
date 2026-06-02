// v3 Phase 2b textured-terrain vertex shader. Projects heightmap vertices to clip space,
// derives world-space UV for diffuse sampling, and emits a per-quadrant local UV that the
// pixel shader uses to sample the 17x17 R8 opacity textures uploaded from VTXT BlendEntries.
//
// The cell-local TEXCOORD on the vertex carries (i/32, j/32). The pixel shader's quadrant
// UV is (cellLocalUv - quadrantOrigin) * 2 so it ranges [0, 1] within each 17x17 quadrant.
// quadrantOrigin is one of {(0,0), (0.5,0), (0,0.5), (0.5,0.5)} for Q=0..3 (SW, SE, NW, NE).
//
// Matrix bytes: System.Numerics.Matrix4x4 stores row-major in CPU memory; HLSL cbuffers
// interpret matrix bytes column-major by default. CPU side does NOT transpose, so
// `mul(M, v)` matches `M * v` on the CPU (same convention as terrain.vert.hlsl).

cbuffer PerFrame    : register(b0)
{
    float4x4 uViewProj;
};

cbuffer PerQuadrant : register(b1)
{
    // xy = quadrant origin in cell-local UV (0|0.5, 0|0.5)
    // z  = layer count (1..4)
    // w  = diffuse UV scale (world units -> texture repeats; e.g. 1/256 = 16 tiles per cell)
    float4 uQuadrantUvOrigin_LayerCount_UvScale;
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
    float2 vWorldUv     : TEXCOORD2;
    float2 vQuadrantUv  : TEXCOORD3;
};

VSOutput main(VSInput input)
{
    VSOutput o;
    o.Position = mul(uViewProj, float4(input.aPosition, 1.0));
    o.vWorldNormal = input.aNormal;
    o.vVertexColor = input.aVertexColor;
    o.vWorldUv = input.aPosition.xy * uQuadrantUvOrigin_LayerCount_UvScale.w;
    o.vQuadrantUv = (input.aTexCoord - uQuadrantUvOrigin_LayerCount_UvScale.xy) * 2.0;
    return o;
}
