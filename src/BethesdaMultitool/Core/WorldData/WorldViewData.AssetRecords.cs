using BethesdaMultitool.Core.Assets;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Semantic.LoadOrder;

namespace BethesdaMultitool.Core.WorldData;

internal sealed partial class WorldViewData
{
    internal WorldAssetRecordCatalog AssetRecords { get; set; } = WorldAssetRecordCatalog.Unavailable("Unavailable");
}

/// <summary>Record owners from the selected parse, independent of current archive bytes or asset priority.</summary>
internal sealed class WorldAssetRecordCatalog
{
    private readonly LoadOrderSelectionView? _selection;
    private readonly IReadOnlyDictionary<uint, string> _models;

    private WorldAssetRecordCatalog(string mode, LoadOrderSelectionView? selection)
    {
        Mode = mode;
        _selection = selection;
        _models = selection is null ? new Dictionary<uint, string>() : ObjectBoundsIndex.BuildModelPathIndex(selection.Records);
    }

    internal string Mode { get; }
    internal static WorldAssetRecordCatalog Selected(LoadOrderSelectionView selection) => new("SelectedPlugins", selection);
    internal static WorldAssetRecordCatalog Unavailable(string mode) => new(mode, null);

    internal AssetRecordOwner Owner(uint id, string signature = "")
    {
        if (_selection is null || !_selection.Index.Records.TryGetValue(id, out var identity))
            return AssetRecordOwner.Unavailable(signature, id);
        if (identity.HasAmbiguousWinningRecords || identity.TypeConflict)
            return AssetRecordOwner.Unavailable(signature, id) with
            { Status = identity.HasAmbiguousWinningRecords ? "Ambiguous" : "TypeConflict" };
        // These descriptors refer to the earlier selected parse. A fresh file hash would describe
        // a different read, so SourceSha256 deliberately remains unavailable.
        var owner = AssetRecordOwner.Selected(identity.Winner, null);
        if (identity.DeletedByWinner) return owner with { Status = "Deleted" };
        if (signature.Length != 0 && owner.Signature != signature) return owner with { Status = "TypeMismatch" };
        return _selection.IncludedFormIds.Contains(id) ? owner : owner with { Status = "Unparsed" };
    }

    internal AssetRecordOwner PlacementOwner(PlacedReference placement)
    {
        var owner = Owner(placement.FormId, placement.RecordType);
        return IsSelected(owner) && owner.RecordOffset != placement.Offset
            ? owner with { Status = "PhysicalMismatch" } : owner;
    }

    internal AssetRecordOwner ModelOwner(PlacedReference placement, string? requestedPath)
    {
        var owner = Owner(placement.BaseFormId);
        if (!IsSelected(owner)) return owner;
        return requestedPath is not null && _models.TryGetValue(placement.BaseFormId, out var authored) &&
               string.Equals(MeshPath(authored), MeshPath(requestedPath), StringComparison.OrdinalIgnoreCase)
            ? owner : owner with { Status = "FieldUnavailable" };
    }

    internal AssetUseGraph PlacementUses(PlacedReference placement, string? requestedPath)
    {
        var graph = new AssetUseGraphBuilder();
        var placed = graph.Add(PlacementOwner(placement), "placement", placement.RecordType, null, "placement-record");
        var link = graph.Add(PlacementOwner(placement), "base-object", "NAME", null, "record-reference", [placed]);
        var owner = Owner(placement.BaseFormId);
        var baseNode = graph.Add(owner, "base-object", owner.Signature, null, "selected-record", [link]);
        AddCandidates(graph, placement.FormId, placed);
        AddCandidates(graph, placement.BaseFormId, baseNode);
        if (!string.IsNullOrWhiteSpace(requestedPath) && placement.RecordType == "REFR")
        {
            var modelOwner = ModelOwner(placement, requestedPath);
            graph.Add(modelOwner, "model", ModelField(modelOwner), MeshPath(requestedPath),
                IsSelected(modelOwner) ? "record-field" : "placement-model-owner-unavailable", [baseNode]);
        }
        return graph.Build();
    }

    /// <summary>Declared model fields sharing requests with recent reads; no placement or draw is inferred.</summary>
    internal AssetUseGraph DeclaredModelUses(IEnumerable<AssetSelectionReceipt> receipts)
    {
        var paths = receipts.Select(r => AssetUseGraph.Normalize(r.RequestedPath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var graph = new AssetUseGraphBuilder();
        foreach (var (id, path) in _models.OrderBy(p => p.Key))
        {
            var requested = MeshPath(path);
            if (!paths.Contains(requested)) continue;
            var owner = Owner(id);
            if (!IsSelected(owner)) continue;
            var record = graph.Add(owner, "base-object", owner.Signature, null, "selected-record");
            graph.Add(owner, "model", ModelField(owner), requested, "record-field", [record]);
        }
        return graph.Build();
    }

    private void AddCandidates(AssetUseGraphBuilder graph, uint id, string parent)
    {
        if (_selection is null || !_selection.Index.Records.TryGetValue(id, out var identity) ||
            (!identity.HasAmbiguousWinningRecords && !identity.TypeConflict)) return;
        foreach (var version in identity.Versions)
            graph.Add(AssetRecordOwner.Selected(version, null) with { Status = "Candidate" }, "record-candidate",
                version.Signature, null, "unselected-physical-candidate", [parent]);
    }

    private static bool IsSelected(AssetRecordOwner owner) => owner.Status is "Selected" or "MappedWithClamp";
    private static string ModelField(AssetRecordOwner owner) => owner.Signature == "ARMO" ? "WorldModelPath / ModelPath" : "MODL";
    private static string MeshPath(string path) => ReferenceModelPath.Normalize(path);
}
