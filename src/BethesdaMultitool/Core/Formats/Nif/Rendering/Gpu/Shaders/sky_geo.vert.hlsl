// Sky-GEOMETRY vertex shader. Draws the REAL climate sky-dome NIF meshes (atmosphere / stars / clouds
// layers, extracted from the game's own Sky\*.nif), camera-centered so the dome stays at infinity. Using
// the authored geometry + UVs is what removes the procedural dome's tiling-stretch, horizon seam, and
// "too far" feel: the mesh covers the sky exactly as the artists built it. +Z is up.

cbuffer SkyGeo : register(b0)
{
    float4x4 uViewProj;
    float4 uCamPosScale;   // xyz = camera world pos, w = sky-dome radius (same for every layer)
    float4 uTintParam;     // rgb = layer tint, a = layer fade/opacity
    float4 uScrollMode;    // xy = cloud UV scroll, z = mode (0 gradient, 1 stars, 2 clouds), w unused
    uint4  uTexIndex;      // x = bindless diffuse index (0xFFFFFFFF = none)
};

struct VSInput
{
    float3 Pos   : TEXCOORD0;
    float2 Uv    : TEXCOORD1;
    float4 Color : COLOR0;     // NIF per-vertex RGBA; ALPHA = the artist-baked cloud-dome horizon fade
};

struct VSOutput
{
    float4 Position : SV_Position;
    float3 vDir     : TEXCOORD0; // mesh-local position (camera-centered) -> view direction in PS
    float2 vUv      : TEXCOORD1;
    float4 vColor   : COLOR0;
};

VSOutput main(VSInput input)
{
    VSOutput o;
    // Place every vertex on ONE sphere of radius uCamPosScale.w centered on the camera, by its DIRECTION
    // from the dome origin. This collapses the sky NIFs' very different authored sizes/centers (stars ±16,
    // clouds ±500 at varying heights) into concentric same-radius domes that overlay into one sky, while
    // keeping each vertex's direction so the star/cloud shapes stay angularly correct. A degenerate (near-
    // zero) position keeps a stable up-direction. +Z is up.
    float len = length(input.Pos);
    float3 dir = len > 1e-4 ? input.Pos / len : float3(0.0, 0.0, 1.0);
    float3 world = uCamPosScale.xyz + (dir * uCamPosScale.w);
    // The cbuffer matrix is column-major (CPU uploads System.Numerics row-major WITHOUT transpose), so
    // the projection is mul(M, v) — matrix FIRST — exactly like reference.vert/the old skydome.vert.
    o.Position = mul(uViewProj, float4(world, 1.0));
    o.vDir = dir;
    o.vUv = input.Uv;
    o.vColor = input.Color;
    return o;
}
