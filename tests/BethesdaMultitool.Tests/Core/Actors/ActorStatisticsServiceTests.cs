using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Formats.Esm.Export.Csv;
using BethesdaMultitool.Core.Formats.Esm.Export.Geck;
using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;
using Microsoft.VisualBasic.FileIO;
using BethesdaMultitool.Core.Actors;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Character;
using BethesdaMultitool.Core.RuntimeSession;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Actors;

public sealed class ActorStatisticsServiceTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(10)]
    public void LegacyActorOutputsPreserveLuckAndRawLevelWithoutInventingCriticalChance(byte luck)
    {
        var npc = new NpcRecord
        {
            FormId = 0x1000, EditorId = "StatsControl", SpecialStats = [5,5,5,5,5,5,luck],
            Stats = new ActorBaseSubrecord(0x80, 0, 0, 1500, 1, 30, 100, 0, 0, 0, 0, false),
            Factions = [new FactionMembership(0x2000, 1)], Spells = [0x2001],
            Inventory = [new InventoryItem(0x2002, 2)], Packages = [0x2003]
        };
        var unavailable = ActorStatisticsService.CriticalChance();
        Assert.Equal("Unavailable", unavailable.Status);
        Assert.Null(unavailable.Value);
        var csv = CsvActorWriter.GenerateNpcsCsv([npc], FormIdResolver.Empty);
        using var reader = new TextFieldParser(new StringReader(csv))
        {
            TextFieldType = FieldType.Delimited, Delimiters = [","],
            HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = false
        };
        var header = Assert.IsType<string[]>(reader.ReadFields());
        Assert.Equal("CritChance", header[29]);
        Assert.Equal("SubDetail", header[^3]);
        var row = Assert.IsType<string[]>(reader.ReadFields());
        Assert.Equal(header.Length, row.Length);
        var fields = header.Zip(row).ToDictionary(pair => pair.First, pair => pair.Second);
        Assert.Equal(luck.ToString(System.Globalization.CultureInfo.InvariantCulture), fields["SPECIAL_LK"]);
        Assert.Equal("1500", fields["Level"]);
        Assert.Equal("ACBS.Int16", fields["LevelEncoding"]);
        Assert.Empty(fields["CritChance"]);
        Assert.Equal("Unavailable", fields["CritChanceStatus"]);
        var subrows = 0;
        while (!reader.EndOfData)
        {
            var child = Assert.IsType<string[]>(reader.ReadFields());
            Assert.Equal(header.Length, child.Length);
            Assert.Empty(child[^1]);
            Assert.Empty(child[^2]);
            subrows++;
        }
        Assert.Equal(4, subrows);
        var report = GeckActorDetailWriter.BuildNpcReport(npc, FormIdResolver.Empty);
        var reportFields = report.Sections.SelectMany(section => section.Fields).ToArray();
        Assert.Equal("Unavailable", Assert.Single(reportFields, field => field.Key == "Critical Chance").Value.Display);
        Assert.Equal("1500", Assert.Single(reportFields, field => field.Key == "Level (encoded)").Value.Display);
        var text = GeckActorWriter.GenerateNpcsReport([npc], FormIdResolver.Empty);
        Assert.Matches(@"Critical Chance:\s+Unavailable", text);
        Assert.Contains("Level (encoded): 1500", text, StringComparison.Ordinal);

        var creature = new CreatureRecord { FormId = npc.FormId, Stats = npc.Stats, Factions = npc.Factions, Spells = npc.Spells };
        using var creatureReader = new TextFieldParser(new StringReader(CsvActorWriter.GenerateCreaturesCsv([creature], FormIdResolver.Empty)))
        {
            TextFieldType = FieldType.Delimited, Delimiters = [","],
            HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = false
        };
        var creatureHeader = Assert.IsType<string[]>(creatureReader.ReadFields());
        var creatureRow = Assert.IsType<string[]>(creatureReader.ReadFields());
        Assert.Equal(creatureHeader.Length, creatureRow.Length);
        Assert.Equal("ACBS.Int16", creatureRow[^1]);
        while (!creatureReader.EndOfData) Assert.Equal(creatureHeader.Length, creatureReader.ReadFields()!.Length);
    }

    [Theory]
    [InlineData(null, "Unavailable")]
    [InlineData(0, "Stored")]
    [InlineData(250, "Stored")]
    public void StoredHealthPreservesZeroAndNeverSynthesizesFromSpecial(int? health, string status)
    {
        var value = ActorStatisticsService.StoredHealth(new NpcRecord { BaseHealth = health, SpecialStats = [5,5,5,5,5,5,5] });
        Assert.Equal(status, value.Status);
        Assert.Equal(health.HasValue ? (double?)health.Value : null, value.Value);
    }

    [Theory]
    [InlineData(16u, true, true)]
    [InlineData(17u, true, false)]
    [InlineData(16u, false, false)]
    public async Task ObservedZeroRequiresActualReferenceBaseAndSourceIdentity(uint observedBase, bool bindSource, bool matches)
    {
        using var temp = CliExeRunner.CreateTempDirectory();
        var trace = Path.Combine(temp.Path, "trace.jsonl");
        const string hash = "abcdef";
        var pluginHash = new string('a', 64);
        string[] lines =
        [
            new JsonObject { ["kind"]="capture-header", ["schema"]="bmt/runtime-trace", ["version"]=1,
                ["identity"] = new JsonObject { ["sequence"]=1, ["executableFileSha256"]=hash,
                    ["activePluginIdentityStatus"]="complete", ["activePlugins"]=new JsonArray(new JsonObject
                    { ["index"]=0, ["name"]="FalloutNV.esm", ["sha256"]=pluginHash,
                        ["hashScope"]="copied-backing-file", ["status"]="verified" }) } }.ToJsonString(),
            "{\"kind\":\"capture-start\",\"protocol\":1,\"sequence\":2,\"dropped\":0}",
            new JsonObject { ["kind"]="snapshot", ["protocol"]=1, ["sequence"]=3, ["dropped"]=0,
                ["targetKind"]="actor", ["statistic"]="Health", ["component"]="current", ["engineTargetFormId"]=20,
                ["engineTargetBaseFormId"]=observedBase, ["value"]=0 }.ToJsonString(),
            "{\"kind\":\"capture-end\",\"status\":\"completed\",\"protocol\":1,\"sequence\":4,\"dropped\":0}",
            "{\"kind\":\"capture-footer\",\"status\":\"completed\",\"events\":3,\"dropped\":0,\"snapshots\":1,\"errors\":0}"
        ];
        await File.WriteAllLinesAsync(trace, lines, TestContext.Current.CancellationToken);
        var scenario = Path.Combine(temp.Path, "scenario.json");
        await File.WriteAllTextAsync(scenario, new JsonObject { ["runtimeTrace"]="trace.jsonl",
            ["referenceFormId"]="0x14", ["executableSha256"]=hash }.ToJsonString(), TestContext.Current.CancellationToken);
        var values = await ActorStatisticsService.InspectAsync(new(16, "Actor", "NPC_", [], [], null),
            "pc-retail", scenario, TestContext.Current.CancellationToken,
            bindSource ? new RuntimeTraceSources([new(0, "FalloutNV.esm", pluginHash)], true) : null);
        var observed = values.Where(value => value.Status == "Observed").ToArray();
        if (!matches) Assert.Empty(observed);
        else
        {
            var value = Assert.Single(observed);
            Assert.Equal(0d, value.Value);
            Assert.Equal("Health.current", value.Key);
            Assert.Equal(20u, value.ReferenceFormId);
            Assert.Equal("Complete", value.TraceStatus);
            Assert.Equal(64, value.TraceSha256!.Length);
        }
        Assert.Contains(values, value => value.Key == "RuntimeDamage" && value.Status == "Unavailable");
        Assert.Contains(values, value => value.Key == "RuntimeCriticalChance" && value.Status == "Unavailable");
        if (!bindSource) Assert.Contains(values, value => value.Key == "RuntimeTrace" && value.Status == "Unbound");
    }
}
