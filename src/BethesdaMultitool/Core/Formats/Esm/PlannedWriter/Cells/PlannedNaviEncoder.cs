using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Planner.Cells;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Nav;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Pipeline;

namespace BethesdaMultitool.Core.Formats.Esm.PlannedWriter.Cells;

/// <summary>
///     Testable adapter over the shared NAVI override-synthesis primitive.
///     <see cref="NavInfoMapBuilder.BuildNaviOverride" /> takes the master NAVI record
///     plus a list of new NAVM entries and splices NVMI / NVCI subrecord runs into the
///     master's existing layout. This adapter just maps <see cref="PlannedNavmEntry" />
///     to the helper's <c>NewNavmEntry</c> shape and delegates.
/// </summary>
/// <remarks>
///     This adapter exposes the base no-connectivity call for isolated parity tests. Production
///     orchestration supplies the plan's connectivity map when it invokes the shared helper.
/// </remarks>
public static class PlannedNaviEncoder
{
    /// <summary>
    ///     Build the NAVI override record bytes. Returns null when there are no new
    ///     entries (no NAVI override needed) or when the master NAVI cannot be located.
    /// </summary>
    public static byte[]? BuildOverride(
        ParsedMainRecord? masterNavi,
        IReadOnlyList<PlannedNavmEntry> newEntries,
        PluginBuildOptions options)
    {
        ArgumentNullException.ThrowIfNull(newEntries);
        ArgumentNullException.ThrowIfNull(options);

        if (newEntries.Count == 0 || masterNavi is null)
        {
            return null;
        }

        var builderEntries = newEntries
            .Select(e => new NewNavmEntry(
                e.NavmFormId,
                e.LocationFormId,
                e.IsInterior,
                (short)e.GridX,
                (short)e.GridY,
                e.NvvxBytes.Length > 0 ? e.NvvxBytes : null))
            .ToList();

        return NavInfoMapBuilder.BuildNaviOverride(masterNavi, builderEntries, options);
    }
}

