using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     Where one transform track of a clip came from, for the clip extras' <c>tracks</c> map
///     (<see cref="NifModelAnimationExtras" />, SA10: Shared cannot target a single track, so track identity lives in
///     the payload). One entry per <see cref="SceneTransformTrack" /> of the clip, in track order.
/// </summary>
/// <param name="Source">The controlled-block ordinal (a sequence clip) or the NiTransformController block (the <c>(controllers)</c> clip).</param>
/// <param name="Interpolator">The interpolator block the track was mapped from.</param>
/// <param name="Property">The channel.</param>
/// <param name="Node">The exact document node the track drives.</param>
/// <param name="SquadPolicy">
///     For a <see cref="SceneInterpolation.GamebryoSquad" /> rotation, the Shared nested-normalization policy the file's
///     platform selected (RE-24, slice 16b); null for every other track.
/// </param>
/// <param name="SquadKeyType">For a Squad rotation, the stored key type it came from (2 QUADRATIC or 3 TBC); 0 otherwise.</param>
/// <param name="SquadPolicySource">For a Squad rotation, where the policy decision came from (<see cref="NifModelSquadPolicy.Source" />); null otherwise.</param>
internal sealed record NifModelTrackSource(
    int Source,
    int Interpolator,
    SceneTransformProperty Property,
    int Node,
    SceneGamebryoSquadPolicy? SquadPolicy = null,
    uint SquadKeyType = 0,
    string? SquadPolicySource = null);
