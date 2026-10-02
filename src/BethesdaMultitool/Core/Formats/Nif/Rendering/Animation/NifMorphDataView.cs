namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     A lossless reading of the frame table of one 20.2.0.7 NiMorphData block (nif.xml NiMorphData and Morph: Num
///     Morphs, Num Vertices, Relative Targets, then per morph its Frame Name index and its vectors), read by
///     <see cref="NifGeomMorpherReader.TryReadMorphDataView" />. The vectors are walked for their extent only; the
///     cut-1a geometry reader types them.
/// </summary>
/// <param name="NumMorphs">The stored morph count (morph 0 is the Base).</param>
/// <param name="NumVertices">The stored vertex count of every morph.</param>
/// <param name="RelativeTargets">The Relative Targets byte as stored; the engine treats exactly 1 as relative (RE-23).</param>
/// <param name="FrameNameIndices">Each morph's Frame Name header string-table index as stored (-1 for none), in morph order.</param>
/// <param name="ConsumedExactly">True when the last morph's vectors end exactly where the block ends.</param>
internal sealed record NifMorphDataView(
    uint NumMorphs,
    uint NumVertices,
    byte RelativeTargets,
    int[] FrameNameIndices,
    bool ConsumedExactly)
{
    /// <summary>The morph count as an index bound.</summary>
    public int MorphCount => FrameNameIndices.Length;

    /// <summary>True when the engine applies the relative rule (RE-23 rule 1: the byte is exactly 1).</summary>
    public bool IsRelative => RelativeTargets == 1;
}
