// v3 Phase 1 placeholder vertex shader — projects cell-boundary line vertices to clip space.
// Replaced by real terrain + REFR shaders in Phases 2–3.

cbuffer Uniforms : register(b0)
{
    float4x4 uViewProj;
    float4 uLineColor; // rgba (alpha kept for future fade)
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
