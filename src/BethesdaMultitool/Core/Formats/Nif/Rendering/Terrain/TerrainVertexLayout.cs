using BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Terrain;

/// <summary>
///     The input layout <c>terrain_textured.vert.hlsl</c> is compiled against.
///     <para>
///         This lives outside the renderer, and outside the <c>WINDOWS_GUI</c> compilation gate,
///         because it is an <b>ABI</b> rather than renderer state: the offsets here have to agree
///         with <see cref="TerrainVertex" />'s field order and the semantics have to agree with the
///         shader's <c>VSInput</c>. A disagreement in the offsets does not fail to bind — it decodes
///         whatever bytes happen to sit at the stated offset, so a cell renders as folded geometry
///         with no error anywhere. Keeping the table reachable from the cross-platform TFM is what
///         lets a headless test reflect the compiled shader and check the two against each other,
///         rather than pinning the source text and hoping.
///     </para>
/// </summary>
internal static class TerrainVertexLayout
{
    /// <summary>Vertex-buffer slot carrying <see cref="TerrainVertex" />.</summary>
    public const int GeometrySlot = 0;

    /// <summary>Vertex-buffer slot carrying the per-vertex blend-weight stream.</summary>
    public const int BlendWeightSlot = 1;

    /// <summary>
    ///     Bytes per vertex in the blend-weight stream: <see cref="CellTerrainTextureSet.SlotVectors" />
    ///     float4s, i.e. 16 layer weights.
    /// </summary>
    public const uint BlendWeightStride = CellTerrainTextureSet.SlotVectors * 16;

    /// <summary>
    ///     Slot 0 is <see cref="TerrainVertex" /> (TEXCOORD0..2, 12 bytes); slot 1 is the per-cell
    ///     blend-weight stream — four float4s per vertex (TEXCOORD3..6), uploaded alongside the
    ///     geometry in the same arena range. Sixteen weights match the 2D blit's per-pixel layer
    ///     ceiling, so the 3D terrain blend is non-lossy.
    ///     <para>
    ///         Slot 0 was the shared 72-byte <c>GpuMeshUploader.GpuVertex</c> until the terrain format
    ///         shrink; its texcoord/tangent/bitangent attributes were declared but read by neither
    ///         terrain shader. World X and Y left too — the shader rebuilds them from
    ///         <c>SV_VertexID</c> and the <see cref="TerrainCellGrid" /> root constants, which is why
    ///         TEXCOORD0 is a single float rather than a float3.
    ///     </para>
    /// </summary>
    public static InputElementDescription[] Elements { get; } =
    [
        new("TEXCOORD", 0, Format.R32_Float, 0, GeometrySlot), // aHeight (world Z)
        new("TEXCOORD", 1, Format.R16G16_SNorm, 4, GeometrySlot), // aNormalOct (octahedral)
        new("TEXCOORD", 2, Format.R8G8B8A8_UNorm, 8, GeometrySlot), // aVertexColor
        new("TEXCOORD", 3, Format.R32G32B32A32_Float, 0, BlendWeightSlot), // aLayerWeights0 (slots 0..3)
        new("TEXCOORD", 4, Format.R32G32B32A32_Float, 16, BlendWeightSlot), // aLayerWeights1 (slots 4..7)
        new("TEXCOORD", 5, Format.R32G32B32A32_Float, 32, BlendWeightSlot), // aLayerWeights2 (slots 8..11)
        new("TEXCOORD", 6, Format.R32G32B32A32_Float, 48, BlendWeightSlot) // aLayerWeights3 (slots 12..15)
    ];
}
