using System.Buffers.Binary;
using System.Text.Json;
using BethesdaMultitool.Tests.Helpers;
using Xunit;
using static BethesdaMultitool.Tests.Helpers.EsmTestRecordBuilder;

namespace BethesdaMultitool.Tests.CLI;

public sealed class TerminalGraphExeTests
{
    [Fact]
    public async Task RealExecutable_ImplicitJsonAndDotAreClean_AndPreserveFullStoredAction()
    {
        using var directory = CliExeRunner.CreateTempDirectory();
        var path = Path.Combine(directory.Path, "FalloutNV.esm");
        var source = "; " + new string('a', 700) + "\r\nset bTreasureFound to 1;\r\n";
        File.WriteAllBytes(path, new EsmTestFileBuilder().AddTopLevelGrup("TERM",
            Terminal(0x1000, "RootTerminal", 0x1001, source), Terminal(0x1001, "ChildTerminal", 0x1000)).Build());
        var cancellation = TestContext.Current.CancellationToken;
        var result = await CliExeRunner.RunAsync(["esm", "terminal-graph", path, "RootTerminal"], cancellation);
        Assert.True(result.ExitCode == 0, result.Describe());
        using var doc = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal(2, doc.RootElement.GetProperty("nodes").GetArrayLength());
        Assert.Equal(2, doc.RootElement.GetProperty("edges").GetArrayLength());
        Assert.Equal(source, doc.RootElement.GetProperty("nodes")[0].GetProperty("items")[0]
            .GetProperty("action").GetProperty("sourceText").GetString());
        var dot = await CliExeRunner.RunAsync(["esm", "terminal-graph", path, "0x1000", "--format", "dot", "--max-depth", "0"], cancellation);
        Assert.True(dot.ExitCode == 0, dot.Describe());
        Assert.StartsWith("digraph terminals {", dot.StandardOutput);
        Assert.EndsWith("}", dot.StandardOutput.TrimEnd());
        Assert.Contains("DepthLimit", dot.StandardOutput);
        Assert.Contains("truncated: True", dot.StandardOutput);
        Assert.Contains("Source:", dot.StandardOutput);
        Assert.Contains("set bTreasureFound to 1;", dot.StandardOutput);
    }

    [Fact]
    public async Task LoadOrder_MasterTargetResolves_DeletedOverrideIsRetainedAsUnresolvedEdge()
    {
        using var directory = CliExeRunner.CreateTempDirectory();
        var master = Path.Combine(directory.Path, "FalloutNV.esm");
        var dlc = Path.Combine(directory.Path, "DeadMoney.esm");
        File.WriteAllBytes(master, new EsmTestFileBuilder().AddTopLevelGrup("TERM", Terminal(0x1000, "BaseTerminal")).Build());
        var root = Terminal(0x01001000, "RootTerminal", 0x1000);
        File.WriteAllBytes(dlc, new EsmTestFileBuilder().WithMasters("FalloutNV.esm").AddTopLevelGrup("TERM", root).Build());
        var cancellation = TestContext.Current.CancellationToken;
        var missing = await CliExeRunner.RunAsync(["esm", "terminal-graph", dlc, "RootTerminal"], cancellation);
        Assert.True(missing.ExitCode == 0, missing.Describe());
        using (var missingDoc = JsonDocument.Parse(missing.StandardOutput))
            Assert.Equal("MasterNotLoaded", missingDoc.RootElement.GetProperty("edges")[0].GetProperty("targetStatus").GetString());
        var resolved = await CliExeRunner.RunAsync(["esm", "terminal-graph", dlc, "DeadMoney.esm:0x01001000", "--load-order", master], cancellation);
        Assert.True(resolved.ExitCode == 0, resolved.Describe());
        using (var resolvedDoc = JsonDocument.Parse(resolved.StandardOutput))
        {
            Assert.Equal(2, resolvedDoc.RootElement.GetProperty("nodes").GetArrayLength());
            Assert.Equal("FalloutNV.esm", resolvedDoc.RootElement.GetProperty("nodes")[1].GetProperty("recordProvenance").GetProperty("winner").GetString());
            Assert.Equal("load-order", resolvedDoc.RootElement.GetProperty("structuredFormIdNamespace").GetString());
            Assert.Contains("not rewritten", resolvedDoc.RootElement.GetProperty("textNamespaceNote").GetString());
        }
        var deleted = Terminal(0x1000, "BaseTerminal");
        BinaryPrimitives.WriteUInt32LittleEndian(deleted.AsSpan(8), 0x20);
        File.WriteAllBytes(dlc, new EsmTestFileBuilder().WithMasters("FalloutNV.esm").AddTopLevelGrup("TERM", root, deleted).Build());
        var overridden = await CliExeRunner.RunAsync(["esm", "terminal-graph", dlc, "RootTerminal", "--load-order", master], cancellation);
        Assert.True(overridden.ExitCode == 0, overridden.Describe());
        using var deletedDoc = JsonDocument.Parse(overridden.StandardOutput);
        Assert.Equal(1, deletedDoc.RootElement.GetProperty("nodes").GetArrayLength());
        var edge = deletedDoc.RootElement.GetProperty("edges")[0];
        Assert.Equal("Deleted", edge.GetProperty("targetStatus").GetString());
        Assert.True(edge.GetProperty("recordProvenance").GetProperty("deletedByWinner").GetBoolean());
    }

    private static byte[] Terminal(uint id, string editorId, uint? child = null, string? source = null)
    {
        var parts = new List<(string, byte[])> { ("EDID", NullTermString(editorId)) };
        if (child is { } target)
        {
            var bytes = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, target);
            parts.Add(("ITXT", NullTermString("Open")));
            parts.Add(("TNAM", bytes));
            if (source is not null) parts.Add(("SCTX", NullTermString(source)));
        }
        return BuildRecordBytes(id, "TERM", false, parts.ToArray());
    }
}
