using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Appearance;
using BethesdaMultitool.Core.Formats.Nif.Rendering.NpcAssembly;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Core.Semantic.LoadOrder;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Npc;

public sealed class NpcAppearanceSelectionTests
{
    [Fact]
    public void Selected_appearance_maps_references_preserves_color_and_combines_idle_children()
    {
        var original = new NpcAppearanceIndex { Game = BethesdaGame.FalloutNewVegas };
        original.Npcs[0x01000800] = new NpcScanEntry
        {
            RaceFormId = 0x01000900, HairColor = 0x01020304, TemplateFormId = 0,
            InventoryItems = [new InventoryItem(0x01000A00, 3)], PackageFormIds = [0x01000B00]
        };
        original.LeveledItemRecords[0x01000A00] = new() { Entries = [new(7, 0x01000C00, 2)] };
        original.Idles[0x01000D00] = new() { ParentIdleFormId = 0x800 };
        var selected = new NpcAppearanceIndex();
        selected.Idles[0x900] = new() { ParentIdleFormId = 0x800 };
        NpcAppearanceSelection.Append(selected, original, id => (id >> 24) == 1 ? id + 0x02000000 : id);

        var npc = selected.Npcs[0x03000800];
        Assert.Equal(0x03000900u, npc.RaceFormId);
        Assert.Equal(0x01020304u, npc.HairColor);
        Assert.Equal(0u, npc.TemplateFormId);
        Assert.Equal(new InventoryItem(0x03000A00, 3), Assert.Single(npc.InventoryItems!));
        Assert.Equal(0x03000B00u, Assert.Single(npc.PackageFormIds!));
        Assert.Equal(new LeveledEntry(7, 0x03000C00, 2), Assert.Single(selected.LeveledItemRecords[0x03000A00].Entries));
        Assert.Equal([0x900u, 0x03000D00u], selected.IdleChildrenByParent[0x800].Order());
        Assert.Equal(0x01000900u, original.Npcs[0x01000800].RaceFormId);
    }

    [Theory]
    [InlineData("selected", true)]
    [InlineData("deleted", false)]
    [InlineData("ambiguous", false)]
    public void Physical_winner_drives_appearance_and_preserves_asset_local_identity(string scenario, bool included)
    {
        var directory = Path.Combine(Path.GetTempPath(), "bmt-appearance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var basePath = Path.Combine(directory, "FalloutNV.esm");
            var otherPath = Path.Combine(directory, "Other.esm");
            var dlcPath = Path.Combine(directory, "Dlc.esm");
            var hedr = new byte[12];
            BinaryPrimitives.WriteSingleLittleEndian(hedr, 1.34f);
            var header = EsmTestRecordBuilder.BuildRecordWithSubrecordsLE("TES4", 0, ("HEDR", hedr));
            var npc = EsmTestRecordBuilder.BuildRecordWithSubrecordsLE("NPC_", 0x01000800,
                ("EDID", Encoding.ASCII.GetBytes("SelectedActor\0")));
            File.WriteAllBytes(basePath, header);
            File.WriteAllBytes(otherPath, header);
            File.WriteAllBytes(dlcPath, [..header, ..npc]);
            var order = PluginLoadOrder.Create([basePath, otherPath, dlcPath], false,
                path => Path.GetFileName(path) == "Dlc.esm" ? ["FalloutNV.esm"] : []);
            var version = new LoadOrderRecordVersion("Dlc.esm", dlcPath, 0x01000800, 0x02000800,
                "NPC_", "SelectedActor", scenario == "deleted" ? 0x20u : 0, header.Length);
            var index = LoadOrderRecordIndex.Create(order, scenario == "ambiguous"
                ? [version, version with { Offset = 500 }] : [version]);
            var resolver = NpcAppearanceResolver.Build(order, index, TestContext.Current.CancellationToken);
            var appearance = resolver.ResolveHeadOnly(0x02000800, "Wrong.esm");
            Assert.Equal(included, appearance != null);
            if (appearance is null) return;
            Assert.Equal(0x02000800u, appearance.NpcFormId);
            Assert.Equal("SelectedActor", appearance.EditorId);
            Assert.Equal(@"meshes\characters\facegendata\facegeom\Dlc.esm\01000800.nif", appearance.FaceGenNifPath);
            Assert.Equal(header.Length, resolver.Sources[0x02000800].Offset);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
