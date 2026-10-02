namespace BethesdaMultitool.Core.Actors;

/// <summary>An inventory declaration or eligible branch, retaining ownership and leveled-list metadata.</summary>
internal sealed record ActorInventoryEntry(uint ItemFormId, string Name, long Count, uint SourceActor,
    int Depth, string Status, ushort? RequiredLevel = null, byte? ChanceNone = null, byte? Flags = null,
    uint? GlobalFormId = null, uint? OwnerFormId = null, float? ItemCondition = null)
{
    /// <summary>Validated snapshot chance used by seeded generation, including an authored global override.</summary>
    internal float? ResolvedChanceNone { get; init; }
}
