namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering;

/// <summary>
///     Scanned PACK record data used for high-level weapon visibility/selection.
/// </summary>
internal sealed class PackageScanEntry
{
    public string? EditorId { get; init; }
    public byte Type { get; init; }
    public uint GeneralFlags { get; init; }
    public uint? UseWeaponFormId { get; init; }
}