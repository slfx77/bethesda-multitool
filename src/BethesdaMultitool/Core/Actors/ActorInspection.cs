namespace BethesdaMultitool.Core.Actors;

/// <summary>Actor statistics and inventory declarations, with an optional explicitly seeded preview.</summary>
internal sealed record ActorInspection(uint FormId, string Name, string Kind,
    IReadOnlyList<ActorStatistic> Statistics, IReadOnlyList<ActorInventoryEntry> Inventory,
    string? InventoryNotice)
{
    /// <summary>The detected source game; unknown captures are not silently assigned a calculation profile.</summary>
    internal BethesdaMultitool.Core.Games.BethesdaGame Game { get; init; }
    /// <summary>The exact optional generated preview; null retains authored/candidate inspection.</summary>
    internal ActorInventoryGeneration? Generation { get; init; }

    /// <summary>Observed actors from the distinct per-group chains.</summary>
    internal IReadOnlyList<ActorTemplateHop> TemplateChain { get; init; } = [];

    /// <summary>Static template resolution, retaining failures and leveled candidates.</summary>
    internal IReadOnlyList<ActorTemplateGroupResolution> TemplateGroups { get; init; } = [];

    /// <summary>Static values after inheritance; these are not runtime combat values.</summary>
    internal IReadOnlyList<ActorEffectiveStatistic> EffectiveStatistics { get; init; } = [];

    /// <summary>Runtime calculations that cannot be established by this static inspection.</summary>
    internal IReadOnlyList<ActorCalculatedStatistic> Calculated { get; init; } = [];

    /// <summary>Input master availability, when the caller supplied file identity.</summary>
    internal IReadOnlyList<ActorMasterStatus> Masters { get; init; } = [];

    /// <summary>Whether the source is a partial memory capture.</summary>
    internal bool IsPartialCapture { get; init; }

    /// <summary>The game-specific interpretation used for the additional template fields.</summary>
    internal string TemplateSemantics { get; init; } = "FO3/FNV";
}
