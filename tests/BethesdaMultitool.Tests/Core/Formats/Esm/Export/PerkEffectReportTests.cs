using BethesdaMultitool.CLI.Commands.Analysis;
using BethesdaMultitool.CLI.Shared;
using BethesdaMultitool.CLI.Show;
using BethesdaMultitool.Core.EsmView;
using BethesdaMultitool.Core.Formats.Esm.Export.Csv;
using BethesdaMultitool.Core.Formats.Esm.Export.Geck;
using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Magic;
using BethesdaMultitool.Core.Formats.Esm.Presentation;
using Microsoft.VisualBasic.FileIO;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Export;

public sealed class PerkEffectReportTests
{
    [Fact]
    public void EffectsRetainFunctionIdentityPayloadAndOrderedConditionsAcrossOutputs()
    {
        var perk = new PerkRecord
        {
            FormId = 0x1234, EditorId = "EffectProbe", FullName = "Perk, \"one\"",
            Entries =
            [
                new PerkEntry
                {
                    Type = 2, Rank = 1, Priority = 7, EntryPoint = 37, EntryPointFunction = 11,
                    FunctionType = 2, PerkConditionTabCount = 2, EffectValue = 1.25f,
                    EffectData = "unknown, \"raw\"", RawEntryData = [37, 11, 2], RawFunctionData = [0, 0, 0xA0, 0x3F],
                    ConditionGroups =
                    [
                        new() { RunOn = 1, Conditions = [new() { FunctionIndex = 14, FunctionName = "GetActorValue", Parameter1 = 5, ComparisonOperator = 3, ComparisonValue = 42.5f }] },
                        new() { RunOn = null, Conditions = [new() { FunctionIndex = 449, FunctionName = "HasPerk", Parameter1 = 0x2345, Parameter1FormId = 0x2345, ComparisonValue = 1 }] }
                    ]
                }
            ]
        };
        var csv = CsvMiscWriter.GeneratePerksCsv([perk], FormIdResolver.Empty);
        using var parser = new TextFieldParser(new StringReader(csv))
        {
            TextFieldType = FieldType.Delimited, Delimiters = [","], HasFieldsEnclosedInQuotes = true
        };
        var header = Assert.IsType<string[]>(parser.ReadFields());
        var rows = new List<Dictionary<string, string>>();
        while (!parser.EndOfData)
        {
            var values = Assert.IsType<string[]>(parser.ReadFields());
            Assert.Equal(header.Length, values.Length);
            rows.Add(header.Zip(values).ToDictionary(pair => pair.First, pair => pair.Second));
        }
        Assert.Equal(["PERK", "ENTRY", "ENTRY_GROUP", "ENTRY_CONDITION", "ENTRY_GROUP", "ENTRY_CONDITION"],
            rows.Select(row => row["RowType"]));
        Assert.Equal(perk.FullName, rows[0]["Name"]);
        Assert.Equal("37", rows[1]["EntryPoint"]);
        Assert.Equal("11", rows[1]["EntryPointFunction"]);
        Assert.Equal("2", rows[1]["FunctionType"]);
        Assert.Equal("1.25", rows[1]["EffectValue"]);
        Assert.Equal("250B02", rows[1]["RawEntryData"]);
        Assert.Equal("0000A03F", rows[1]["RawFunctionData"]);
        Assert.Equal("unknown, \"raw\"", rows[1]["EffectData"]);
        Assert.Equal("0", rows[3]["GroupIndex"]);
        Assert.Equal("1", rows[3]["GroupRunOn"]);
        Assert.Equal("42.5", rows[3]["ConditionValue"]);
        Assert.Equal("1", rows[5]["GroupIndex"]);
        Assert.Equal("", rows[5]["GroupRunOn"]);
        Assert.Equal("0x00002345", rows[5]["ConditionParameter1FormID"]);

        var report = GeckEffectsWriter.GeneratePerksReport([perk]);
        var shown = CliHelpers.CaptureSpectreOutput(console =>
        {
            console.Profile.Width = 180;
            Assert.True(ShowCommand.TryRender(new RecordCollection { Perks = [perk] }, FormIdResolver.Empty,
                perk.FormId, null, new ShowRenderContext(console, true, false)));
        });
        Assert.True(RecordDetailPresenter.TryBuildForRecord(perk, null, FormIdResolver.Empty, out var detail));
        var guiRows = RecordDetailPropertyAdapter.Convert(Assert.IsType<RecordDetailModel>(detail));
        var gui = string.Join("\n", guiRows.Select(row => $"{row.Name}: {row.Value}"));
        foreach (var output in new[] { report, shown, gui })
        {
            Assert.Contains("Entry Point: 37", output);
            Assert.Contains("Entry Point Function: 11", output);
            Assert.Contains("Function Type (EPFT): 2", output);
            Assert.Contains("Group [1] Run On: Unassigned", output);
            Assert.Contains("0000A03F", output);
            Assert.Contains("42.5", output);
        }
    }
}
