namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Terrain;

/// <summary>
///     UNORM16 packing for the per-vertex terrain layer weights — 32 bytes a vertex for all 16
///     slots, against the 64 the four <c>Vector4</c>s cost, and only
///     <see cref="BytesPerVertexFor" /> of those 32 for a cell that does not use all 16.
///     <para>
///         <b>The shader does not change.</b> The input assembler expands
///         <c>R16G16B16A16_UNORM</c> to a <c>float4</c> before the vertex shader ever sees it, so
///         this is purely a change of what crosses the bus and sits in VRAM.
///     </para>
///     <para>
///         <b>Why 16 bits and not 8.</b> A weight's error becomes a colour error: the fragment
///         shader's output is Σ wᵢ·textureᵢ, so an error of ε in a weight moves the result by up to
///         ε of the difference between two layers. At UNORM16 that is 1/65535 — roughly 250× below
///         one step of an 8-bit output channel, i.e. it cannot change a single rendered pixel.
///         UNORM8's 1/255 is the same order as the output quantisation itself, so it would be
///         visible on a smooth two-layer gradient as banding along the blend. The extra 16 bytes a
///         vertex buy nothing back once terrain has stopped being the binding constraint, and the
///         alternative — switching format by distance — costs two pipeline variants, a re-upload
///         whenever a cell crosses the boundary, and a per-cell format state machine that degrades
///         worst exactly when flying fast.
///     </para>
///     <para>
///         Weights arrive already renormalised to sum to 1 and are non-negative, so the clamp below
///         is defensive rather than load-bearing — but a weight that arrived at 1.00005 from the
///         renormalisation tolerance would otherwise wrap to near zero, which is the kind of thing
///         that shows up as one black vertex in one cell.
///     </para>
/// </summary>
internal static class TerrainBlendWeightPacking
{
    /// <summary>UNORM16 scale: D3D12 decodes value/65535, so 1.0 is exactly 65535.</summary>
    public const float UnormScale = 65535f;

    /// <summary>Layer weights in one packed quad — one <c>R16G16B16A16_UNORM</c> vertex element.</summary>
    public const int SlotsPerQuad = 4;

    /// <summary>Bytes one quad costs per vertex.</summary>
    public const int BytesPerQuad = SlotsPerQuad * sizeof(ushort);

    /// <summary>Quads needed for all <see cref="Textures.CellTerrainTextureSet.MaxSlots" /> slots.</summary>
    public const int MaxQuadCount = Textures.CellTerrainTextureSet.MaxSlots / SlotsPerQuad;

    /// <summary>
    ///     Bytes one vertex's full 16-slot weight set occupies on the wire — the <b>worst case</b>,
    ///     which only a cell painted with more than 12 distinct land textures actually pays. Use
    ///     <see cref="BytesPerVertexFor" /> for a specific cell; this constant is the right number
    ///     wherever an upper bound is what is wanted (a scratch buffer, a residency floor).
    /// </summary>
    public const int BytesPerVertex = Textures.CellTerrainTextureSet.MaxSlots * sizeof(ushort);

    /// <summary>
    ///     Quads a cell with <paramref name="activeSlotCount" /> populated slots needs:
    ///     <c>ceil(slots / 4)</c>, clamped to at least one.
    ///     <para>
    ///         This is the whole of phase 3d, and it is <b>exact rather than lossy</b>: the slot →
    ///         LTEX map is constant across a cell, so every weight past
    ///         <see cref="Textures.CellTerrainTextureSet.ActiveSlotCount" /> is zero at every vertex
    ///         of that cell. A typical cell paints 2–6 textures, so quads 2 and 3 are 16 bytes of
    ///         zeroes per vertex — 16,641 vertices of them on a Fallout 76 cell.
    ///     </para>
    ///     <para>
    ///         The floor of one quad matters: a cell with no LAND texture data at all still renders,
    ///         through the fragment shader's <c>totalWeight</c> fallback to slot 0's engine default,
    ///         and that path still needs an all-zero quad 0 to read. A zero-quad geometry stream
    ///         would also leave the vertex shader with no <c>TEXCOORD3</c> to declare, which is a
    ///         different permutation entirely — the one the depth-only and shadow passes use.
    ///     </para>
    /// </summary>
    public static int QuadCountFor(int activeSlotCount)
    {
        // Clamp the SLOT count, not the quad count. Rounding up first overflows for a slot count
        // near int.MaxValue, and the wrapped negative then clamps to one quad — the one direction
        // that is not safe, since it would drop every layer past the fourth.
        var slots = Math.Clamp(activeSlotCount, 1, Textures.CellTerrainTextureSet.MaxSlots);
        return (slots + SlotsPerQuad - 1) / SlotsPerQuad;
    }

    /// <summary>Wire bytes per vertex for a cell sized to <paramref name="quadCount" /> quads.</summary>
    public static int BytesPerVertexFor(int quadCount) =>
        Math.Clamp(quadCount, 0, MaxQuadCount) * BytesPerQuad;

    /// <summary>
    ///     Packs a weight in [0, 1]. Out-of-range inputs clamp rather than wrap, and a non-finite
    ///     one becomes zero — <c>Math.Clamp</c> passes NaN straight through, and casting NaN to an
    ///     integer is unspecified in .NET, so a corrupt weight would otherwise produce whatever the
    ///     hardware happened to do.
    /// </summary>
    public static ushort Pack(float weight)
    {
        if (!float.IsFinite(weight) || weight <= 0f)
        {
            return 0;
        }

        return weight >= 1f ? (ushort)UnormScale : (ushort)MathF.Round(weight * UnormScale);
    }

    /// <summary>Unpacks exactly as the input assembler does, so a CPU check means what the GPU sees.</summary>
    public static float Unpack(ushort packed) => packed / UnormScale;

    /// <summary>Round-trip error for a finite <paramref name="weight" />. Diagnostics and tests.</summary>
    public static float RoundTripError(float weight)
    {
        var clamped = float.IsFinite(weight) ? Math.Clamp(weight, 0f, 1f) : 0f;
        return MathF.Abs(clamped - Unpack(Pack(weight)));
    }
}
