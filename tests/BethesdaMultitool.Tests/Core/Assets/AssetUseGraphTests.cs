using System.Text.Json;
using BethesdaMultitool.Core.Assets;
using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Appearance;
using BethesdaMultitool.Core.Formats.Nif.Rendering.NpcAssembly;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Scene;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Core.Semantic.LoadOrder;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Assets;

public sealed class AssetUseGraphTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Appearance_keeps_selected_record_identity_per_component(bool female)
    {
        var index = new NpcAppearanceIndex { Game = BethesdaGame.FalloutNewVegas };
        var npc = new NpcScanEntry { RaceFormId = 0x03000900, HeadPartFormIds = [0x03000A00], IsFemale = female };
        index.Npcs[0x03000800] = npc;
        index.Races[0x03000900] = new() { MaleHeadModelPath = "head-m.nif", FemaleHeadModelPath = "head-f.nif" };
        index.HeadParts[0x03000A00] = new() { ModelPath = "part.nif" };
        foreach (var (id, type, offset) in new[] { (0x03000800u, "NPC_", 100L), (0x03000900u, "RACE", 200L), (0x03000A00u, "HDPT", 300L) })
            index.Sources[id] = new("Patch.esp", "C:/fixture/Patch.esp", id - 0x02000000, id, type, null, 0, offset);
        index.SourceHashes["C:/fixture/Patch.esp"] = new string('a', 64);
        var appearance = new NpcAppearanceFactory(index).Build(0x03000800, npc, "Wrong.esp");
        var head = Assert.Single(appearance.AssetUses.Nodes, n => n.Component == "head");
        Assert.Equal(female ? "FemaleHeadModelPath" : "MaleHeadModelPath", head.Field);
        Assert.Equal("Patch.esp", head.Owner!.Plugin);
        Assert.Equal(0x01000900u, head.Owner.FileLocalFormId);
        Assert.Equal(0x03000900u, head.Owner.LoadOrderFormId);
        Assert.Equal(200L, head.Owner.RecordOffset);
        Assert.Equal(new string('a', 64), head.Owner.SourceSha256);
        var part = Assert.Single(appearance.AssetUses.Nodes, n => n.Component == "head-part");
        Assert.Equal("HDPT", part.Owner!.Signature);
        Assert.Equal(300L, part.Owner.RecordOffset);
        Assert.Equal(appearance.AssetUses, appearance.CloneWithTextureVariant([], "fixture").AssetUses);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Equipment_keeps_actual_armor_addon_and_shared_mesh_owners(bool leveled)
    {
        var index = new NpcAppearanceIndex { Game = BethesdaGame.FalloutNewVegas };
        index.Armors[1] = new() { BipedFlags = 4, MaleBipedModelPath = "shared.nif", BipedModelListFormId = 4 };
        index.Armors[2] = new() { BipedFlags = 8, MaleBipedModelPath = "shared.nif" };
        index.ArmorAddons[3] = new() { BipedFlags = 16, MaleModelPath = "addon.nif" };
        index.FormLists[4] = [3];
        index.LeveledItems[9] = [1];
        var resolver = new NpcEquipmentResolver(index.Armors, index.ArmorAddons, index.FormLists, index.LeveledItems, index.Game);
        var items = resolver.Resolve([new InventoryItem(leveled ? 9u : 1u, 1), new InventoryItem(2, 1)], false)!;
        var shared = Assert.Single(items, i => i.MeshPath.EndsWith("shared.nif", StringComparison.Ordinal));
        Assert.Equal(new[] { 1u, 2u }, shared.AssetOwners.Select(o => o.ArmorFormId).Order());
        var addon = Assert.Single(items, i => i.MeshPath.EndsWith("addon.nif", StringComparison.Ordinal));
        var owner = Assert.Single(addon.AssetOwners);
        Assert.Equal(1u, owner.ArmorFormId);
        Assert.Equal(3u, owner.AddonFormId);
        Assert.Equal(4u, owner.FormListId);
        Assert.Equal(leveled ? 9u : 1u, owner.InventoryFormId);
        var npc = new NpcAppearance { NpcFormId = 7, EquippedItems = items };
        var graph = NpcAssetUseFactory.Build(index, npc, "fixture.esm", null, null, null, null);
        var mesh = Assert.Single(graph.Nodes, n => n.Component == "equipment-mesh" && n.Owner!.LoadOrderFormId == 3);
        var list = Assert.Single(graph.Nodes, n => n.Id == Assert.Single(mesh.Parents));
        Assert.Equal("FLST", list.Owner!.Signature);
        Assert.Equal("Unavailable", list.Owner.Status);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Shared_physical_read_retains_each_owner_and_explicit_unknown(int owners)
    {
        var root = Path.Combine(Path.GetTempPath(), "bmt-use-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllBytes(Path.Combine(root, "mesh.bin"), [1, 2, 3]);
            using var session = new AssetSelectionSession(AssetSourcePlan.FromPaths([root]));
            var receipt = session.Read("mesh.bin", bytes => bytes).Receipt;
            var builder = new AssetUseGraphBuilder();
            for (var i = 0; i < owners; i++) builder.Add(AssetRecordOwner.Unavailable("ARMA", (uint)i), "equipment", "ModelPath", "mesh.bin");
            var bound = builder.Build().Bind([receipt, receipt]);
            Assert.Equal(owners, bound.Bindings.Length);
            Assert.Single(bound.Bindings.SelectMany(b => b.Reads).Select(r => r.ReceiptId).Distinct());
            Assert.All(bound.Nodes, n => { Assert.Equal("Unavailable", n.Owner!.Status); Assert.Null(n.Owner.FileLocalFormId); });
            using var json = JsonDocument.Parse(AssetSelectionJson.SerializeUses(bound));
            Assert.Equal(owners, json.RootElement.GetProperty("Nodes").GetArrayLength());
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Generated_pixels_retain_inputs_and_record_coefficients_after_cache_reuse(bool nested)
    {
        using var resolver = new NifTextureResolver(_ => null);
        var pixel = DecodedTexture.FromBaseLevel([1, 2, 3, 255], 1, 1);
        resolver.InjectTexture("textures/generated.dds", pixel, new("FaceGen EGT", ["textures/base.dds", "meshes/head.egt"], true));
        if (nested) resolver.InjectTexture("textures/final.dds", pixel, new("default modulation", ["textures/generated.dds"]));
        var builder = new AssetUseGraphBuilder();
        var race = builder.Add(AssetRecordOwner.Unavailable("RACE", 1), "head-texture", "MaleHeadTexturePath", "textures/base.dds");
        var coeff = builder.Add(AssetRecordOwner.Unavailable("NPC_", 7), "facegen-texture-coefficients", "FaceGenTexture", null);
        builder.Add(AssetRecordOwner.Unavailable("RACE", 1), "head", "ModelPath", "meshes/head.nif");
        var mesh = new RenderableSubmesh
        {
            Positions = [], Triangles = [],
            SourceNifPath = "meshes/head.nif", SourceBlockIndex = 7, ShapeName = "head",
            DiffuseTexturePath = nested ? "textures/final.dds" : "textures/generated.dds"
        };
        var assembled = SceneAssetUses.WithMeshes(builder.Build(), [mesh]);
        var consumer = Assert.Single(assembled.Nodes, n => n.Relation == "assembled-mesh-slot").Id;
        var graph = SceneAssetUses.WithGeneratedInputs(assembled, resolver);
        var generated = Assert.Single(graph.Nodes, n => n.Relation == "generated-texture" && n.Field == "FaceGen EGT");
        Assert.Contains(race, generated.Parents);
        Assert.Contains(coeff, generated.Parents);
        Assert.Contains(graph.Nodes, n => n.RequestedPath == "meshes/head.egt" && generated.Parents.Contains(n.Id));
        var reachable = Dependencies(graph, consumer);
        Assert.Contains(generated.Id, reachable);
        Assert.Contains(race, reachable);
        Assert.Contains(coeff, reachable);
        Assert.Contains(graph.Nodes.Single(n => n.RequestedPath == "meshes/head.egt").Id, reachable);
        if (nested)
        {
            var final = Assert.Single(graph.Nodes, n => n.Relation == "generated-texture" && n.Field == "default modulation");
            Assert.Contains(final.Id, graph.Nodes.Single(n => n.Id == consumer).Parents);
            Assert.Contains(generated.Id, final.Parents);
        }
        Assert.Equal(graph, SceneAssetUses.WithGeneratedInputs(graph, resolver));
        var repeated = SceneAssetUses.WithGeneratedInputs(SceneAssetUses.WithMeshes(graph, [mesh]), resolver);
        Assert.Equal(graph.Nodes.Length, repeated.Nodes.Length);
        Assert.Equal(consumer, Assert.Single(repeated.Nodes, n => n.Relation == "assembled-mesh-slot").Id);
        Assert.Equal(graph.Nodes.Single(n => n.Id == consumer).Parents, repeated.Nodes.Single(n => n.Id == consumer).Parents);
        Assert.True(reachable.SetEquals(Dependencies(repeated, consumer)));
        resolver.EvictTexture("textures/generated.dds");
        Assert.Null(resolver.GeneratedInput("textures/generated.dds"));
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 4)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    [InlineData(true, 3)]
    public void Reused_generation_paths_keep_physical_inputs_without_consumer_cycles(bool mutual, int consumerLayout)
    {
        var root = Path.Combine(Path.GetTempPath(), "bmt-generated-use-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllBytes(Path.Combine(root, "generated.dds"), [1, 2, 3]);
            using var selection = new AssetSelectionSession(AssetSourcePlan.FromPaths([root]));
            var receipt = selection.Read("generated.dds", bytes => bytes).Receipt;
            using var resolver = new NifTextureResolver(_ => null);
            var pixel = DecodedTexture.FromBaseLevel([1, 2, 3, 255], 1, 1);
            var selfInput = consumerLayout == 4 ? "Data/textures/Generated.dds" : "generated.dds";
            resolver.InjectTexture("generated.dds", pixel, new("first", [mutual ? "second.dds" : selfInput],
                ObservedInputs: mutual ? [] : [new(selfInput, [receipt])]));
            if (mutual)
                resolver.InjectTexture("second.dds", pixel, new("second", ["generated.dds"],
                    ObservedInputs: [new("generated.dds", [receipt])]));
            var builder = new AssetUseGraphBuilder();
            var component = builder.Add(AssetRecordOwner.Unavailable("RACE", 1), "head", "ModelPath", "head.nif");
            string[] paths = !mutual ? ["generated.dds"] : consumerLayout switch
            {
                1 => ["second.dds", "generated.dds"],
                2 => ["generated.dds"],
                3 => ["second.dds"],
                _ => ["generated.dds", "second.dds"]
            };
            foreach (var path in paths)
                builder.Add(null, "head-slot", "DiffuseTexturePath", path, "assembled-mesh-slot", [component], 7);
            var graph = SceneAssetUses.WithGeneratedInputs(builder.Build(), resolver);
            foreach (var consumer in graph.Nodes.Where(n => n.Relation == "assembled-mesh-slot"))
            {
                var generated = Assert.Single(graph.Nodes, n => n.Relation == "generated-texture" && n.RequestedPath == consumer.RequestedPath);
                var inputPath = !mutual ? selfInput : consumer.RequestedPath == "generated.dds" ? "second.dds" : "generated.dds";
                var physical = Assert.Single(graph.Nodes, n => n.Relation == "generation-input" && n.RequestedPath == inputPath);
                Assert.Empty(physical.Parents);
                Assert.Contains(generated.Id, consumer.Parents);
                Assert.Contains(component, consumer.Parents);
                Assert.Contains(physical.Id, Dependencies(graph, consumer.Id));
                Assert.Equal(physical.Id, Assert.Single(generated.Parents));
                if (!mutual || inputPath == "generated.dds")
                    Assert.Equal(AssetUseRead.From(receipt), Assert.Single(graph.Bindings.Single(b => b.UseId == physical.Id).Reads));
                else Assert.DoesNotContain(graph.Bindings, b => b.UseId == physical.Id);
            }
            Assert.Equal(paths.Length, graph.Nodes.Count(n => n.Relation == "generated-texture"));
            foreach (var node in graph.Nodes) Dependencies(graph, node.Id);
            Assert.Same(graph, SceneAssetUses.WithGeneratedInputs(graph, resolver));
        }
        finally { Directory.Delete(root, true); }
    }

    private static HashSet<string> Dependencies(AssetUseGraph graph, string root)
    {
        var nodes = graph.Nodes.ToDictionary(n => n.Id);
        var found = new HashSet<string>();
        Visit(root, []);
        return found;

        void Visit(string id, HashSet<string> active)
        {
            Assert.True(active.Add(id), "Dependency cycle at " + id);
            foreach (var parent in nodes[id].Parents)
            {
                found.Add(parent);
                Visit(parent, active);
            }
            active.Remove(id);
        }
    }
}
