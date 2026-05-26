namespace FalloutXbox360Utils.Core.Formats.Esm.Export.Projections;

/// <summary>
///     Minimal NPC projection: only the FormID is needed by cross-source builders
///     (npc placement index, npc script reference index — both just check membership in
///     a HashSet keyed by FormID). Pass-B <c>BuildReport</c> for NPCs still consults the
///     original <see cref="Models.Records.Character.NpcRecord" /> stashed on the projection.
/// </summary>
internal readonly record struct NpcSkeleton(uint FormId);
