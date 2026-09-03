using BethesdaMultitool.Core.Formats.Esm.Models;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.NpcAssembly;

/// <summary>
///     Appearance-index representation of an LVLI/LVLN record. Unlike the legacy
///     flattened FormID table, this retains the authored eligibility level, count,
///     chance-none byte, and flags needed to evaluate a retail leveled list.
/// </summary>
internal sealed class LeveledListScanEntry
{
    public string? EditorId { get; init; }

    public byte ChanceNone { get; init; }

    public byte Flags { get; init; }

    public List<LeveledEntry> Entries { get; init; } = [];

    public bool CalculateFromAllLevelsAtOrBelowPlayer => (Flags & 0x01) != 0;

    public bool CalculateForEachItemInCount => (Flags & 0x02) != 0;
}
