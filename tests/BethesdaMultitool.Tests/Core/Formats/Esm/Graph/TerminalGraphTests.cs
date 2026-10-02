using System.Text.Json;
using BethesdaMultitool.Core.Formats.Esm.Graph;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Graph;

public sealed class TerminalGraphTests
{
    [Fact]
    public void CycleAndSharedChild_KeepEveryMenuEdge_VisitEachTerminalOnce()
    {
        var records = Records(Terminal(1, 2, 3), Terminal(2, 3), Terminal(3, 1));
        var graph = Build(records);
        Assert.Equal([1u, 2u, 3u], graph.Nodes.Select(n => n.Terminal.FormId).ToArray());
        Assert.Equal([0, 1, 1], graph.Nodes.Select(n => n.Depth).ToArray());
        Assert.Equal(4, graph.Edges.Count);
        Assert.Equal("AlreadyDiscovered", graph.Edges[^1].Traversal);
        Assert.Equal(1u, graph.Edges[^1].To);
        Assert.Equal([1, 2], graph.Nodes[0].Items.Select(i => i.Index).ToArray());
        Assert.False(graph.Truncated);
    }

    [Theory]
    [InlineData(0, 10, "DepthLimit")]
    [InlineData(10, 1, "NodeLimit")]
    public void Limits_RetainBoundaryEdgesWithoutInspectingTargets(int depth, int count, string expected)
    {
        var graph = Build(Records(Terminal(1, 2), Terminal(2, 3), Terminal(3)), depth, count);
        Assert.Single(graph.Nodes);
        var edge = Assert.Single(graph.Edges);
        Assert.Equal(TerminalTargetStatus.Resolved, edge.TargetStatus);
        Assert.Equal(expected, edge.Traversal);
        Assert.True(graph.Truncated);
    }

    [Theory]
    [InlineData("Deleted")]
    [InlineData("TypeConflict")]
    [InlineData("WrongType")]
    [InlineData("MasterNotLoaded")]
    [InlineData("NotParsed")]
    public void AuthoritativeUnusableIdentity_PreventsTraversingStaleTypedRecord(string statusName)
    {
        var status = Enum.Parse<TerminalTargetStatus>(statusName);
        var records = Records(Terminal(1, 2), Terminal(2, 3));
        var graph = TerminalGraph.Build(records, records.CreateResolver(), 1,
            targetStatus: id => id == 2 ? status : null);
        Assert.Single(graph.Nodes);
        Assert.Equal(status, Assert.Single(graph.Edges).TargetStatus);
        Assert.Equal("Unresolved", graph.Edges[0].Traversal);
        Assert.False(graph.Truncated);
    }

    [Fact]
    public void MissingInPluginAndMissingInPartialCapture_AreDifferentEvidence()
    {
        var records = Records(Terminal(1, 2));
        Assert.Equal(TerminalTargetStatus.Missing, Assert.Single(Build(records).Edges).TargetStatus);
        var capture = TerminalGraph.Build(records, records.CreateResolver(), 1, partialCapture: true);
        Assert.Equal(TerminalTargetStatus.NotPresentInCapture, Assert.Single(capture.Edges).TargetStatus);
        using var output = new MemoryStream();
        TerminalGraphWriter.WriteJson(output, capture, "partial.dmp");
        using var doc = JsonDocument.Parse(output.ToArray());
        Assert.Contains("not evidence of absence", doc.RootElement.GetProperty("absenceNote").GetString());
        using var dot = new StringWriter();
        TerminalGraphWriter.WriteDot(dot, capture, "partial.dmp");
        Assert.Contains("Source: partial.dmp", dot.ToString());
        Assert.Contains("not evidence of absence", dot.ToString());
        Assert.Contains("maxDepth: 32; maxNodes: 1000", dot.ToString());
    }

    [Fact]
    public void RawTerminalWithoutParsedModel_IsNotReportedMissing()
    {
        var records = Records(Terminal(1, 2));
        var graph = TerminalGraph.Build(records, records.CreateResolver(), 1,
            targetStatus: _ => TerminalTargetStatus.Resolved);
        Assert.Equal(TerminalTargetStatus.NotParsed, Assert.Single(graph.Edges).TargetStatus);
        var error = Assert.Throws<InvalidOperationException>(() => TerminalGraph.Build(records, records.CreateResolver(), 2,
            targetStatus: _ => TerminalTargetStatus.Deleted));
        Assert.Contains("Deleted", error.Message);
    }

