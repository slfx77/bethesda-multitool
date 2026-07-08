namespace BethesdaMultitool.Core.Formats.SpeedTree;

/// <summary>
///     The TREE record's CNAM dimming pair — the engine's per-tree canopy-depth darkening inputs
///     (<c>TESObjectTREE::Get{Leaf,Branch}Dimming</c> → <c>CSpeedTreeRT::Set{Leaf,Branch}DimmingScalar</c>
///     → <c>SIdvLeafInfo</c> +4/+8, consumed by <c>CIdvBranch::MakeLeaf</c> / <c>BuildCrossSection</c>).
/// </summary>
public readonly record struct SpeedTreeDimming(float Leaf, float Branch);
