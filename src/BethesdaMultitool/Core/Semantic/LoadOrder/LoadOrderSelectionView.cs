using System.Collections;
using System.Reflection;
using BethesdaMultitool.Core.Analysis;
using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Inspection;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Records;

namespace BethesdaMultitool.Core.Semantic.LoadOrder;

/// <summary>
/// Shared explicit load-order selection view. Selects physical winners before merging, so an unparsed override
/// or a cleared field cannot inherit an older value and acquire the newer plugin's provenance.
/// Derived dialogue and terrain views are rebuilt from selected physical records, without earlier-field fallback.
/// </summary>
internal sealed class LoadOrderSelectionView
{
    private static readonly (PropertyInfo List, PropertyInfo Id)[] RecordLists = typeof(RecordCollection)
        .GetProperties()
        .Where(p => p.PropertyType.IsGenericType && p.PropertyType.GetGenericTypeDefinition() == typeof(List<>))
        .Select(p => (List: p, Id: p.PropertyType.GetGenericArguments()[0].GetProperty("FormId")))
        .Where(p => p.Id?.PropertyType == typeof(uint))
        .Select(p => (p.List, p.Id!)).ToArray();

    private readonly Dictionary<uint, Dictionary<uint, PlacedReference>> _placements = [];
    private readonly List<SelectedLandComponent> _terrain = [];
    private readonly Lazy<FormIdResolver> _resolver;

    private LoadOrderSelectionView(PluginLoadOrder order, LoadOrderRecordIndex index)
    {
        if (order.Entries.Count + order.MissingMasters.Count > 128)
        {
            throw new NotSupportedException("Typed load-order views support at most 128 loaded/reserved slots: script locals reserve the high bit.");
        }
        Order = order;
        Index = index;
        _resolver = new(() => Records.CreateResolver());
    }

    internal PluginLoadOrder Order { get; }
    internal LoadOrderRecordIndex Index { get; }
    internal RecordCollection Records { get; private set; } = new();
    internal FormIdResolver Resolver => _resolver.Value;
    internal HashSet<uint> IncludedFormIds { get; } = [];
    internal IReadOnlyList<SelectedLandComponent> TerrainComponents => _terrain;
    internal List<PlacementOccurrence> PhysicalPlacements { get; } = [];
    internal IEnumerable<(uint CellFormId, PlacedReference Reference)> PlacementComponents =>
        _placements.SelectMany(c => c.Value.Values.Select(r => (c.Key, r)));

    /// <summary>Uses headers retained from the same parse as the typed records; no second file scan.</summary>
    internal static LoadOrderSelectionView FromParsedSources(PluginLoadOrder order,
        IReadOnlyList<(PluginLoadOrderEntry Entry, RecordCollection Records, EsmRecordScanResult Scan)> sources)
    {
        var versions = sources.SelectMany(source =>
        {
            var editorIds = source.Scan.EditorIds.GroupBy(e => e.Offset).ToDictionary(g => g.Key, g => g.Last().Name);
            return source.Scan.MainRecords.Where(h => h.FormId != 0).Select(h =>
            {
                var id = order.Map(source.Entry.Path, h.FormId);
                return new LoadOrderRecordVersion(source.Entry.Name, source.Entry.Path, h.FormId, id.LoadOrderFormId,
                    h.RecordType, editorIds.GetValueOrDefault(h.Offset), h.Flags, h.Offset, id.WasClamped);
            });
        });
        var index = LoadOrderRecordIndex.Create(order, versions);
        var view = new LoadOrderSelectionView(order, index);
        foreach (var source in sources)
        {
            view.Append(source.Entry, LoadOrderChildEvidence.WithPhysicalPlacements(source.Records, source.Scan),
                LoadOrderChildEvidence.ReadPlacementParents(source.Records, source.Scan));
            view._terrain.AddRange(LoadOrderChildEvidence.ReadTerrain(source.Records, source.Scan, order, source.Entry, index));
        }
        view.Complete();
        return view;
    }

    internal static async Task<LoadOrderSelectionView> LoadAsync(PluginLoadOrder order,
        CancellationToken cancellationToken = default, bool retainPhysicalPlacements = false)
    {
        if (order.Entries.Count + order.MissingMasters.Count > 128)
        {
            throw new NotSupportedException("Typed load-order reports support at most 128 loaded/reserved slots: the script model reserves the high bit for SCRV local-variable indexes.");
        }
        var view = new LoadOrderSelectionView(order, LoadOrderRecordIndex.Build(order, cancellationToken));
        foreach (var entry in order.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var loaded = await SemanticFileLoader.LoadAsync(entry.Path,
                new SemanticFileLoadOptions { FileType = AnalysisFileType.EsmFile }, cancellationToken);
            if (retainPhysicalPlacements)
            {
                view.PhysicalPlacements.AddRange(LoadOrderPlacementQuery.WinningOccurrences(view, entry,
                    PlacementQuery.Read(loaded, cancellationToken: cancellationToken)));
            }
            var records = LoadOrderChildEvidence.WithPhysicalPlacements(loaded, cancellationToken);
            view._terrain.AddRange(LoadOrderChildEvidence.ReadTerrain(loaded, order, entry, view.Index));
            view.Append(entry, records, LoadOrderChildEvidence.ReadPlacementParents(loaded.Records, loaded.RawResult.EsmRecords));
        }
        view.Complete();
        return view;
    }

