using FalloutXbox360Utils.Core.Formats.Esm.Enums;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering;

/// <summary>
///     Scanned NPC_ record data.
/// </summary>
internal sealed class NpcScanEntry
{
    public string? EditorId { get; init; }
    public string? FullName { get; init; }
    public uint? RaceFormId { get; init; }
    public bool IsFemale { get; init; }
    public uint? HairFormId { get; init; }
    public uint? EyesFormId { get; init; }
    public List<uint>? HeadPartFormIds { get; init; }
    public uint? HairColor { get; init; }
    public float[]? FaceGenSymmetric { get; init; }
    public float[]? FaceGenAsymmetric { get; init; }
    public float[]? FaceGenTexture { get; init; }
    public List<uint>? InventoryFormIds { get; init; }
    public uint? TemplateFormId { get; init; }
    public ushort TemplateFlags { get; init; }
}

/// <summary>
///     Scanned RACE record data.
/// </summary>
internal sealed class RaceScanEntry
{
    public string? EditorId { get; init; }
    public uint? DefaultEyesFormId { get; init; }
    public string? MaleHeadModelPath { get; init; }
    public string? FemaleHeadModelPath { get; init; }
    public string? MaleHeadTexturePath { get; init; }
    public string? FemaleHeadTexturePath { get; init; }
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

/// <summary>
///     Scanned HAIR record data.
/// </summary>
internal sealed class HairScanEntry
{
    public string? EditorId { get; init; }
    public string? ModelPath { get; init; }
    public string? TexturePath { get; init; }
}

/// <summary>
///     Scanned EYES record data (eye texture).
/// </summary>
internal sealed class EyesScanEntry
{
    public string? EditorId { get; init; }
    public string? TexturePath { get; init; }
}

/// <summary>
///     Scanned HDPT record data (head part mesh).
/// </summary>
internal sealed class HdptScanEntry
{
    public string? EditorId { get; init; }
    public string? ModelPath { get; init; }
}

/// <summary>
///     Scanned ARMO record data (armor biped model paths for rendering).
/// </summary>
internal sealed class ArmoScanEntry
{
    public string? EditorId { get; init; }
    public uint BipedFlags { get; init; }
    public string? MaleBipedModelPath { get; init; }
    public string? FemaleBipedModelPath { get; init; }
}

/// <summary>
///     Scanned WEAP record data (weapon model path for NPC rendering).
/// </summary>
internal sealed class WeapScanEntry
{
    public string? EditorId { get; init; }
    public string? ModelPath { get; init; }
    public WeaponType WeaponType { get; init; }
    public short Damage { get; init; }
}
