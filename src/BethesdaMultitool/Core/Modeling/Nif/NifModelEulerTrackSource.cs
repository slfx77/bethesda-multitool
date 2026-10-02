namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     Where one Euler rotation track of a clip came from, for the clip extras' <c>eulerTracks</c> map
///     (<see cref="NifModelAnimationExtras" />, SA10). One entry per <see cref="Slfx77.Multitool.Core.Models.SceneEulerRotationTrack" />
///     of the clip, in track order; the track's property is always the rotation.
/// </summary>
/// <param name="Source">The controlled-block ordinal (a sequence clip) or the NiTransformController block (the <c>(controllers)</c> clip).</param>
/// <param name="Interpolator">The interpolator block the track was mapped from.</param>
/// <param name="Node">The exact document node the track drives.</param>
/// <param name="XKeyType">The X axis's stored key type (1 LINEAR, 2 QUADRATIC, 3 TBC, 5 CONST), or 0 for an empty axis.</param>
/// <param name="YKeyType">The Y axis's stored key type, or 0 for an empty axis.</param>
/// <param name="ZKeyType">The Z axis's stored key type, or 0 for an empty axis.</param>
internal sealed record NifModelEulerTrackSource(
    int Source,
    int Interpolator,
    int Node,
    uint XKeyType,
    uint YKeyType,
    uint ZKeyType);
