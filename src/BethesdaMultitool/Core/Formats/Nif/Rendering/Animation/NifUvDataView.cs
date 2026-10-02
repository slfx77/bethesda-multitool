namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     A lossless reading of one NiUVData block (nif.xml: UV Groups, four KeyGroup&lt;float&gt; in file order: U
///     translation, V translation, U scaling, V scaling), each group as the slice-1 key-group view. Read by
///     <see cref="NifUvDataReader.TryReadView" />; the renderer's <see cref="NifUvDataReader.TryRead" /> is a projection
///     of the same walk.
/// </summary>
/// <param name="UOffset">The first group: U translation.</param>
/// <param name="VOffset">The second group: V translation.</param>
/// <param name="UScale">The third group: U scaling (tiling).</param>
/// <param name="VScale">The fourth group: V scaling (tiling).</param>
/// <param name="ConsumedExactly">True when the fourth group ends exactly where the block ends.</param>
/// <remarks>The views reference the file bytes and are valid only while that buffer lives and is unchanged.</remarks>
internal readonly record struct NifUvDataView(
    NifKeyGroupView UOffset,
    NifKeyGroupView VOffset,
    NifKeyGroupView UScale,
    NifKeyGroupView VScale,
    bool ConsumedExactly)
{
    /// <summary>The four groups in file order.</summary>
    public IEnumerable<NifKeyGroupView> Groups
    {
        get
        {
            yield return UOffset;
            yield return VOffset;
            yield return UScale;
            yield return VScale;
        }
    }
}
