using System.Text.Json;
using BethesdaMultitool.Core.Formats.Esm.Export.Report;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Character;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Item;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Magic;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Script.Conditions;
using BethesdaMultitool.Core.Semantic.LoadOrder;
using BethesdaMultitool.Tests.Core.Semantic.LoadOrder;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.CLI;

public sealed class EsmReportsLoadOrderTests
{
    [Fact]
    public async Task Reports_preserve_dlc_namespaces_and_explain_winners_and_exclusions_in_the_shipped_cli()
    {
        using var directory = CliExeRunner.CreateTempDirectory();
        LoadOrderRecordIndexTests.WritePlugin(directory.Path, "FalloutNV.esm", [],
            Npc(0x800, "OldName", ("FULL", Z("Old full name"))), Npc(0x801, "DeletedActor"), Npc(0x802, "ChangedType"));
        LoadOrderRecordIndexTests.WritePlugin(directory.Path, "Earlier.esm", ["FalloutNV.esm"],
            Npc(0x01000800, "EarlierActor"));
        var last = LoadOrderRecordIndexTests.WritePlugin(directory.Path, "Later.esm", ["FalloutNV.esm"],
            Npc(0x800, "NewName"),
            Npc(0x01000800, "LaterActor", ("SCRI", new byte[] { 0, 9, 0, 1 })),
            EsmTestFileBuilder.BuildRecord("NPC_", 0x801, 0x20),
            EsmTestFileBuilder.BuildRecord("BOOK", 0x802, 0, ("EDID", Z("ChangedType"))),
            Npc(0x01000802, "DuplicateOne"), Npc(0x01000802, "DuplicateTwo"));
        var output = Path.Combine(directory.Path, "reports");

        var result = await CliExeRunner.RunAsync(["--plain", "esm", "reports", last, "--load-order",
            "FalloutNV.esm;Earlier.esm;Later.esm", "--output", output], TestContext.Current.CancellationToken);

        Assert.True(result.ExitCode == 0, result.Describe());
        var csv = await File.ReadAllTextAsync(Path.Combine(output, "npcs.csv"), TestContext.Current.CancellationToken);
        Assert.Contains("0x00000800,NewName,", csv, StringComparison.Ordinal);
        Assert.Contains("0x01000800,EarlierActor,", csv, StringComparison.Ordinal);
        Assert.Contains("0x02000800,LaterActor,", csv, StringComparison.Ordinal);
        Assert.Contains("0x02000900", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("Old full name", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("DeletedActor", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("ChangedType", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("Duplicate", csv, StringComparison.Ordinal);
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output, "report_sources.json"),
            TestContext.Current.CancellationToken));
        Assert.Equal("explicit-load-order", manifest.RootElement.GetProperty("formIdNamespace").GetString());
        Assert.Equal(3, manifest.RootElement.GetProperty("loadOrder").GetArrayLength());
        var counts = manifest.RootElement.GetProperty("recordCounts");
        Assert.Equal(1, counts.GetProperty("excluded-deleted").GetInt32());
        Assert.Equal(1, counts.GetProperty("excluded-type-conflict").GetInt32());
        Assert.Equal(1, counts.GetProperty("excluded-ambiguous-winner").GetInt32());
        var provenance = await File.ReadAllTextAsync(Path.Combine(output, "record_provenance.csv"),
            TestContext.Current.CancellationToken);
        Assert.Contains("0x00000800,FalloutNV.esm,Later.esm,included-in-typed-view", provenance, StringComparison.Ordinal);
        Assert.Contains("0x02000800,Later.esm,Later.esm,included-in-typed-view,Later.esm,0x01000800", provenance, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(output, "dialogue_tree.txt")));
    }

    [Fact]
    public void Unparsed_late_winner_cannot_relabel_an_older_typed_record_or_its_names()
    {
        var order = Order();
        var index = LoadOrderRecordIndex.Create(order,
        [
            Version("A.esm", 0x800, "NPC_"), Version("B.esm", 0x800, "NPC_")
        ]);
        var old = new RecordCollection
        {
            Npcs = [new NpcRecord { FormId = 0x800, EditorId = "OldActor", FullName = "Old full name" }],
            FormIdToEditorId = new() { [0x800] = "OldActor" },
            FormIdToDisplayName = new() { [0x800] = "Old full name" }
        };
        // Raw indexing found B's override, but semantic parsing supplied no typed NPC for it.
        var view = LoadOrderReportView.FromSources(order, index,
            [(order.Entries[0], old), (order.Entries[1], new RecordCollection())]);

        Assert.Empty(view.Records.Npcs);
        Assert.Empty(view.Records.FormIdToEditorId);
        Assert.Empty(view.Records.FormIdToDisplayName);
        Assert.Equal("not-in-typed-view", view.Status(index.Records[0x800]));
        Assert.False(LoadOrderReportWriter.Generate(view, "test").ContainsKey("npcs.csv"));
        Assert.Single(old.Npcs); // Selection must not mutate a reusable source collection.
    }

