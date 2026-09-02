using System.Numerics;

namespace BethesdaMultitool.Core.Formats.Nif.Materials;

/// <summary>
///     A CE2 material UV-offset controller that is exactly reducible to the native renderer's
///     constant looping scroll representation.
/// </summary>
internal readonly record struct StarfieldMaterialUvAnimationPolicy(
    bool IsResolved,
    Vector2 InitialOffset,
    Vector2 Velocity,
    float PeriodSeconds);
