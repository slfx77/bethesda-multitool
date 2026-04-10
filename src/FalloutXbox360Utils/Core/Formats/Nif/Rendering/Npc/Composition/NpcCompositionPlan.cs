namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Composition;

internal sealed class NpcCompositionPlan
{
    public required NpcAppearance Appearance { get; init; }

    public required NpcCompositionOptions Options { get; init; }

    public NpcSkeletonComposition? Skeleton { get; init; }

    public required NpcHeadCompositionPlan Head { get; init; }

    public IReadOnlyList<NpcBodyMeshPlan> BodyParts { get; init; } = [];

    public IReadOnlyList<EquippedItem> BodyEquipment { get; init; } = [];

    public uint CoveredSlots { get; init; }

    public string? EffectiveBodyTexturePath { get; init; }

    public string? EffectiveHandTexturePath { get; init; }

    public NpcWeaponCompositionPlan? Weapon { get; init; }
}