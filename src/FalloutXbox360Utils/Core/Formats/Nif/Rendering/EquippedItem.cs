namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering;

/// <summary>
///     A single piece of equipment resolved from NPC_ CNTO → ARMO.
/// </summary>
internal sealed class EquippedItem
{
    public uint BipedFlags { get; init; }
    public bool IsPowerArmor { get; init; }
    public EquipmentAttachmentMode AttachmentMode { get; init; }
    public string MeshPath { get; init; } = "";
}