    /// <summary>Same selection boundary for already-parsed sources, still in each file's local namespace.</summary>
    internal static LoadOrderSelectionView FromSources(PluginLoadOrder order, LoadOrderRecordIndex index,
        IEnumerable<(PluginLoadOrderEntry Entry, RecordCollection Records)> sources,
        IEnumerable<SelectedLandComponent>? terrain = null)
    {
        var view = new LoadOrderSelectionView(order, index);
        foreach (var (entry, records) in sources) { view.Append(entry, records); }
        if (terrain != null) { view._terrain.AddRange(terrain.Where(t => view.IsWinner(t.FormId, t.Plugin))); }
        view.Complete();
        return view;
    }

    internal string Status(LoadOrderRecordIdentity identity)
    {
        if (identity.HasAmbiguousWinningRecords) { return "excluded-ambiguous-winner"; }
        if (identity.TypeConflict) { return "excluded-type-conflict"; }
        if (identity.DeletedByWinner) { return "excluded-deleted"; }
        return IncludedFormIds.Contains(identity.LoadOrderFormId) ? "included-in-typed-view" : "not-in-typed-view";
    }

    private bool IsWinner(uint id, string plugin)
    {
        return Index.Records.TryGetValue(id, out var identity) &&
               !identity.HasAmbiguousWinningRecords && !identity.TypeConflict && !identity.DeletedByWinner &&
               identity.Winner.Plugin.Equals(plugin, StringComparison.OrdinalIgnoreCase);
    }

    private void Append(PluginLoadOrderEntry entry, RecordCollection source,
        IEnumerable<(uint ParentCellFormId, PlacedReference Reference)>? physicalPlacements = null)
    {
        var rebased = RecordCollectionFormIdRebaser.Rebase(source, id => Order.Map(entry.Path, id).LoadOrderFormId);

        // A CELL override does not override its independently identified child records. Collect
        // those before dropping the old CELL envelope; a moved/deleted REFR must not survive there.
        var placements = physicalPlacements == null
            ? rebased.Cells.SelectMany(cell => cell.PlacedObjects.Select(reference => (cell.FormId, Reference: reference)))
            : physicalPlacements.Select(p => (Order.Map(entry.Path, p.ParentCellFormId).LoadOrderFormId,
                Reference: RecordCollectionFormIdRebaser.RebaseModel(p.Reference, id => Order.Map(entry.Path, id).LoadOrderFormId, source.Game)));
        foreach (var (parentCellId, reference) in placements.Where(p => IsWinner(p.Reference.FormId, entry.Name)))
        {
            if (!_placements.TryGetValue(parentCellId, out var children))
            {
                children = [];
                _placements.Add(parentCellId, children);
            }
            children[reference.FormId] = reference;
        }
        foreach (var (property, idProperty) in RecordLists)
        {
            if (property.GetValue(rebased) is not IList list) { continue; }
            for (var i = list.Count - 1; i >= 0; i--)
            {
                if (idProperty.GetValue(list[i]) is not uint id || !IsWinner(id, entry.Name))
                {
                    list.RemoveAt(i);
                }
            }
        }

        // Name/model lookups must obey the same winner boundary; a missing EDID/FULL in the
        // winner cannot revive a name from an earlier plugin. Source text and opaque bytes remain
        // verbatim and are explicitly outside the rebased typed-field namespace.
        rebased = rebased with
        {
            DialogueTree = null,
            FormIdToEditorId = SelectDictionary(rebased.FormIdToEditorId, entry.Name),
            FormIdToDisplayName = SelectDictionary(rebased.FormIdToDisplayName, entry.Name),
            ModelPathIndex = SelectDictionary(rebased.ModelPathIndex, entry.Name),
            DecodedTreesByFormId = SelectDictionary(rebased.DecodedTreesByFormId, entry.Name),
            AlternateTexturesByFormId = SelectDictionary(rebased.AlternateTexturesByFormId, entry.Name),
            DestructionByFormId = SelectDictionary(rebased.DestructionByFormId, entry.Name),
            TextureHashesByFormId = SelectDictionary(rebased.TextureHashesByFormId, entry.Name),
            BaseMaterialSwapFormIds = SelectDictionary(rebased.BaseMaterialSwapFormIds, entry.Name),
            BaseColorRemapIndices = SelectDictionary(rebased.BaseColorRemapIndices, entry.Name),
            UnparsedTypeCounts = []
        };
        // Every retained top-level ID now occurs in only one source, so MergeWith cannot apply
        // cross-plugin field fallback. Child references are attached separately in Complete.
        Records = Records.MergeWith(rebased, carryBaseTerrainIntoCells: false);
    }

