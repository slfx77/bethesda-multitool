// v3 Phase 5 skybox vertex shader. Emits a single fullscreen triangle (no vertex buffer) and
// reconstructs the per-pixel world-space view ray by unprojecting the near + far plane points with
// the inverse view-projection. uInvViewProj is uploaded DIRECTLY (same matrix convention as the
// scene's forward mul(uViewProj, worldPos)), so mul(uInvViewProj, clip) is the exact inverse.
// Drawn FIRST with depth test/write OFF, so it fills the cleared color target behind all geometry.

cbuffer SkyParams : register(b0)
{
    float4x4 uInvViewProj;
};

struct VSOutput
{
    float4 Position : SV_Position;
    float3 vRayDir  : TEXCOORD0;
};

VSOutput main(uint vid : SV_VertexID)
{
    // Fullscreen triangle: NDC (0,0),(2,0),(0,2) → clip (-1,-1),(3,-1),(-1,3), covering [-1,1]².
    float2 ndc = float2((vid << 1) & 2, vid & 2);
    float2 clipXY = ndc * 2.0 - 1.0;

    VSOutput o;
    // Reversed-Z: the far plane is z=0. Emit there; depth-write is off so this z is never stored, and
    // any real geometry (depth > 0) overwrites the sky in the normal depth pass that follows.
    o.Position = float4(clipXY, 0.0, 1.0);

    // World view ray = unproject(far) - unproject(near). Reversed-Z: near z=1, far z=0.
    float4 nearH = mul(uInvViewProj, float4(clipXY, 1.0, 1.0));
    float4 farH  = mul(uInvViewProj, float4(clipXY, 0.0, 1.0));
    o.vRayDir = (farH.xyz / farH.w) - (nearH.xyz / nearH.w);
    return o;
}
