// v3 Phase 2c+ engine-accurate terrain vertex shader. Replaces the prior per-quadrant
// origin/scale math with per-vertex blend weights: each vertex carries a Vector4 of weights
// into up to 4 cell-wide diffuse texture slots, sourced from `CellTerrainTextureSet`. The
// per-pixel weighted sum in the fragment shader reproduces the engine's per-vertex blend
// table behaviour, so cross-quadrant boundaries and (with neighbor-fed weight tables)
// cross-cell boundaries fade smoothly instead of the prior hard seam.
//
// Slot 0 vertex stream: TerrainVertex — 12 bytes (height float, octahedral SNORM16 normal,
// R8G8B8A8 colour). It used to be the shared 72-byte GpuMeshUploader.GpuVertex, three of whose
// six fields this path never read: the UV below is derived from the world position, and the
// fragment shader builds its tangent frame analytically from the geometric normal, so the texcoord,
// tangent and bitangent were carried for nothing. World X and Y are not stored either — a LAND cell
// is a regular grid, so they are rebuilt here from SV_VertexID and the b4 root constants.
// Slot 1 vertex stream: per-vertex aLayerWeights (float4), built per-cell from the engine-
// accurate per-vertex weight table and uploaded as an independent GPU buffer per cell.

cbuffer PerFrame : register(b0)
{
    float4x4 uViewProj;
};

// b1 = PerCell texture indices, consumed by the fragment shader. b2 = PerMode
// (debug + UV scale). Per-quadrant constants from the prior layout are gone; everything
// per-cell varies via bindless texture indices + the per-vertex weight stream.
cbuffer PerMode : register(b2)
{
    // x = 1.0 → show diffuse terrain textures (consumed by the fragment shader)
    // y = diffuse UV scale (world units → texture repeats; 1/512 = 8 repeats per cell)
    // z = apply VCLR; w = FNV terrain-normal gate (both consumed by the fragment shader)
    float4 uDebugMode_UvScale_Pad;
};

// 1G — camera-relative render origin (xyz) from the shared atmosphere CB (b3). Subtracted from each
// world vertex before projection (kills the worldspace-edge wobble). Zero when camera-relative is off;
// the leading 9 float4 are the sun/sky/fog/camera fields this VS does not use.
#include "atmosphere.hlsli"

// Octahedral SNORM16 -> unit normal. Mirrors TerrainNormalPacking.Decode step for step (including
// the >= 0 tie-break, so a zero component folds the same way on both sides); the two must agree or
// a CPU-side assertion about what the shader sees means nothing. Branchless fold: for a point that
// landed in the lower hemisphere (z < 0), x' = x - sign(x)*(-z) = sign(x)*(1 - |y|), which is the
// mirror-and-unfold the encoder applied.
float3 DecodeOctNormal(float2 e)
{
    float3 n = float3(e.x, e.y, 1.0 - abs(e.x) - abs(e.y));
    float t = saturate(-n.z);
    n.xy += float2(n.x >= 0.0 ? -t : t, n.y >= 0.0 ? -t : t);
    return normalize(n);
}

// Per-cell grid, as four VS-only root constants. Root constants rather than a per-cell CBV because
// the shadow and depth-only terrain passes deliberately bind no per-cell constant buffer.
cbuffer TerrainCellGrid : register(b4)
{
    float2 uCellOriginXy;
    float  uVertexSpacing;
    uint   uGridSize;
};

struct VSInput
{
    // Slot 0 — TerrainVertex, 12 bytes.
    float  aHeight       : TEXCOORD0;  // R32_FLOAT — world Z; X/Y come from uVertexId
    float2 aNormalOct    : TEXCOORD1;  // R16G16_SNORM octahedral
    float4 aVertexColor  : TEXCOORD2;  // R8G8B8A8_UNORM
    // Slot 1 — 16 per-vertex layer weights as four float4s (slots 0..3, 4..7, 8..11, 12..15).
    float4 aLayerWeights0 : TEXCOORD3;
    float4 aLayerWeights1 : TEXCOORD4;
    float4 aLayerWeights2 : TEXCOORD5;
    float4 aLayerWeights3 : TEXCOORD6;
    // Not a vertex buffer: the index buffer value, which for the shared LAND index buffer IS
    // j * gridSize + i. Every terrain draw is DrawIndexedInstanced with BaseVertexLocation 0.
    uint   aVertexId      : SV_VertexID;
};

struct VSOutput
{
    float4 Position      : SV_Position;
    float3 vWorldNormal  : TEXCOORD0;
    float4 vVertexColor  : TEXCOORD1;
    float2 vWorldUv      : TEXCOORD2;
    float4 vLayerWeights0 : TEXCOORD3;
    float4 vLayerWeights1 : TEXCOORD4;
    float4 vLayerWeights2 : TEXCOORD5;
    float4 vLayerWeights3 : TEXCOORD6;
    float3 vWorldPos     : TEXCOORD7;  // world-space position for per-pixel distance fog
};

VSOutput main(VSInput input)
{
    VSOutput o;

    // Rebuild the horizontal position the CPU no longer sends. `precise` forbids the compiler from
    // reassociating or contracting this into a fused multiply-add: the result is bit-identical
    // either way for every grid the games use (origin, index*spacing and their sum are all
    // integer-valued floats well inside 2^24), but the guarantee is what keeps a cell's east column
    // and its neighbour's west column landing on the SAME float. One ULP of disagreement there is a
    // hairline crack along every cell boundary in the worldspace.
    uint gridIndexX = input.aVertexId % uGridSize;
    uint gridIndexY = input.aVertexId / uGridSize;
    precise float worldX = uCellOriginXy.x + (float)gridIndexX * uVertexSpacing;
    precise float worldY = uCellOriginXy.y + (float)gridIndexY * uVertexSpacing;
    float3 worldPos = float3(worldX, worldY, input.aHeight);

    // Camera-relative: shift the vertex by -origin before projection. UVs keep ABSOLUTE world XY so the
    // terrain texture tiling is unchanged. vWorldPos becomes camera-relative to match the shader's
    // camera (= 0 in this mode) for distance fog.
    float3 worldPosRel = worldPos - uCameraOrigin.xyz;
    o.Position = mul(uViewProj, float4(worldPosRel, 1.0));
    o.vWorldNormal = DecodeOctNormal(input.aNormalOct);
    o.vVertexColor = input.aVertexColor;
    o.vWorldUv = worldPos.xy * uDebugMode_UvScale_Pad.y;
    o.vLayerWeights0 = input.aLayerWeights0;
    o.vLayerWeights1 = input.aLayerWeights1;
    o.vLayerWeights2 = input.aLayerWeights2;
    o.vLayerWeights3 = input.aLayerWeights3;
    o.vWorldPos = worldPosRel; // camera-relative world pos (matches the shader camera = 0 for fog)
    return o;
}
