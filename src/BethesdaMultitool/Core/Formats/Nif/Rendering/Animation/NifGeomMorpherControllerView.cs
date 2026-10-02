namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     A lossless reading of one 20.2.0.7 NiGeomMorpherController block (nif.xml NiGeomMorpherController: the
///     NiTimeController header, Morpher Flags, Data, Always Update, Num Interpolators and the Interpolator Weights
///     items), read by <see cref="NifGeomMorpherReader.TryReadControllerView" />. Nothing is validated beyond what
///     bounds the read; the renderer's <see cref="NifGeometryMorphReader" /> keeps its own admission rules.
/// </summary>
/// <param name="Header">The NiTimeController header (next controller, flags, clock, target).</param>
/// <param name="MorpherFlags">The Morpher Flags word as stored (GeomMorpherFlags).</param>
/// <param name="DataRef">The Data ref (NiMorphData) as stored.</param>
/// <param name="AlwaysUpdate">The Always Update byte as stored.</param>
/// <param name="Items">The Interpolator Weights items in file order; item j drives morph j (slot 0 is the Base).</param>
/// <param name="ConsumedExactly">True when the last item ends exactly where the block ends.</param>
internal sealed record NifGeomMorpherControllerView(
    NifTimeControllerHeader Header,
    ushort MorpherFlags,
    int DataRef,
    byte AlwaysUpdate,
    NifMorphWeightView[] Items,
    bool ConsumedExactly);