    private Dictionary<uint, T> SelectDictionary<T>(IReadOnlyDictionary<uint, T> values, string plugin)
    {
        return values.Where(p => IsWinner(p.Key, plugin)).ToDictionary(p => p.Key, p => p.Value);
    }

    private void Complete()
    {
        SelectedDialogueAttribution.Rebuild(Records);
        var scripts = Records.Scripts.ToDictionary(script => script.FormId);
        for (var i = 0; i < Records.Quests.Count; i++)
        {
            var quest = Records.Quests[i];
            Records.Quests[i] = quest with
            {
                Variables = quest.Script is { } scriptId && scripts.TryGetValue(scriptId, out var script)
                    ? script.Variables.ToList() : [],
                RelatedNpcFormIds = Records.Dialogues.Where(info => info.QuestFormId == quest.FormId && info.SpeakerFormId is > 0)
                    .Select(info => info.SpeakerFormId!.Value).Distinct().Order().ToList()
            };
        }
        // These per-source caches cannot survive a PROJ override. Keep the direct record links;
        // projectile_report.txt/CSV carry the winning physics without duplicating that model here.
        for (var i = 0; i < Records.Weapons.Count; i++)
        {
            Records.Weapons[i] = Records.Weapons[i] with { ProjectileData = null };
        }
        var projectiles = Records.Projectiles.ToDictionary(projectile => projectile.FormId);
        for (var i = 0; i < Records.Ammo.Count; i++)
        {
            var ammo = Records.Ammo[i];
            var projectileId = ammo.HasRecordLocalProjectileSnapshot ? ammo.RecordLocalProjectileFormId : ammo.ProjectileFormId;
            Records.Ammo[i] = ammo with
            {
                ProjectileFormId = projectileId,
                ProjectileFormIds = projectileId is > 0 ? [projectileId.Value] : [],
                ProjectileModelPath = projectileId is { } linkedId && projectiles.TryGetValue(linkedId, out var projectile)
                    ? projectile.ModelPath : null
            };
        }
        var bounds = ObjectBoundsIndex.Build(Records);
        var models = ObjectBoundsIndex.BuildModelPathIndex(Records);
        var terrainByCell = _terrain.Where(t => t.ParentCellFormId.HasValue)
            .GroupBy(t => t.ParentCellFormId!.Value).ToDictionary(g => g.Key, g => g.ToArray());
        for (var i = 0; i < Records.Cells.Count; i++)
        {
            var cell = Records.Cells[i];
            var placed = _placements.TryGetValue(cell.FormId, out var children)
                ? children.Values.Select(reference => reference with
                {
                    BaseEditorId = Records.FormIdToEditorId.GetValueOrDefault(reference.BaseFormId),
                    ModelPath = models.GetValueOrDefault(reference.BaseFormId),
                    Bounds = bounds.GetValueOrDefault(reference.BaseFormId)
                }).ToList()
                : [];
            var terrain = terrainByCell.TryGetValue(cell.FormId, out var candidates) && candidates.Length == 1
                ? candidates[0] : null;
            Records.Cells[i] = cell with
            {
                PlacedObjects = placed,
                HasPersistentObjects = placed.Any(reference => reference.IsPersistent),
                // LAND has its own winner. Multiple surviving LAND identities in one cell remain
                // ambiguous in component provenance; do not guess engine precedence between them.
                Heightmap = terrain?.Heightmap,
                CapturedLandHeightmap = null,
                LandVisualData = terrain?.VisualData,
                RuntimeTerrainMesh = null
            };
        }
        Records.RelinkWorldspaceCells();
        foreach (var (property, idProperty) in RecordLists)
        {
            if (property.GetValue(Records) is not IList list) { continue; }
            foreach (var item in list)
            {
                if (idProperty.GetValue(item) is uint id) { IncludedFormIds.Add(id); }
            }
        }
        foreach (var (_, reference) in PlacementComponents)
        {
            IncludedFormIds.Add(reference.FormId);
        }
        foreach (var terrain in _terrain) { IncludedFormIds.Add(terrain.FormId); }
        Records = Records with
        {
            DialogueTree = Records.Dialogues.Count == 0 && Records.DialogTopics.Count == 0
                ? null : SelectedDialogueViewBuilder.Build(Records),
            TotalRecordsProcessed = Index.Records.Count,
            UnparsedTypeCounts = Index.Records.Values.Where(r => Status(r) == "not-in-typed-view")
                .GroupBy(r => r.Winner.Signature, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal)
        };
    }
}
