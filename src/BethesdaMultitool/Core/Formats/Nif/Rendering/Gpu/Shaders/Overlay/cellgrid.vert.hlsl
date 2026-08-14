// Shared unlit overlay vertex shader for cell-grid lines, navmesh fill/edges, and selection outlines.
// Projects caller-supplied world-space vertices and forwards the configured RGBA blend color.

cbuffer Uniforms : register(b0)
{
    float4x4 uViewProj;
    float4 uLineColor; // RGBA; alpha actively controls each overlay renderer's blend.
};

struct VSInput
{
    float3 aPosition : TEXCOORD0;
};

struct VSOutput
{
    float4 Position : SV_Position;
    float4 vColor   : COLOR0;
};

VSOutput main(VSInput input)
{
    VSOutput o;
    // System.Numerics row-major bytes → HLSL column-major interpretation = transpose, so
    // `mul(uViewProj, ...)` applies the math correctly. Same pattern as skin.vert.hlsl.
    o.Position = mul(uViewProj, float4(input.aPosition, 1.0));
    o.vColor = uLineColor;
    return o;
}
