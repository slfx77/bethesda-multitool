using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;

namespace BethesdaMultitool.Core.WorldData;

/// <summary>The authored subrecord that supplied the Starfield water-material string.</summary>
internal enum StarfieldWaterMaterialSelectionSource : uint
{
    Unavailable = 0,
    CellXcwm = 1,
    WorldspaceNam7 = 2
}

/// <summary>
///     Pure resolution result for Starfield's authored water-material strings. The string is retained
///     verbatim (including an authored empty string); consumers decide whether and how to use it.
/// </summary>
internal readonly record struct ResolvedStarfieldWaterMaterialSelection(
    string? Material,
    StarfieldWaterMaterialSelectionSource Source,
    uint? CellFormId,
    uint? WorldspaceFormId)
{
    internal string SourceTelemetry => Source switch
    {
        StarfieldWaterMaterialSelectionSource.CellXcwm => "cell-xcwm",
        StarfieldWaterMaterialSelectionSource.WorldspaceNam7 => "worldspace-nam7",
        _ => "unavailable"
    };
}

/// <summary>
///     Resolves Starfield's authored CELL XCWM override against its WRLD NAM7 fallback. This class
///     intentionally performs no WATR lookup, fallback-form selection, normalization, or shader mapping.
/// </summary>
internal static class StarfieldWaterMaterialSelectionResolver
{
    internal static ResolvedStarfieldWaterMaterialSelection Resolve(
        CellRecord? cell,
        WorldspaceRecord? worldspace)
    {
        // A non-null empty XCWM is still an authored override, so it must suppress NAM7 just as a
        // concrete authored type does. This preserves empty-versus-absent semantics for later consumers.
        if (cell?.StarfieldWaterType is { } cellWaterType)
        {
            return new ResolvedStarfieldWaterMaterialSelection(
                cellWaterType,
                StarfieldWaterMaterialSelectionSource.CellXcwm,
                cell.FormId,
                worldspace?.FormId);
        }

        if (worldspace?.StarfieldWaterMaterial is { } worldspaceWaterMaterial)
        {
            return new ResolvedStarfieldWaterMaterialSelection(
                worldspaceWaterMaterial,
                StarfieldWaterMaterialSelectionSource.WorldspaceNam7,
                cell?.FormId,
                worldspace.FormId);
        }

        return new ResolvedStarfieldWaterMaterialSelection(
            null,
            StarfieldWaterMaterialSelectionSource.Unavailable,
            cell?.FormId,
            worldspace?.FormId);
    }
}