    [Fact]
    public void Cleared_cell_fields_stay_clear_while_independent_children_survive_move_or_delete()
    {
        var order = Order();
        var index = LoadOrderRecordIndex.Create(order,
        [
            Version("A.esm", 0x1000, "CELL"), Version("A.esm", 0x2000, "REFR"),
            Version("A.esm", 0x2001, "REFR"), Version("A.esm", 0x2002, "REFR"),
            Version("A.esm", 0x900, "STAT"), Version("A.esm", 0x901, "STAT"),
            Version("B.esm", 0x1000, "CELL"), Version("B.esm", 0x1001, "CELL"),
            Version("B.esm", 0x2001, "REFR"), Version("B.esm", 0x2002, "REFR", 0x20),
            Version("B.esm", 0x900, "STAT"), Version("B.esm", 0x901, "STAT", 0x20)
        ]);
        var old = new RecordCollection
        {
            Cells = [new CellRecord
            {
                FormId = 0x1000, FullName = "Old cell name", WaterFormId = 0x900,
                PlacedObjects = [Placement(0x2000) with { BaseEditorId = "OldBase", ModelPath = "old.nif" },
                    Placement(0x2001), Placement(0x2002)]
            }],
            FormIdToEditorId = new() { [0x900] = "OldBase", [0x901] = "DeletedBase" }
        };
        var latest = new RecordCollection
        {
            Cells =
            [
                new CellRecord { FormId = 0x1000, EditorId = "NewCell", Heightmap = new LandHeightmap { HeightDeltas = [] } },
                new CellRecord { FormId = 0x1001, PlacedObjects = [Placement(0x2001) with { BaseFormId = 0x901, BaseEditorId = "DeletedBase" }] }
            ],
            Statics = [new StaticRecord { FormId = 0x900, EditorId = "NewBase", ModelPath = "new.nif" }],
            FormIdToEditorId = new() { [0x900] = "NewBase" }
        };
        var view = LoadOrderReportView.FromSources(order, index,
            [(order.Entries[0], old), (order.Entries[1], latest)]);

        var cell = Assert.Single(view.Records.Cells, c => c.FormId == 0x1000);
        Assert.Equal("NewCell", cell.EditorId);
        Assert.Null(cell.FullName);
        Assert.Null(cell.WaterFormId);
        Assert.Null(cell.Heightmap);
        Assert.Equal(0x2000u, Assert.Single(cell.PlacedObjects).FormId);
        Assert.Equal("NewBase", cell.PlacedObjects[0].BaseEditorId);
        Assert.Equal("new.nif", cell.PlacedObjects[0].ModelPath);
        Assert.Equal(0x2001u, Assert.Single(Assert.Single(view.Records.Cells, c => c.FormId == 0x1001).PlacedObjects).FormId);
        Assert.Null(view.Records.Cells.Single(c => c.FormId == 0x1001).PlacedObjects[0].BaseEditorId);
        Assert.DoesNotContain(0x2002u, view.IncludedFormIds);
        Assert.Equal(3, old.Cells[0].PlacedObjects.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cross_record_caches_use_surviving_winners_and_clear_deleted_dependencies(bool deleted)
    {
        var order = Order();
        var flags = deleted ? 0x20u : 0u;
        var index = LoadOrderRecordIndex.Create(order,
        [
            Version("A.esm", 0x800, "SCPT"), Version("A.esm", 0x810, "QUST"),
            Version("A.esm", 0x900, "PROJ"), Version("A.esm", 0x910, "AMMO"), Version("A.esm", 0x920, "WEAP"),
            Version("B.esm", 0x800, "SCPT", flags), Version("B.esm", 0x900, "PROJ", flags)
        ]);
        var original = new RecordCollection
        {
            Quests = [new QuestRecord { FormId = 0x810, Script = 0x800, Variables = [new(1, "OldVariable", 1)], RelatedNpcFormIds = [0xA00] }],
            Scripts = [new ScriptRecord { FormId = 0x800, Variables = [new(1, "OldVariable", 1)] }],
            Ammo = [new AmmoRecord { FormId = 0x910, ProjectileFormId = 0x900, ProjectileFormIds = [0x900, 0xBAD], ProjectileModelPath = "old.nif" }],
            Weapons = [new WeaponRecord { FormId = 0x920, ProjectileFormId = 0x900, ProjectileData = new ProjectilePhysicsData { Speed = 42 } }],
            Projectiles = [new ProjectileRecord { FormId = 0x900, ModelPath = "old.nif" }]
        };
        var overlay = new RecordCollection
        {
            Scripts = [new ScriptRecord { FormId = 0x800, Variables = [new(1, "NewVariable", 1)] }],
            Projectiles = [new ProjectileRecord { FormId = 0x900, ModelPath = "new.nif" }]
        };
        var view = LoadOrderReportView.FromSources(order, index,
            [(order.Entries[0], original), (order.Entries[1], overlay)]);
        var conditions = ConditionDisplayContext.From(view.Records, view.Records.CreateResolver());
        Assert.Equal(deleted ? null : "NewVariable", conditions.TryGetQuestVariableName(0x810, 1, out _));
        Assert.Empty(Assert.Single(view.Records.Quests).RelatedNpcFormIds);
        Assert.Null(Assert.Single(view.Records.Weapons).ProjectileData);
        var ammo = Assert.Single(view.Records.Ammo);
        Assert.Equal(deleted ? null : "new.nif", ammo.ProjectileModelPath);
        Assert.Equal([0x900u], ammo.ProjectileFormIds);
    }

    [Theory]
    [InlineData("missing-master", "Missing master")]
    [InlineData("allow-without-order", "requires --load-order")]
    [InlineData("occupied-directory", "empty output directory")]
    [InlineData("single-file-in-merged-directory", "cannot overwrite a load-order report directory")]
    public async Task Invalid_report_context_fails_without_writing_misleading_output(string scenario, string expectedError)
    {
        using var directory = CliExeRunner.CreateTempDirectory();
        var plugin = LoadOrderRecordIndexTests.WritePlugin(directory.Path, "Later.esm", ["FalloutNV.esm"], Npc(0x01000800, "Actor"));
        var output = Path.Combine(directory.Path, "reports");
        var arguments = new List<string> { "--plain", "esm", "reports", plugin, "--output", output };
        if (scenario == "allow-without-order") { arguments.Add("--allow-missing-masters"); }
        else if (scenario != "single-file-in-merged-directory") { arguments.AddRange(["--load-order", "Later.esm"]); }
        if (scenario == "occupied-directory")
        {
            Directory.CreateDirectory(output);
            await File.WriteAllTextAsync(Path.Combine(output, "previous-report.txt"), "preserve", TestContext.Current.CancellationToken);
        }
        if (scenario == "single-file-in-merged-directory")
        {
            Directory.CreateDirectory(output);
            await File.WriteAllTextAsync(Path.Combine(output, "report_sources.json"), "preserve", TestContext.Current.CancellationToken);
        }
        var result = await CliExeRunner.RunAsync(arguments, TestContext.Current.CancellationToken);
        Assert.True(result.ExitCode != 0, result.Describe());
        Assert.Contains(expectedError, result.StandardOutput + result.StandardError, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(output, "summary.txt")));
        if (scenario == "occupied-directory")
        {
            Assert.Equal("preserve", await File.ReadAllTextAsync(Path.Combine(output, "previous-report.txt"), TestContext.Current.CancellationToken));
        }
        if (scenario == "single-file-in-merged-directory")
        {
            Assert.Equal("preserve", await File.ReadAllTextAsync(Path.Combine(output, "report_sources.json"), TestContext.Current.CancellationToken));
        }
    }

    private static PluginLoadOrder Order() => PluginLoadOrder.Create(["A.esm", "B.esm"], false,
        path => path == "A.esm" ? [] : ["A.esm"]);

    private static LoadOrderRecordVersion Version(string plugin, uint id, string signature, uint flags = 0) =>
        new(plugin, plugin, id, id, signature, null, flags, 24);

    private static PlacedReference Placement(uint id) => new() { FormId = id, BaseFormId = 0x900 };

    private static byte[] Npc(uint id, string editorId, params (string Signature, byte[] Data)[] fields) =>
        EsmTestFileBuilder.BuildRecord("NPC_", id, 0, [("EDID", Z(editorId)), .. fields]);

    private static byte[] Z(string value) => LoadOrderRecordIndexTests.Z(value);
}
