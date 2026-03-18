namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering;

/// <summary>
///     A single EGM morph basis: scale factor + per-vertex int16 XYZ deltas.
/// </summary>
internal sealed class EgmMorph
{
    /// <summary>Scale factor applied to all deltas before adding to vertex positions.</summary>
    public required float Scale { get; init; }

    /// <summary>Interleaved int16 deltas: [X0, Y0, Z0, X1, Y1, Z1, ...]. Length = vertexCount * 3.</summary>
    public required short[] Deltas { get; init; }
}
