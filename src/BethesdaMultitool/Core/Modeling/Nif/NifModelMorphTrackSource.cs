namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     Where one emitted morph track of a clip came from, for the clip extras' <c>morphTracks</c> map
///     (<see cref="NifModelAnimationExtras" />, SA10: Shared cannot target a single track, so track identity lives in the
///     payload). One entry per emitted <see cref="Slfx77.Multitool.Core.Models.SceneMorphTargetTrack" /> (form
///     <see cref="TargetForm" />) or <see cref="Slfx77.Multitool.Core.Models.SceneMorphTrack" /> (form <see cref="VectorForm" />).
/// </summary>
/// <param name="Source">The controlled-block ordinal (a sequence clip) or the NiGeomMorpherController block (the <c>(controllers)</c> clip).</param>
/// <param name="Node">The exact document node the track drives.</param>
/// <param name="Form"><see cref="TargetForm" /> or <see cref="VectorForm" />.</param>
/// <param name="Interpolator">The interpolator block behind a per-target track; -1 for a stored weight or the vector form.</param>
/// <param name="Morph">The morph index behind a per-target track; -1 for the vector form.</param>
/// <param name="Target">The cut-1a target index behind a per-target track; -1 for the vector form.</param>
/// <param name="Slots">For the vector form, each target's (morph index, interpolator block) in target order; null otherwise.</param>
internal sealed record NifModelMorphTrackSource(
    int Source,
    int Node,
    string Form,
    int Interpolator,
    int Morph,
    int Target,
    IReadOnlyList<(int Morph, int Interpolator)>? Slots)
{
    /// <summary>The form of a per-target track.</summary>
    public const string TargetForm = "target";

    /// <summary>The form of a whole-vector track.</summary>
    public const string VectorForm = "vector";
}
