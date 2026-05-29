using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.Planner.Cells;

/// <summary>
///     Subsumes legacy <c>PreEncodeNewWorldspacesWithCells</c> at the catalog level:
///     identifies WRLD FormIDs that have at least one captured cell and classifies each
///     as <c>MasterOnly</c> (no DMP capture for the cells), <c>DmpOverride</c> (master
///     WRLD with new captured cells), or <c>DmpNew</c> (worldspace not in master, must
///     emit fresh anchor).
/// </summary>
public sealed class WorldspaceCatalog
{
    public sealed record WorldspaceCatalogEntry
    {
        public required uint WorldspaceFormId { get; init; }
        public required WorldspaceCatalogSource Source { get; init; }
        public WorldspaceRecord? DmpModel { get; init; }
        public required IReadOnlyList<uint> CellFormIds { get; init; }
    }

    public enum WorldspaceCatalogSource
    {
        MasterOnly,
        DmpOverride,
        DmpNew,
    }

    /// <summary>
    ///     Build worldspace entries from the cell catalog + DMP worldspace list.
    /// </summary>
    public static IReadOnlyList<WorldspaceCatalogEntry> Build(
        IReadOnlyList<CellCatalogEntry> cellEntries,
        IReadOnlyList<WorldspaceRecord> dmpWorldspaces,
        IReadOnlySet<uint> masterFormIds)
    {
        ArgumentNullException.ThrowIfNull(cellEntries);
        ArgumentNullException.ThrowIfNull(dmpWorldspaces);
        ArgumentNullException.ThrowIfNull(masterFormIds);

        var cellsByWorldspace = new Dictionary<uint, List<uint>>();
        foreach (var entry in cellEntries)
        {
            var wrldId = ExtractWorldspaceFormId(entry);
            if (wrldId is null)
            {
                continue;
            }

            if (!cellsByWorldspace.TryGetValue(wrldId.Value, out var list))
            {
                list = [];
                cellsByWorldspace[wrldId.Value] = list;
            }

            list.Add(entry.CellFormId);
        }

        var dmpByFormId = new Dictionary<uint, WorldspaceRecord>(dmpWorldspaces.Count);
        foreach (var dmp in dmpWorldspaces)
        {
            dmpByFormId[dmp.FormId] = dmp;
        }

        var result = new List<WorldspaceCatalogEntry>(cellsByWorldspace.Count);
        foreach (var (wrldId, cellIds) in cellsByWorldspace)
        {
            var inMaster = masterFormIds.Contains(wrldId);
            dmpByFormId.TryGetValue(wrldId, out var dmpRecord);

            var source = (inMaster, dmpRecord is not null) switch
            {
                (true, true) => WorldspaceCatalogSource.DmpOverride,
                (true, false) => WorldspaceCatalogSource.MasterOnly,
                (false, _) => WorldspaceCatalogSource.DmpNew,
            };

            result.Add(new WorldspaceCatalogEntry
            {
                WorldspaceFormId = wrldId,
                Source = source,
                DmpModel = dmpRecord,
                CellFormIds = cellIds,
            });
        }

        return result;
    }

    private static uint? ExtractWorldspaceFormId(CellCatalogEntry entry)
    {
        if (entry.MasterContext is { WorldspaceFormId: { } masterWrld })
        {
            return masterWrld;
        }

        return entry.DmpModel?.WorldspaceFormId;
    }
}
