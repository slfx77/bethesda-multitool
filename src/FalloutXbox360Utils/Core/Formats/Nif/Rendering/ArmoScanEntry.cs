namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering;

/// <summary>
///     Scanned ARMO record data (armor biped model paths for rendering).
/// </summary>
internal sealed class ArmoScanEntry
{
    public string? EditorId { get; init; }
    public uint BipedFlags { get; init; }
    public byte GeneralFlags { get; init; }
    public string? MaleBipedModelPath { get; init; }
    public string? FemaleBipedModelPath { get; init; }
    public uint? BipedModelListFormId { get; init; }

    public bool IsPowerArmor => (GeneralFlags & 0x20) != 0;
}