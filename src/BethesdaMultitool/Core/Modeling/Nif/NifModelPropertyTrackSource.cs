using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     Where one property track of a clip came from, for the clip extras' <c>propertyTracks</c> map
///     (<see cref="NifModelAnimationExtras" />, SA10: Shared cannot target a single track, so track identity lives in
///     the payload). One entry per <see cref="ScenePropertyTrack" /> of the clip, in track order.
/// </summary>
/// <param name="Source">The controlled-block ordinal (a sequence clip) or the controller block (the <c>(controllers)</c> clip).</param>
/// <param name="Interpolator">The interpolator block the track was mapped from; -1 when the curve came straight from an NiUVData.</param>
/// <param name="Kind">The Shared kind.</param>
/// <param name="Index">The document material index, or the node index for <see cref="ScenePropertyKind.NodeVisibility" />.</param>
/// <param name="LayerIndex">The layer ordinal for a layer kind; -1 otherwise.</param>
/// <param name="Node">The document node the track drives for visibility; -1 for a material kind.</param>
/// <param name="PropertyBlock">The NiMaterialProperty or NiTexturingProperty block the track drives; -1 for visibility.</param>
/// <param name="Controller">The controller block the binding resolved to; -1 when a sequence block resolved none.</param>
/// <param name="KeyType">The stored key type the curve came from (1 LINEAR, 2 QUADRATIC, 3 TBC, 5 CONST), or 0 for a constant or a B-spline.</param>
internal sealed record NifModelPropertyTrackSource(
    int Source,
    int Interpolator,
    ScenePropertyKind Kind,
    int Index,
    int LayerIndex,
    int Node,
    int PropertyBlock,
    int Controller,
    uint KeyType);
