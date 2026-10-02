using BethesdaMultitool.Core.Assets;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Semantic.LoadOrder;
using BethesdaMultitool.Core.WorldData;
using Xunit;

namespace BethesdaMultitool.Tests.Core.WorldData;

public sealed class WorldAssetRecordTests
{
    [Fact]
    public void Placement_and_overridden_model_keep_distinct_physical_owners_without_a_later_file_hash()
    {
        var (view, before) = View();
        var catalog = WorldAssetRecordCatalog.Selected(view);
        var placement = Assert.Single(Assert.Single(view.Records.Cells).PlacedObjects);
        var graph = catalog.PlacementUses(placement, placement.ModelPath);
        var placed = Assert.Single(graph.Nodes.Where(n => n.Component == "placement"));
        var model = Assert.Single(graph.Nodes.Where(n => n.Component == "model"));
        Assert.Equal("A.esm", placed.Owner!.Plugin);
        Assert.Equal(40L, placed.Owner.RecordOffset);
        Assert.Equal("B.esm", model.Owner!.Plugin);
        Assert.Equal(120L, model.Owner.RecordOffset);
        Assert.Equal(0x900u, model.Owner.FileLocalFormId);
        Assert.Equal(0x900u, model.Owner.LoadOrderFormId);
        Assert.Equal("meshes\\new.nif", model.RequestedPath);
        Assert.Null(model.Owner.SourceSha256);
        Assert.Null(placed.Owner.SourceSha256);
        Assert.Equal("old.nif", before.Statics[0].ModelPath);
        Assert.Equal("old.nif", Assert.Single(before.Cells[0].PlacedObjects).ModelPath);
        Assert.Contains("\"SourceSha256\":null", AssetSelectionJson.SerializeUses(graph));
    }

    [Theory]
    [InlineData("deleted", "Deleted")]
    [InlineData("duplicate", "Ambiguous")]
    [InlineData("type-conflict", "TypeConflict")]
    [InlineData("unparsed", "Unparsed")]
    [InlineData("missing", "Unavailable")]
    public void Excluded_base_never_borrows_an_older_model_owner(string scenario, string status)
    {
        var (view, _) = View(scenario);
        var catalog = WorldAssetRecordCatalog.Selected(view);
        var placed = new PlacedReference { FormId = 0x200, BaseFormId = 0x900, Offset = 40, ModelPath = "old.nif" };
        var graph = catalog.PlacementUses(placed, placed.ModelPath);
        Assert.Equal(status, catalog.Owner(0x900).Status);
        var model = Assert.Single(graph.Nodes.Where(n => n.Component == "model"));
        Assert.Equal(status, model.Owner!.Status);
        Assert.Equal("placement-model-owner-unavailable", model.Relation);
        Assert.Empty(catalog.DeclaredModelUses([Receipt("meshes\\old.nif")]).Nodes);
        var candidates = graph.Nodes.Where(n => n.Component == "record-candidate").ToArray();
        Assert.Equal(scenario == "duplicate" ? 3 : scenario == "type-conflict" ? 2 : 0, candidates.Length);
        Assert.All(candidates, c => { Assert.Equal("Candidate", c.Owner!.Status); Assert.Null(c.RequestedPath); });
    }

