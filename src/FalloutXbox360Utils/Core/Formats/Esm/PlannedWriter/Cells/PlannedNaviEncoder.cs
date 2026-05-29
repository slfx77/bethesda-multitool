using FalloutXbox360Utils.Core.Formats.Esm.Planner.Cells;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Nav;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Pipeline;

namespace FalloutXbox360Utils.Core.Formats.Esm.PlannedWriter.Cells;

/// <summary>
///     Planner-side adapter for NAVI override synthesis. The legacy
///     <see cref="NavInfoMapBuilder.BuildNaviOverride" /> takes the master NAVI record
///     plus a list of new NAVM entries and splices NVMI / NVCI subrecord runs into the
///     master's existing layout. This adapter just maps <see cref="PlannedNavmEntry" />
///     to the legacy <c>NewNavmEntry</c> shape and delegates.
/// </summary>
/// <remarks>
///     Runs once at top-level after every cell finishes emitting NAVMs. Without it, the
///     FNV runtime null-derefs at <c>FalloutNV+0x0069E09A</c> during NavMeshInfoMap
///     iteration when any new NAVMs were emitted.
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

        var legacyEntries = newEntries
            .Select(e => new NewNavmEntry(
                e.NavmFormId,
                e.LocationFormId,
                e.IsInterior,
                (short)e.GridX,
                (short)e.GridY,
                e.NvvxBytes.Length > 0 ? e.NvvxBytes : null))
            .ToList();

        return NavInfoMapBuilder.BuildNaviOverride(masterNavi, legacyEntries, options);
    }
}
