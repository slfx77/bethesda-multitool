namespace BethesdaMultitool.Core.Formats.Esm.Export;

internal sealed record CrossDumpSourceSummary(
    string FilePath,
    int WeaponCount,
    int NpcCount,
    int CellCount,
    string? SkillEraSummary);
