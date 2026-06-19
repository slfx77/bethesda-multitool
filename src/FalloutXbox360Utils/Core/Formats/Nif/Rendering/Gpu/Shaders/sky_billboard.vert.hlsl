// v3 textured-sky billboard vertex shader. Expands a DOME-ANCHORED quad (4 verts, triangle strip)
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
    float3 dir = uCenterDirRadius.xyz;
    float3 center = uCamPos.xyz + dir * uCenterDirRadius.w;

    // Dome-anchored basis: the quad faces ALONG its own world direction (the line of sight from the
    // camera at the dome centre to the body), so the disc stays a true circle from any view angle —
    // unlike the camera-facing basis (uRightHalfSize.xyz / uUpFade.xyz, now unused), which tilts a body
    // toward the screen plane when it sits at the periphery of the view. uRightHalfSize.w is the quad
    // half-size; uUpFade.w (fade) is still consumed by the fragment shader.
    float3 worldUp = float3(0.0, 0.0, 1.0);
    float3 right = cross(worldUp, dir);
    float rl = length(right);
    right = rl > 1e-4 ? (right / rl) : float3(1.0, 0.0, 0.0); // body straight up/down → arbitrary right
    float3 up = cross(dir, right);
    float3 world = center + ((right * c.x) + (up * c.y)) * uRightHalfSize.w;

    VSOutput o;
    o.Position = mul(uViewProj, float4(world, 1.0));
    o.vUv = (c * 0.5) + 0.5;
    return o;
}
