using BethesdaMultitool.Core.Formats.Esm.Plugin.Cell;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Nav;

namespace BethesdaMultitool.Core.Formats.Esm.PlannedWriter.Cells;

/// <summary>
///     Reads each emitted NAVM record's cross-navmesh connectivity (NVEX edge targets + NVDP door
///     refs) out of the finished cell bundles, for NVCI reconstruction in the NAVI override.
/// </summary>
internal static class NavmConnectivityExtractor
{
    /// <summary>
    ///     Builds a per-NAVM connectivity map from the temporary child records of
    ///     <paramref name="bundles" />. NVEX targets are already validated by the NVEX sanitize pass;
    ///     NVDP door refs are filtered here against <paramref name="validRefFormIds" /> (emitted ∪
    ///     master) so a dangling door ref never lands in NVCI DoorLinks.
    /// </summary>
    public static Dictionary<uint, NavmConnectivity> Extract(
        List<CellOverrideBundle> bundles,
        IReadOnlySet<uint> validRefFormIds)
    {
        var result = new Dictionary<uint, NavmConnectivity>();
        foreach (var bundle in bundles)
        {
            foreach (var rec in bundle.TemporaryChildRecords)
            {
                if (rec.Length < 4 || rec[0] != (byte)'N' || rec[1] != (byte)'A'
                    || rec[2] != (byte)'V' || rec[3] != (byte)'M')
                {
                    continue;
                }

                if (!NavMeshConnectivity.TryExtract(rec, out var formId, out var connectivity))
                {
                    continue;
                }

                var validDoors = connectivity.DoorRefs.Where(validRefFormIds.Contains).ToList();
                result[formId] = validDoors.Count == connectivity.DoorRefs.Count
                    ? connectivity
                    : connectivity with { DoorRefs = validDoors };
            }
        }

        return result;
    }
}
