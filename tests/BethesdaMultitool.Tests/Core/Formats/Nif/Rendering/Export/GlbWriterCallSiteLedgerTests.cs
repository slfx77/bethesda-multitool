using System.Text.RegularExpressions;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Export;

/// <summary>
///     Every production call of <c>GlbWriter.Write</c> or <c>GlbWriter.WriteToBytes</c> is on this ledger with a
///     disposition. A new caller fails until it is classified, and a removed or moved one fails until its row is
///     updated, so "retiring" direct writer calls (in-scope call sites stop calling the writer directly) stays
///     measurable.
/// </summary>
/// <remarks>
///     In this round no call site has been routed: the only <see cref="GlbWriterCallSiteDisposition.Router" /> row is
///     the router's own native route, and every production entry point is still a direct caller.
/// </remarks>
public sealed class GlbWriterCallSiteLedgerTests
{
    private static readonly Regex CallPattern =
        new(@"\bGlbWriter\.(?:Write|WriteToBytes)\s*\(", RegexOptions.CultureInvariant);

    private static readonly (string Path, int Calls, GlbWriterCallSiteDisposition Disposition, string Reason)[]
        Ledger =
        [
            ("src/BethesdaMultitool/Core/Formats/Nif/Rendering/Export/NifGlbExport.cs", 2,
                GlbWriterCallSiteDisposition.Router,
                "NifGlbExport's native route; GlbWriter remains the decline route and the parity oracle."),
            ("src/BethesdaMultitool/CLI/Rendering/Nif/NifExportPipeline.cs", 1,
                GlbWriterCallSiteDisposition.ToBeRouted,
                "Call site A, CLI export nif: moves to NifGlbExport when NifGlbExportCallSite.CliExportNif flips."),
            ("src/BethesdaMultitool/Core/Formats/Nif/Rendering/NifBrowserService.cs", 1,
                GlbWriterCallSiteDisposition.ToBeRouted,
                "ExportViewerSceneToGlb serves both the GUI NIF viewer export (call site B, static scenes, to be " +
                "routed) and the WebView compatibility preview (call site C, pinned native); the two must be split " +
                "before B routes."),
            ("src/BethesdaMultitool/CLI/Rendering/Npc/NpcExportPipeline.cs", 2,
                GlbWriterCallSiteDisposition.Later,
                "NPC export: skinned FaceGen scenes are outside the static normalized scope."),
            ("src/BethesdaMultitool/CLI/Commands/Export/ExportCreatureCommand.cs", 1,
                GlbWriterCallSiteDisposition.Later,
                "Creature export: skinned scenes are outside the static normalized scope."),
            ("src/BethesdaMultitool/Core/Formats/Nif/Rendering/Npc/NpcBrowserService.cs", 2,
                GlbWriterCallSiteDisposition.Later,
                "GUI NPC viewer and batch export: skinned scenes are outside the static normalized scope."),
            ("src/BethesdaMultitool/Core/Formats/Esm/Export/ModelExport/MeshGlbExporter.cs", 1,
                GlbWriterCallSiteDisposition.Later,
                "Runtime meshes extracted from memory dumps, not NIF sources; not part of M2.1."),
            ("src/BethesdaMultitool/Core/Formats/Esm/Export/ModelExport/TerrainGlbExporter.cs", 1,
                GlbWriterCallSiteDisposition.Later,
                "Runtime terrain grids, not NIF sources; not part of M2.1.")
        ];

    /// <summary>The source scan and the ledger agree on every calling file and its call count.</summary>
    [Fact]
    public void EveryProductionCallerIsOnTheLedgerWithItsCallCount()
    {
        var found = ScanCallers();
        var expected = Ledger.ToDictionary(row => row.Path, row => row.Calls, StringComparer.Ordinal);

        var unclassified = found.Keys.Where(path => !expected.ContainsKey(path)).Order(StringComparer.Ordinal)
            .Select(path => $"{path} ({found[path]})").ToArray();
        var stale = expected.Keys.Where(path => !found.ContainsKey(path)).Order(StringComparer.Ordinal).ToArray();
        var miscounted = expected.Keys.Where(path => found.TryGetValue(path, out var calls) && calls != expected[path])
            .Order(StringComparer.Ordinal).Select(path => $"{path} (ledger {expected[path]}, source {found[path]})")
            .ToArray();

        Assert.True(unclassified.Length == 0,
            "Classify each new GlbWriter caller on the ledger: " + string.Join("; ", unclassified));
        Assert.True(stale.Length == 0, "Remove or move ledger rows whose file no longer calls GlbWriter: " +
                                       string.Join("; ", stale));
        Assert.True(miscounted.Length == 0, "Update the ledger's call counts: " + string.Join("; ", miscounted));
    }

    /// <summary>Ledger rows are unique, explained and positive, and only the router carries the Router disposition.</summary>
    [Fact]
    public void LedgerRowsAreUniqueExplainedAndOnlyTheRouterIsARouter()
    {
        Assert.Equal(Ledger.Length, Ledger.Select(row => row.Path).Distinct(StringComparer.Ordinal).Count());
        foreach (var row in Ledger)
        {
            Assert.False(string.IsNullOrWhiteSpace(row.Reason), row.Path);
            Assert.True(row.Calls > 0, row.Path);
            Assert.True(Enum.IsDefined(row.Disposition), row.Path);
        }

        var routers = Ledger.Where(row => row.Disposition == GlbWriterCallSiteDisposition.Router)
            .Select(row => row.Path).ToArray();
        Assert.Equal(new[] { "src/BethesdaMultitool/Core/Formats/Nif/Rendering/Export/NifGlbExport.cs" }, routers);
    }

    /// <summary>The scanner must see a known call and ignore commented and look-alike text, or the ledger proves nothing.</summary>
    [Fact]
    public void TheScannerCountsCallsAndIgnoresCommentsAndLookAlikes()
    {
        Assert.Equal(2, CountCalls(
        [
            "        GlbWriter.Write(scene, textureResolver, outputPath);",
            "        return GlbWriter.WriteToBytes (scene, resolver);",
            "        // GlbWriter.Write(scene, textureResolver, outputPath);",
            "        /// <see cref=\"GlbWriter.Write\" />",
            "        NpcGlbWriter.Write(scene);",
            "        GlbWriter.WriteSomethingElse(scene);"
        ]));
    }

    /// <summary>Counts writer calls per source file under src, skipping build output.</summary>
    private static Dictionary<string, int> ScanCallers()
    {
        var callers = new Dictionary<string, int>(StringComparer.Ordinal);
        var root = Path.Combine(SourceContract.RepoRoot, "src");
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(SourceContract.RepoRoot, file).Replace('\\', '/');
            if (relative.Contains("/bin/", StringComparison.Ordinal) ||
                relative.Contains("/obj/", StringComparison.Ordinal))
            {
                continue;
            }

            var calls = CountCalls(File.ReadLines(file));
            if (calls > 0)
            {
                callers[relative] = calls;
            }
        }

        return callers;
    }

    /// <summary>Counts writer calls on non-comment lines.</summary>
    private static int CountCalls(IEnumerable<string> lines)
    {
        var calls = 0;
        foreach (var line in lines)
        {
            if (line.TrimStart().StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            calls += CallPattern.Matches(line).Count;
        }

        return calls;
    }
}
