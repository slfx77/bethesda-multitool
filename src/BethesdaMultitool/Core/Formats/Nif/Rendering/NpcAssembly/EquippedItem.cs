namespace BethesdaMultitool.Core.Formats.Nif.Rendering.NpcAssembly;

/// <summary>
///     A single piece of equipment resolved from NPC_ CNTO → ARMO.
/// </summary>
internal sealed class EquippedItem
{
    /// <summary>Biped slot flag for the Pip-Boy (bit 6).</summary>
    internal const uint PipBoyBipedFlag = 0x40;

    public uint BipedFlags { get; init; }
    public bool IsPowerArmor { get; init; }
    public EquipmentAttachmentMode AttachmentMode { get; init; }
    public string MeshPath { get; init; } = "";
    internal List<EquipmentAssetOwner> AssetOwners { get; } = [];

    /// <summary>
    ///     True when the equipment set includes a Pip-Boy. Drives the engine's
    ///     PipBoyOn/PipBoyOff sleeve-variant toggle on armor meshes
    ///     (see <see cref="NifBlockParsers.IsSuppressedPipBoyVariantShape" />).
    /// </summary>
    internal static bool AnyPipBoy(IEnumerable<EquippedItem>? items)
    {
        return items != null &&
               items.Any(item => (item.BipedFlags & PipBoyBipedFlag) != 0);
    }
}

internal sealed record EquipmentAssetOwner(uint ArmorFormId, uint? AddonFormId, string Field,
    uint? InventoryFormId = null, uint? FormListId = null);
