namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering;

/// <summary>
///     Scanned RACE record data.
/// </summary>
internal sealed class RaceScanEntry
{
    public string? EditorId { get; init; }
    public uint? OlderRaceFormId { get; init; }
    public uint? YoungerRaceFormId { get; init; }
    public uint? DefaultEyesFormId { get; init; }
    public string? MaleHeadModelPath { get; init; }
    public string? FemaleHeadModelPath { get; init; }
    public string? MaleHeadTexturePath { get; init; }
    public string? FemaleHeadTexturePath { get; init; }
    public string? MaleMouthModelPath { get; init; }
    public string? FemaleMouthModelPath { get; init; }
    public string? MaleLowerTeethModelPath { get; init; }
    public string? FemaleLowerTeethModelPath { get; init; }
    public string? MaleUpperTeethModelPath { get; init; }
    public string? FemaleUpperTeethModelPath { get; init; }
    public string? MaleTongueModelPath { get; init; }
    public string? FemaleTongueModelPath { get; init; }
    public string? MaleEyeLeftModelPath { get; init; }
    public string? FemaleEyeLeftModelPath { get; init; }
    public string? MaleEyeRightModelPath { get; init; }
    public string? FemaleEyeRightModelPath { get; init; }
    public float[]? MaleFaceGenSymmetric { get; init; }
    public float[]? FemaleFaceGenSymmetric { get; init; }
    public float[]? MaleFaceGenAsymmetric { get; init; }
    public float[]? FemaleFaceGenAsymmetric { get; init; }
    public float[]? MaleFaceGenTexture { get; init; }

    public float[]? FemaleFaceGenTexture { get; init; }

    // Body mesh paths (from body parts section after NAM1)
    public string? MaleUpperBodyPath { get; init; }
    public string? FemaleUpperBodyPath { get; init; }
    public string? MaleLeftHandPath { get; init; }
    public string? FemaleLeftHandPath { get; init; }
    public string? MaleRightHandPath { get; init; }
    public string? FemaleRightHandPath { get; init; }
    public string? MaleBodyTexturePath { get; init; }
    public string? FemaleBodyTexturePath { get; init; }
}
