using BethesdaMultitool.Core.Formats.Esm.Models;

namespace BethesdaMultitool.Core.Actors;

/// <summary>Retains the exact concrete items produced by one bounded, repeatable inventory preview.</summary>
/// <param name="Seed">Unsigned seed used with the versioned algorithm.</param>
/// <param name="Level">Explicit preview level supplied to every nested list.</param>
/// <param name="Items">Concrete items including counts and inherited ownership; no unresolved lists.</param>
/// <param name="Complete">Whether all declarations were processed without unresolved or truncated branches.</param>
internal sealed record ActorInventoryGeneration(uint Seed, ushort Level,
    IReadOnlyList<InventoryItem> Items, bool Complete)
{
    /// <summary>Distinct incomplete-branch reasons in encounter order, including limit exhaustion.</summary>
    internal IReadOnlyList<string> Notices { get; init; } = Array.Empty<string>();

    /// <summary>Identifies draw encoding, traversal order, and the documented generation policy.</summary>
    internal const string Algorithm = "inventory-preview-sha256-v1";

    /// <summary>Machine-invariant assumptions; GUI consumers localize these identifiers.</summary>
    internal static IReadOnlyList<string> Assumptions { get; } = Array.AsReadOnly(new[]
    {
        "explicit-level-without-actor-scaling", "highest-tier-or-all-levels", "use-all-ignores-level",
        "authored-entry-order", "snapshot-global-chance", "parent-ownership",
        "no-scripts-or-runtime-rng", "no-game-setting-level-cutoff"
    });
}