    [Theory]
    [InlineData("Captured")]
    [InlineData("CapturedWithMasterPreview")]
    [InlineData("SaveOverlay")]
    public void Captured_and_preview_paths_do_not_acquire_a_selected_plugin_owner(string mode)
    {
        var catalog = WorldAssetRecordCatalog.Unavailable(mode);
        var placement = new PlacedReference { FormId = 0x200, BaseFormId = 0x900, Offset = 400, ModelPath = "new.nif" };
        var graph = catalog.PlacementUses(placement, placement.ModelPath).Bind([Receipt("meshes\\new.nif")]);
        Assert.Equal(mode, catalog.Mode);
        Assert.All(graph.Nodes, n => Assert.Equal("Unavailable", n.Owner!.Status));
        Assert.Equal("ObservedReadAttempts", Assert.Single(graph.Bindings).Status);
        Assert.Null(graph.Nodes.Last().Owner!.Plugin);
        Assert.Empty(catalog.DeclaredModelUses([Receipt("meshes\\new.nif")]).Nodes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Different_physical_placement_or_model_field_is_explicit(bool positionMismatch)
    {
        var (view, _) = View();
        var catalog = WorldAssetRecordCatalog.Selected(view);
        var placement = Assert.Single(Assert.Single(view.Records.Cells).PlacedObjects);
        if (positionMismatch)
            Assert.Equal("PhysicalMismatch", catalog.PlacementOwner(placement with { Offset = 400 }).Status);
        else
            Assert.Equal("FieldUnavailable", catalog.ModelOwner(placement, "borrowed.nif").Status);
    }

    [Fact]
    public void One_cached_model_read_can_bind_two_declared_base_owners_without_a_placement_claim()
    {
        var (view, _) = View("shared");
        var catalog = WorldAssetRecordCatalog.Selected(view);
        var receipt = Receipt("meshes\\new.nif");
        var graph = catalog.DeclaredModelUses([receipt]).Bind([receipt]);
        var models = graph.Nodes.Where(n => n.Component == "model").ToArray();
        Assert.Equal(2, models.Length);
        Assert.Equal(new uint?[] { 0x900, 0x901 }, models.Select(n => n.Owner!.LoadOrderFormId));
        Assert.Equal(2, graph.Bindings.Length);
        Assert.Equal(graph.Bindings[0].Reads[0].ReceiptId, graph.Bindings[1].Reads[0].ReceiptId);
        Assert.DoesNotContain(graph.Nodes, n => n.Component == "placement");
    }

    [Theory]
    [InlineData("Data/meshes/new.nif", "meshes\\new.nif")]
    [InlineData("trees/tree.spt", "trees\\tree.spt")]
    [InlineData("new.nif", "meshes\\new.nif")]
    public void Declared_model_binding_uses_the_renderer_path_rules(string authored, string request)
    {
        var (view, _) = View(model: authored);
        var catalog = WorldAssetRecordCatalog.Selected(view);
        var receipt = Receipt(request);
        var graph = catalog.DeclaredModelUses([receipt]).Bind([receipt]);
        Assert.Equal(request, Assert.Single(graph.Nodes.Where(n => n.Component == "model")).RequestedPath);
        Assert.Equal("ObservedReadAttempts", Assert.Single(graph.Bindings).Status);
    }

    private static AssetSelectionReceipt Receipt(string path)
    {
        var candidate = new AssetCandidate(0, "edition-meshes.bsa", path, 0, 128, 4, "primary", 4096, 1);
        return new("declared-order", "plan", path, AssetSelectionStatus.Selected, [candidate], candidate,
            [new AssetReadAttempt(candidate, 0, "read")], new string('a', 64), 7);
    }

    private static (LoadOrderSelectionView View, RecordCollection Before) View(string scenario = "selected", string model = "new.nif")
    {
        var order = PluginLoadOrder.Create(["A.esm", "B.esm"], false, p => p == "A.esm" ? [] : ["A.esm"]);
        LoadOrderRecordVersion V(string plugin, uint id, string signature, long offset, uint flags = 0) =>
            new(plugin, plugin, id, id, signature, null, flags, offset);
        var versions = new List<LoadOrderRecordVersion> { V("A.esm", 0x100, "CELL", 20), V("A.esm", 0x200, "REFR", 40) };
        if (scenario != "missing")
        {
            versions.Add(V("A.esm", 0x900, "STAT", 80));
            versions.Add(V("B.esm", 0x900, scenario == "type-conflict" ? "BOOK" : "STAT", 120, scenario == "deleted" ? 0x20u : 0));
        }
        if (scenario == "duplicate") versions.Add(V("B.esm", 0x900, "STAT", 240));
        if (scenario == "shared") versions.Add(V("B.esm", 0x901, "STAT", 160));
        var before = new RecordCollection
        {
            Statics = [new StaticRecord { FormId = 0x900, ModelPath = "old.nif", Offset = 80 }],
            Cells = [new CellRecord { FormId = 0x100, PlacedObjects =
                [new PlacedReference { FormId = 0x200, BaseFormId = 0x900, Offset = 40, ModelPath = "old.nif" }] }]
        };
        var later = new RecordCollection
        {
            Statics = scenario is "unparsed" or "missing" or "type-conflict" ? [] :
                [new StaticRecord { FormId = 0x900, ModelPath = model, Offset = 120 }]
        };
        if (scenario == "shared") later.Statics.Add(new StaticRecord { FormId = 0x901, ModelPath = "new.nif", Offset = 160 });
        return (LoadOrderSelectionView.FromSources(order, LoadOrderRecordIndex.Create(order, versions),
            [(order.Entries[0], before), (order.Entries[1], later)]), before);
    }
}
