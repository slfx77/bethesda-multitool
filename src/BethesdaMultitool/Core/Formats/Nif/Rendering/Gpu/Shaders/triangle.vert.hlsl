// Phase 0b placeholder vertex shader — proves the swapchain + shader pipeline
// works. Renders a single colored triangle that rotates over time.
// Replaced in Phases 1+ by the terrain + REFR shaders.

cbuffer Uniforms : register(b0)
{
    float4 uRotation; // x = cos(theta), y = sin(theta), zw = unused
    float4 uAspect;   // x = aspectInverse (height/width), yzw = unused
};

struct VSInput
{
    float2 aPosition : TEXCOORD0;
    float4 aColor    : TEXCOORD1;
};

struct VSOutput
{
    float4 Position : SV_Position;
    float4 vColor   : COLOR0;
};

VSOutput main(VSInput input)
{
    // 2D rotation by (cos, sin)
    float2 rotated = float2(
        input.aPosition.x * uRotation.x - input.aPosition.y * uRotation.y,
        input.aPosition.x * uRotation.y + input.aPosition.y * uRotation.x);

    // Preserve aspect ratio so the triangle isn't squashed when the panel is non-square.
    rotated.x *= uAspect.x;

    VSOutput o;
    o.Position = float4(rotated, 0.0, 1.0);
    o.vColor = input.aColor;
    return o;
}
