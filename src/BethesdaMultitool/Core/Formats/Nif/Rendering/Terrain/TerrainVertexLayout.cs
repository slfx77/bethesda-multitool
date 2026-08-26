using Vortice.Direct3D12;
using Vortice.DXGI;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Terrain;

/// <summary>
///     The input layouts <c>terrain_textured.vert.hlsl</c> is compiled against — one per
///     blend-quad permutation.
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
///     <para>
///         <b>Why a family and not one table.</b> A cell declares only the layer-weight quads it
///         actually uses (<see cref="TerrainBlendWeightPacking.QuadCountFor" />), and D3D12 rejects a
///         PSO whose vertex shader declares an input the layout omits — so the shader permutation and
///         the layout have to be chosen together. Quad count 0 is the depth-only and shadow variant:
///         those passes have no pixel shader and discard the weights, so they fetch none.
///     </para>
/// </summary>
internal static class TerrainVertexLayout
{
    /// <summary>Vertex-buffer slot carrying <see cref="TerrainVertex" />.</summary>
    public const int GeometrySlot = 0;

    /// <summary>Vertex-buffer slot carrying the per-vertex blend-weight stream.</summary>
    public const int BlendWeightSlot = 1;

    /// <summary>Layer-weight quads the widest variant declares — all 16 slots.</summary>
    public const int MaxBlendQuads = TerrainBlendWeightPacking.MaxQuadCount;

    /// <summary>
    ///     The three <see cref="TerrainVertex" /> attributes, present in every variant. Slot 0 was
    ///     the shared 72-byte <c>GpuMeshUploader.GpuVertex</c> until the terrain format shrink; its
    ///     texcoord/tangent/bitangent attributes were declared but read by neither terrain shader.
    ///     World X and Y left too — the shader rebuilds them from <c>SV_VertexID</c> and the
    ///     <see cref="TerrainCellGrid" /> root constants, which is why TEXCOORD0 is a single float
    ///     rather than a float3.
    /// </summary>
    private static readonly InputElementDescription[] GeometryElements =
    [
        new("TEXCOORD", 0, Format.R32_Float, 0, GeometrySlot), // aHeight (world Z)
        new("TEXCOORD", 1, Format.R16G16_SNorm, 4, GeometrySlot), // aNormalOct (octahedral)
        new("TEXCOORD", 2, Format.R8G8B8A8_UNorm, 8, GeometrySlot) // aVertexColor
    ];

    private static readonly InputElementDescription[][] ElementsByBlendQuads = BuildAll();

    /// <summary>
    ///     Layout for a variant reading <paramref name="blendQuadCount" /> layer-weight quads
    ///     (0..<see cref="MaxBlendQuads" />). Each quad is one <c>R16G16B16A16_UNORM</c> element:
    ///     UNORM16 on the wire, expanded to a <c>float4</c> by the input assembler before the shader
    ///     sees it. Sixteen weights match the 2D blit's per-pixel layer ceiling, so the 3D terrain
    ///     blend is non-lossy at the widest variant.
    /// </summary>
    public static InputElementDescription[] ElementsFor(int blendQuadCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(blendQuadCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(blendQuadCount, MaxBlendQuads);
        return ElementsByBlendQuads[blendQuadCount];
    }

    /// <summary>
    ///     Bytes per vertex in the blend-weight stream for this variant. Was a single constant 64
    ///     while the weights were <c>Vector4</c>s, then a constant 32 — see
    ///     <see cref="TerrainBlendWeightPacking" /> for why 16 bits is both sufficient and the right
    ///     stopping point, and why the quad count varies per cell.
    /// </summary>
    public static uint BlendWeightStrideFor(int blendQuadCount) =>
        (uint)TerrainBlendWeightPacking.BytesPerVertexFor(blendQuadCount);

    private static InputElementDescription[][] BuildAll()
    {
        var byQuads = new InputElementDescription[MaxBlendQuads + 1][];
        for (var quads = 0; quads <= MaxBlendQuads; quads++)
        {
            var elements = new InputElementDescription[GeometryElements.Length + quads];
            GeometryElements.CopyTo(elements, 0);
            for (var quad = 0; quad < quads; quad++)
            {
                // TEXCOORD3 + quad — aLayerWeights0..3, tightly packed at 8 bytes each.
                elements[GeometryElements.Length + quad] = new InputElementDescription(
                    "TEXCOORD",
                    (uint)(GeometryElements.Length + quad),
                    Format.R16G16B16A16_UNorm,
                    (uint)(quad * TerrainBlendWeightPacking.BytesPerQuad),
                    BlendWeightSlot);
            }

            byQuads[quads] = elements;
        }

        return byQuads;
    }
}
