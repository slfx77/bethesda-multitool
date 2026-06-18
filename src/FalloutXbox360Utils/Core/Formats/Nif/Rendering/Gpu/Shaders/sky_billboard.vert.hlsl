// v3 textured-sky billboard vertex shader. Expands a camera-facing quad (4 verts, triangle strip)
// from SV_VertexID, centered on a world direction at a fixed sky-sphere radius. Used for the sun disc,
// the sun glare halo, and the moon — the engine draws each celestial object as a billboard quad with a
// NiBillboard camera-facing controller (decompiled Sun::Initialize / Moon::Initialize, both build a
// 4-vertex quad). Drawn depth-OFF right after the sky gradient, so depth-written terrain occludes it.
//
// The quad is built in WORLD space (center = camPos + dir*radius; corners offset by the camera's world
// right/up * halfSize) and projected with the main viewProj — so the object sits in the sky at the
// right screen position and is correctly hidden behind mountains once terrain draws over it.

cbuffer Billboard : register(b0)
{
    float4x4 uViewProj;
    float4 uCenterDirRadius; // xyz = unit world direction to the object, w = sky-sphere radius
    float4 uRightHalfSize;   // xyz = camera world right, w = quad half-size (world units)
    float4 uUpFade;          // xyz = camera world up, w = fade (0..1)
    float4 uCamPos;          // xyz = camera world pos, w unused
    float4 uTint;            // rgb = tint color, a = base alpha
    uint4  uTexIndex;        // x = bindless texture index (passed as a real uint — never float-packed:
                             // a small index reinterpreted as a float is a denormal and may flush to 0)
};

struct VSOutput
{
    float4 Position : SV_Position;
    float2 vUv      : TEXCOORD0;
};

// Triangle-strip quad corners (v00, v10, v01, v11).
static const float2 kCorners[4] =
{
    float2(-1.0, -1.0), float2(1.0, -1.0), float2(-1.0, 1.0), float2(1.0, 1.0)
};

VSOutput main(uint vid : SV_VertexID)
{
    float2 c = kCorners[vid];
    float3 center = uCamPos.xyz + uCenterDirRadius.xyz * uCenterDirRadius.w;
    float3 world = center + ((uRightHalfSize.xyz * c.x) + (uUpFade.xyz * c.y)) * uRightHalfSize.w;

    VSOutput o;
    o.Position = mul(uViewProj, float4(world, 1.0));
    o.vUv = (c * 0.5) + 0.5;
    return o;
}
