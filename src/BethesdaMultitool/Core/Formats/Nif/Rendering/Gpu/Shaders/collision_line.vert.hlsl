// Collision-overlay lines as screen-space quads: each wireframe edge (two float3 entries in the
// persistent line buffer, bound as a root SRV at t8/space0) expands to 6 vertices from
// SV_VertexID alone — no input-assembler vertex stream. The PS applies an analytic pixel-space
// feather, replacing the fixed-function AntialiasedLineEnable rasterizer state whose quality is
// GPU/driver/output dependent (the source of the "aliased on one monitor, clean on the other"
// report). All widths are computed in render-target pixels, so the result is deterministic on
// every adapter, monitor, and DPI scale.

cbuffer Uniforms : register(b0)
{
    float4x4 uViewProj;
    float4   uLineColor;    // rgba
    float4   uLineParams;   // x,y = viewport size px; z = core half-width px; w = feather px
};

StructuredBuffer<float3> uLineVertices : register(t8, space0);

struct VSOutput
{
    float4 Position : SV_Position;
    // Pixel-space distances must interpolate linearly in SCREEN space — the two segment ends
    // carry different w, so perspective-correct interpolation would bend the feather.
    noperspective float2 vDistPx : TEXCOORD0; // x = signed lateral px, y = longitudinal px
    nointerpolation float vSegLenPx : TEXCOORD1;
};

VSOutput main(uint vid : SV_VertexID)
{
    uint seg    = vid / 6u;
    uint corner = vid % 6u;                              // two CCW triangles: 0,1,2 / 2,1,3
    uint quadId = corner == 3u ? 2u : corner == 4u ? 1u : corner == 5u ? 3u : corner;
    float endT  = (quadId & 1u) != 0u ? 1.0 : 0.0;       // 0 = p0 end, 1 = p1 end
    float side  = (quadId & 2u) != 0u ? 1.0 : -1.0;      // across the line

    float4 c0 = mul(uViewProj, float4(uLineVertices[seg * 2u + 0u], 1.0));
    float4 c1 = mul(uViewProj, float4(uLineVertices[seg * 2u + 1u], 1.0));

    // Homogeneous near-plane clip: the cage surrounds the camera (depth-ignoring overlay), so
    // segments legitimately cross w <= 0. Slide the behind endpoint onto w = eps; both behind
    // emits a clipped degenerate (z outside [0,1] with w = 1).
    const float kEps = 1e-4;
    VSOutput o = (VSOutput)0;
    if (c0.w < kEps && c1.w < kEps) { o.Position = float4(0.0, 0.0, 2.0, 1.0); return o; }
    if (c0.w < kEps) { c0 = lerp(c0, c1, (kEps - c0.w) / (c1.w - c0.w)); }
    else if (c1.w < kEps) { c1 = lerp(c1, c0, (kEps - c1.w) / (c0.w - c1.w)); }

    float2 halfVp = uLineParams.xy * 0.5;
    float2 s0 = (c0.xy / c0.w) * float2(1.0, -1.0) * halfVp;  // screen px, y-down
    float2 s1 = (c1.xy / c1.w) * float2(1.0, -1.0) * halfVp;
    float2 dirPx = s1 - s0;
    float lenPx = length(dirPx);
    dirPx = lenPx > 1e-3 ? dirPx / lenPx : float2(1.0, 0.0);
    float2 nrmPx = float2(-dirPx.y, dirPx.x);

    float halfW = uLineParams.z + uLineParams.w;              // core + feather
    float2 endPx = lerp(s0, s1, endT);
    // Extended square caps: push each end outward by halfW so adjacent edges seal their corners.
    float2 posPx = endPx + nrmPx * (side * halfW)
                         + dirPx * (endT * 2.0 - 1.0) * halfW;
    float4 cEnd = lerp(c0, c1, endT);
    o.Position = float4(posPx / halfVp * float2(1.0, -1.0) * cEnd.w, cEnd.z, cEnd.w);
    o.vDistPx = float2(side * halfW, lerp(-halfW, lenPx + halfW, endT));
    o.vSegLenPx = lenPx;
    return o;
}
