namespace BethesdaMultitool.Core.Formats.Esm.Export.Comparison;

internal sealed record CrossDumpSourceSummary(
    string FilePath,
    int WeaponCount,
    int NpcCount,
    int CellCount,
    string? SkillEraSummary);
