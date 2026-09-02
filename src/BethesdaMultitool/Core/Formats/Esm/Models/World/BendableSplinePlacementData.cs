using System.Numerics;

namespace BethesdaMultitool.Core.Formats.Esm.Models.World;

/// <summary>
///     Fallout 4-family REFR <c>XBSD</c> spline parameters. These are the authored instance values
///     only; this model deliberately does not infer endpoints, a local axis, or the engine's sag
///     equation from the half extents.
/// </summary>
public sealed record BendableSplinePlacementData
{
    public float Slack { get; init; }

    public float Thickness { get; init; }

    public Vector3 HalfExtents { get; init; }

    /// <summary>
    ///     Byte at offset 20. FO4 may omit it, while FO76 requires it plus three padding bytes.
    ///     It stays nullable because the shared decoder does not receive game/form-version context,
    ///     and stays raw because preserving the serialized value is safer than normalizing every
    ///     nonzero byte to 1.
    /// </summary>
    public byte? WindDetachedEndRaw { get; init; }

    public bool? WindDetachedEnd => WindDetachedEndRaw.HasValue
        ? WindDetachedEndRaw.Value != 0
        : null;

    /// <summary>
    ///     Bytes after the wind byte, retained losslessly. These include FO76 padding and may include
    ///     two additional legacy floats on records whose form version predates 131; interpreting them
    ///     requires game/form-version context that this common transport does not have.
    /// </summary>
    public IReadOnlyList<byte> TrailingData { get; init; } = [];
}
