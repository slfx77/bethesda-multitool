using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     One NIF key group mapped onto a Shared transform curve by <see cref="NifModelCurveMapping" />: the ordinary key
///     arrays of a <see cref="SceneTransformTrack" /> plus its interpolation and, for TBC, its named parameters and RE-19
///     endpoints; for a quaternion TBC or QUADRATIC rotation (slice 16b), the Squad triples and the platform's policy.
///     The arrays are owned by this record and never changed after mapping.
/// </summary>
/// <param name="KeyType">The stored key type the curve came from (1 LINEAR, 2 QUADRATIC, 3 TBC, 5 CONST).</param>
/// <param name="ComponentCount">
///     The Shared width of one value: 3 for a translation or a replicated scale, 4 for a rotation (X, Y, Z, W), 1 for an
///     unreplicated float group.
/// </param>
/// <param name="Times">One Float32 time per key, exactly as stored, strictly increasing.</param>
/// <param name="Values">
///     Key-major values, <see cref="ComponentCount" /> per key; for <see cref="SceneInterpolation.Hermite" /> each key is
///     the triple [incoming, value, outgoing], that is [Forward, Value, Backward] as stored (RE-18); for
///     <see cref="SceneInterpolation.GamebryoSquad" /> each key is the triple [incoming inner point, key, outgoing inner
///     point], each in Shared's X, Y, Z, W (RE-24 step 2).
/// </param>
/// <param name="Interpolation">The Shared interpolation the key type maps to.</param>
/// <param name="TbcParameters">
///     For <see cref="SceneInterpolation.Tbc" />, one parameter set per key named from the three stored floats in file
///     order (Tension, Continuity, Bias); null otherwise (a Squad rotation's stored T, C and B enter only its inner
///     points, and Shared refuses parameters on a Squad track; the raw floats stay in the block's native row).
/// </param>
/// <param name="TbcEndpoints">
///     For a multi-key <see cref="SceneInterpolation.Tbc" /> curve, the RE-19 boundary tangents; null otherwise (a single
///     TBC key has none, as the engine writes none).
/// </param>
/// <param name="SquadPolicy">
///     For <see cref="SceneInterpolation.GamebryoSquad" />, the nested-normalization policy the file's platform selected
///     (<see cref="NifModelSquadPolicy" />); null for every other interpolation.
/// </param>
internal sealed record NifModelCurve(
    uint KeyType,
    int ComponentCount,
    float[] Times,
    float[] Values,
    SceneInterpolation Interpolation,
    SceneTbcParameters[]? TbcParameters,
    SceneTbcEndpointTangents? TbcEndpoints,
    SceneGamebryoSquadPolicy? SquadPolicy = null);
