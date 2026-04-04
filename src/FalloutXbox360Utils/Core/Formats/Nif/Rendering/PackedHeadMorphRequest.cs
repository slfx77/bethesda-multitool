namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering;

/// <summary>
///     Static FaceGen morph inputs used by the packed base-head extraction path.
/// </summary>
internal sealed class PackedHeadMorphRequest
{
    public required EgmParser Egm { get; init; }
    public float[]? SymmetricCoeffs { get; init; }
    public float[]? AsymmetricCoeffs { get; init; }
}
