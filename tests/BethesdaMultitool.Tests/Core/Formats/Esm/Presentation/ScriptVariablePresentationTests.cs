using System.Text.Json;
using BethesdaMultitool.Core.Formats.Esm.Export.Geck;
using BethesdaMultitool.Core.Formats.Esm.Graph;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Presentation;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Presentation;

public sealed class ScriptVariablePresentationTests
{
    // A reference-table slot may name a FormID, another local, or this local. Only the
    // last case identifies myLink as a ref; the raw integer flag remains independent.
    [Theory]
    [InlineData(0, 0x80000001u, "ref", "scrv-local-reference")]
    [InlineData(0, 0x00000001u, "float", "slsd-storage-flag")]
    [InlineData(0, 0x80000002u, "float", "slsd-storage-flag")]
    [InlineData(1, 0x80000002u, "int", "slsd-storage-flag")]
    [InlineData(1, 0x80000001u, "ref", "scrv-local-reference; conflicts-with-integer-storage")]
    public void TerminalLocalType_UsesItsScrvEntryAndPreservesStorageEvidence(
        byte rawType, uint reference, string expectedType, string expectedEvidence)
    {
        var variable = new ScriptVariableInfo(1, "myLink", rawType);
        var terminal = new TerminalRecord
        {
            FormId = 0x00132162,
            EditorId = "NVCCCoreDoorTerminalHard",
            MenuItems =
            [
                new TerminalMenuItem
                {
                    Text = "Disengage Lock",
                    Variables = [variable],
                    ReferencedObjects = [reference]
                }
            ]
        };
        var records = new RecordCollection { Game = BethesdaGame.FalloutNewVegas, Terminals = [terminal] };
        var resolver = records.CreateResolver();

        Assert.True(RecordDetailPresenter.TryBuildForLookup(records, resolver, terminal.FormId, null, out var detail));
        var entry = Assert.Single(detail!.Sections.SelectMany(section => section.Entries),
            candidate => candidate.Label == "Result Script Variables");
        var declaration = Assert.Single(entry.Items!).Value;
        Assert.StartsWith($"{expectedType} myLink", declaration, StringComparison.Ordinal);
        if (expectedType == "ref")
        {
            Assert.Contains(expectedEvidence, declaration, StringComparison.Ordinal);
            Assert.Contains($"storage type byte {rawType}", declaration, StringComparison.Ordinal);
        }

        var report = GeckTextContentWriter.GenerateTerminalsReport([terminal], resolver);
        Assert.Contains(declaration!, report, StringComparison.Ordinal);

        using var output = new MemoryStream();
        TerminalGraphWriter.WriteJson(output, TerminalGraph.Build(records, resolver, terminal.FormId), "fixture.esm");
        using var json = JsonDocument.Parse(output.ToArray());
        var action = json.RootElement.GetProperty("nodes")[0].GetProperty("items")[0].GetProperty("action");
        var jsonVariable = Assert.Single(action.GetProperty("variables").EnumerateArray());
        Assert.Equal(expectedType, jsonVariable.GetProperty("type").GetString());
        Assert.Equal(expectedEvidence, jsonVariable.GetProperty("typeEvidence").GetString());
        Assert.Equal(rawType, jsonVariable.GetProperty("typeRaw").GetByte());
        Assert.Equal(rawType == 0 ? "float" : "int", jsonVariable.GetProperty("storageType").GetString());
        Assert.Equal(rawType, variable.Type);
        Assert.Equal(reference, Assert.Single(terminal.MenuItems[0].ReferencedObjects));
    }
}
