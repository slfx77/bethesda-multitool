namespace BethesdaMultitool.Core.Formats.Esm;

internal sealed class CellWorldspaceAuthorityBuilder
{
    public Dictionary<uint, CellAuthorityMetadata> Cells { get; } = [];
    public Dictionary<uint, uint> ReferenceParents { get; } = [];
    public List<CellReferenceParentWindow> ReferenceParentWindows { get; } = [];
    public Dictionary<uint, HashSet<uint>> Conflicts { get; } = [];
    public Dictionary<uint, HashSet<uint>> ReferenceConflicts { get; } = [];
    public Dictionary<uint, string> WorldspaceNames { get; } = [];
    public List<CellWorldspaceAuthoritySource> Sources { get; } = [];

    public Dictionary<uint, uint> CellToWorldspace => Cells
        .Where(kv => kv.Value.WorldspaceFormId is > 0)
        .ToDictionary(kv => kv.Key, kv => kv.Value.WorldspaceFormId!.Value);

    public bool TryAddOrFlag(uint cellFormId, uint worldspaceFormId, string sourceLabel)
    {
        if (cellFormId == 0 || worldspaceFormId == 0)
        {
            return false;
        }

        if (!Cells.TryGetValue(cellFormId, out var existing))
        {
            Cells[cellFormId] = new CellAuthorityMetadata { WorldspaceFormId = worldspaceFormId };
            return true;
        }

        if (existing.WorldspaceFormId is null or 0u)
        {
            Cells[cellFormId] = existing with { WorldspaceFormId = worldspaceFormId };
            return false;
        }

        if (existing.WorldspaceFormId.Value != worldspaceFormId)
        {
            if (!Conflicts.TryGetValue(cellFormId, out var set))
            {
                set = [existing.WorldspaceFormId.Value];
                Conflicts[cellFormId] = set;
            }

            set.Add(worldspaceFormId);
        }

        return false;
    }

    public bool TryAddReferenceParent(uint referenceFormId, uint cellFormId, string sourceLabel)
    {
        if (referenceFormId == 0 || cellFormId == 0)
        {
            return false;
        }

        if (!ReferenceParents.TryGetValue(referenceFormId, out var existing))
        {
            ReferenceParents[referenceFormId] = cellFormId;
            return true;
        }

        if (existing != cellFormId)
        {
            if (!ReferenceConflicts.TryGetValue(referenceFormId, out var set))
            {
                set = [existing];
                ReferenceConflicts[referenceFormId] = set;
            }

            set.Add(cellFormId);
        }

        return false;
    }

    public bool AddOrUpdateCell(uint cellFormId, CellAuthorityMetadata metadata, string sourceLabel)
    {
        if (cellFormId == 0)
        {
            return false;
        }

        var added = !Cells.TryGetValue(cellFormId, out var existing);
        existing ??= new CellAuthorityMetadata();

        if (metadata.WorldspaceFormId is > 0)
        {
            TryAddOrFlag(cellFormId, metadata.WorldspaceFormId.Value, sourceLabel);
            existing = Cells[cellFormId];
        }

        Cells[cellFormId] = existing.MergeMissing(metadata);
        return added;
    }

    public void AddWorldspaceName(uint worldspaceFormId, string? editorId)
    {
        if (!string.IsNullOrEmpty(editorId))
        {
            WorldspaceNames.TryAdd(worldspaceFormId, editorId);
        }
    }

    public void AddSource(string type, string path, int addedCells, int observedCells)
    {
        Sources.Add(new CellWorldspaceAuthoritySource(type, path, addedCells, observedCells));
    }
}