    [Fact]
    public void Json_PreservesFullConditionsActionBodiesAndSeparateSourceProvenance()
    {
        var source = "; "+new string('x', 600)+"\r\nset bTreasureFound to 1;\r\n";
        const string decompiled = "set bTreasureFound to 1\r\nForceTerminalBack";
        var records = Records(new TerminalRecord { FormId = 1, EditorId = "Root", MenuItems =
        [new TerminalMenuItem { Text = "Yes", ResultText = "Done", SourceText = source, DecompiledText = decompiled,
            CompiledData = [1, 2], IsIncompleteExecutableBundle = true,
            Conditions = [new DialogueCondition { FunctionIndex = 0x35, Parameter1 = 20, Parameter2 = 4,
                Type = 0x20, ComparisonValue = 1 }], Variables = [new(4, "bTreasureFound", 1)],
            ReferencedObjects = [20, 0x80000004] }] });
        using var output = new MemoryStream();
        TerminalGraphWriter.WriteJson(output, Build(records), "test.esm");
        using var doc = JsonDocument.Parse(output.ToArray());
        var item = doc.RootElement.GetProperty("nodes")[0].GetProperty("items")[0];
        Assert.Equal("GetScriptVariable", item.GetProperty("conditions")[0].GetProperty("function").GetString());
        var action = item.GetProperty("action");
        Assert.Equal(source, action.GetProperty("sourceText").GetString());
        Assert.Equal(decompiled, action.GetProperty("decompiledText").GetString());
        Assert.Equal("plugin-record", action.GetProperty("sourceProvenance").GetString());
        Assert.Contains("Reconstruction (SCDA)", action.GetProperty("decompiledLabel").GetString());
        Assert.Equal(JsonValueKind.Null, action.GetProperty("references")[1].GetProperty("formId").ValueKind);
        Assert.True(action.GetProperty("incompleteExecutableBundle").GetBoolean());
        Assert.Contains("not establish in-game reachability", doc.RootElement.GetProperty("scope").GetString());
        Assert.Equal("file-local", doc.RootElement.GetProperty("structuredFormIdNamespace").GetString());
        Assert.Contains("not rewritten", action.GetProperty("textFormIdNamespace").GetString());
    }

    [Fact]
    public void Dot_EscapesLabelsAndBackslashes_AndKeepsActionsAndCycleEdges()
    {
        var records = Records(new TerminalRecord { FormId = 1, EditorId = "quote\";\\n injected",
            MenuItems = [new() { Text = "first\r\nsecond", SubTerminal = 1,
                SourceText = "set bTreasureFound to 1;", DecompiledText = "reconstructed" }] });
        using var output = new StringWriter();
        TerminalGraphWriter.WriteDot(output, Build(records));
        var dot = output.ToString();
        Assert.StartsWith("digraph terminals {", dot);
        Assert.Contains("quote\\\";\\\\n injected", dot);
        Assert.Contains("first\\nsecond", dot);
        Assert.Contains("set bTreasureFound to 1;", dot);
        Assert.Contains("Reconstruction (SCDA)", dot);
        Assert.Contains("n00000001 -> n00000001", dot);
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(1025, 1)]
    [InlineData(1, 0)]
    [InlineData(1, 10001)]
    public void InvalidBounds_AreRejected(int depth, int count) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Build(Records(Terminal(1)), depth, count));

    private static TerminalGraphResult Build(RecordCollection records, int depth = 32, int count = 1000) =>
        TerminalGraph.Build(records, records.CreateResolver(), 1, depth, count);
    private static RecordCollection Records(params TerminalRecord[] terminals) => new()
        { Game = BethesdaGame.FalloutNewVegas, Terminals = [.. terminals] };
    private static TerminalRecord Terminal(uint id, params uint[] links) => new()
        { FormId = id, EditorId = $"Terminal{id}", MenuItems = links.Select(id => new TerminalMenuItem { Text = "Open", SubTerminal = id }).ToList() };
}
