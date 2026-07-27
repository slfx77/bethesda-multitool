// Fullscreen-triangle vertex shader for the tonemap resolve pass. Emits one oversized triangle from
// SV_VertexID (no vertex buffer) that covers the whole target; the pixel shader samples the HDR scene
// texture and maps it to the 8-bit display target. The classic 3-vertex trick: verts at (-1,-1),
// (3,-1), (-1,3) in clip space cover [-1,1]² with UVs (0,0),(2,0),(0,2) — the off-screen excess is
// clipped, leaving exactly the viewport with no seam and better cache behaviour than a quad.

struct VSOutput
{
    float4 Position : SV_Position;
    float2 vUv      : TEXCOORD0;
};

VSOutput main(uint vid : SV_VertexID)
{
    VSOutput o;
    o.vUv = float2((vid << 1) & 2, vid & 2);        // (0,0), (2,0), (0,2)
    o.Position = float4(o.vUv * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
    return o;
}
