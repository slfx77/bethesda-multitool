namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering;

/// <summary>
///     A single EGT texture morph basis: scale factor + per-texel int8 RGB deltas.
/// </summary>
internal sealed class EgtMorph
{
    /// <summary>Scale factor applied to all deltas before adding to texture pixels.</summary>
    public required float Scale { get; init; }

    /// <summary>Red channel deltas, length = rows * cols.</summary>
    public required sbyte[] DeltaR { get; init; }

    /// <summary>Green channel deltas, length = rows * cols.</summary>
    public required sbyte[] DeltaG { get; init; }

    /// <summary>Blue channel deltas, length = rows * cols.</summary>
    public required sbyte[] DeltaB { get; init; }
}
