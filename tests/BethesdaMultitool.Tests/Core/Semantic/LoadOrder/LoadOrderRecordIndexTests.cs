using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Character;
using BethesdaMultitool.Core.Semantic.LoadOrder;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Semantic.LoadOrder;

public sealed class LoadOrderRecordIndexTests
{
    [Fact]
    public void Keeps_overrides_separates_dlc_ids_and_reports_ambiguous_editor_ids()
    {
        using var directory = CliExeRunner.CreateTempDirectory();
        var root = WritePlugin(directory.Path, "FalloutNV.esm", [],
            EsmTestFileBuilder.BuildRecord("NPC_", 0x800, 0, ("EDID", Z("Original"))));
        var first = WritePlugin(directory.Path, "Earlier.esm", ["FalloutNV.esm"],
            EsmTestFileBuilder.BuildRecord("NPC_", 0x800, 0, ("EDID", Z("Override"))),
            EsmTestFileBuilder.BuildRecord("NPC_", 0x01000800, 0, ("EDID", Z("SameName"))));
        var last = WritePlugin(directory.Path, "Later.esm", ["FalloutNV.esm"],
            EsmTestFileBuilder.BuildRecord("NPC_", 0x01000800, 0, ("EDID", Z("SameName"))));
        var order = PluginLoadOrder.Open([root, first, last]);
        var index = LoadOrderRecordIndex.Build(order);
        Assert.Equal(3, index.Records.Count);
        Assert.Equal(2, index.Records[0x800].Versions.Count);
        Assert.Equal("Earlier.esm", index.Records[0x800].Winner.Plugin);
        Assert.Equal("FalloutNV.esm", index.Records[0x800].OwnerPlugin);
        Assert.Equal(0x02000800u, index.ResolveTarget("Later.esm:0x01000800", order));
        Assert.Equal(0x01000800u, index.ResolveTarget("0x01000800", order));
        Assert.Equal(2, index.FindByEditorId("samename").Count);
        var error = Assert.Throws<ArgumentException>(() => index.ResolveTarget("SameName", order));
        Assert.Contains("Earlier.esm", error.Message, StringComparison.Ordinal);
        Assert.Contains("Later.esm", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Deleted_and_cross_type_winners_do_not_leave_stale_typed_actors()
    {
        var order = PluginLoadOrder.Create(["A.esm", "B.esm"], false, p => p == "A.esm" ? [] : ["A.esm"]);
        var index = LoadOrderRecordIndex.Create(order,
        [
            new("A.esm", "A.esm", 0x800, 0x800, "NPC_", "Deleted", 0, 24),
            new("B.esm", "B.esm", 0x800, 0x800, "NPC_", null, 0x20, 24),
            new("A.esm", "A.esm", 0x801, 0x801, "NPC_", "ChangedType", 0, 48),
            new("B.esm", "B.esm", 0x801, 0x801, "CREA", "ChangedType", 0, 48),
            new("B.esm", "B.esm", 0x803, 0x803, "NPC_", "Duplicate", 0, 72),
            new("B.esm", "B.esm", 0x803, 0x803, "NPC_", "Duplicate", 0, 96)
        ]);
        var records = new RecordCollection
        {
            Npcs = [new NpcRecord { FormId = 0x800 }, new NpcRecord { FormId = 0x801 }, new NpcRecord { FormId = 0x802 }, new NpcRecord { FormId = 0x803 }],
            Creatures = [new CreatureRecord { FormId = 0x801 }],
            FormIdToEditorId = new() { [0x800] = "Deleted", [0x801] = "ChangedType" }
        };
        records = LoadOrderSession.RemoveUnusableRecords(records, index);
        Assert.Equal(0x802u, Assert.Single(records.Npcs).FormId);
        Assert.Empty(records.Creatures);
        Assert.Empty(records.FormIdToEditorId);
        Assert.True(index.Records[0x800].DeletedByWinner);
        Assert.True(index.Records[0x801].TypeConflict);
        Assert.True(index.Records[0x803].HasAmbiguousWinningRecords);
    }

    internal static string WritePlugin(string directory, string name, string[] masters, params byte[][] records)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, new EsmTestFileBuilder().WithMasters(masters).AddTopLevelGrup("NPC_", records).Build());
        return path;
    }

    internal static byte[] Z(string value) => System.Text.Encoding.ASCII.GetBytes(value + "\0");
}
