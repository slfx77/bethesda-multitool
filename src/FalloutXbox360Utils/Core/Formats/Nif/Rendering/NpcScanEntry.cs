using FalloutXbox360Utils.Core.Formats.Esm.Models;

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
    public byte[]? SpecialStats { get; init; }
    public byte[]? Skills { get; init; }
    public List<InventoryItem>? InventoryItems { get; init; }
    public List<uint>? PackageFormIds { get; init; }
    public uint? TemplateFormId { get; init; }
    public ushort TemplateFlags { get; init; }
}
