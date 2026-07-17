using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;

namespace BethesdaMultitool;

/// <summary>The authored record that supplied the current water appearance.</summary>
internal enum WaterAppearanceSelectionSource : uint
{
    Unavailable = 0,
    CellXcwt = 1,
    WorldspaceNam2 = 2,
}

/// <summary>
///     Pure result of resolving a CELL's XCWT override against its WRLD NAM2 fallback.
///     The selected record is retained so renderer and telemetry use the same lookup result.
/// </summary>
internal readonly record struct ResolvedWaterAppearanceSelection(
    WaterRecord? Water,
    WaterAppearanceSelectionSource Source,
    uint? CellFormId,
    uint? WorldspaceFormId)
{
    internal uint? WaterFormId => Water?.FormId;

    internal string SourceTelemetry => Source switch
    {
        WaterAppearanceSelectionSource.CellXcwt => "cell-xcwt",
        WaterAppearanceSelectionSource.WorldspaceNam2 => "worldspace-nam2",
        _ => "unavailable",
    };
}

/// <summary>
///     Resolves the usable WATR for the current cell. A non-zero, retained CELL XCWT wins; missing,
///     zero, or unresolved XCWT falls back to the retained WRLD NAM2 record.
/// </summary>
internal static class WaterAppearanceSelectionResolver
{
    internal static ResolvedWaterAppearanceSelection Resolve(
        CellRecord? cell,
        WorldspaceRecord? worldspace,
        IReadOnlyDictionary<uint, WaterRecord>? watersByFormId)
    {
        if (TryResolve(cell?.WaterFormId, watersByFormId, out var cellWater))
        {
            return new ResolvedWaterAppearanceSelection(
                cellWater,
                WaterAppearanceSelectionSource.CellXcwt,
                cell?.FormId,
                worldspace?.FormId);
        }

        if (TryResolve(worldspace?.WaterFormId, watersByFormId, out var worldspaceWater))
        {
            return new ResolvedWaterAppearanceSelection(
                worldspaceWater,
                WaterAppearanceSelectionSource.WorldspaceNam2,
                cell?.FormId,
                worldspace?.FormId);
        }

        return new ResolvedWaterAppearanceSelection(
            Water: null,
            Source: WaterAppearanceSelectionSource.Unavailable,
            CellFormId: cell?.FormId,
            WorldspaceFormId: worldspace?.FormId);
    }

    private static bool TryResolve(
        uint? formId,
        IReadOnlyDictionary<uint, WaterRecord>? watersByFormId,
        out WaterRecord? water)
    {
        water = null;
        return formId is > 0 && watersByFormId is not null &&
               watersByFormId.TryGetValue(formId.Value, out water);
    }
}
