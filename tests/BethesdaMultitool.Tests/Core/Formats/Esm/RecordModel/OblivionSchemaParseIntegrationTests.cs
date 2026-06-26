using BethesdaMultitool.Core.Semantic;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.RecordModel;

/// <summary>
///     End-to-end verification that an Oblivion (TES4) plugin is read correctly through the real loader:
///     the game is detected, dispatched to the schema-driven parser, and every NPC_ comes back as a
///     <c>GenericEsmRecord</c> carrying a decoded field tree with the rich blocks (Configuration / Stats).
///     This is the "reads properly in the GUI" guarantee at the data layer — the GUI consumes the same
///     <c>RecordCollection</c>. Skipped when Oblivion.esm isn't installed.
/// </summary>
public class OblivionSchemaParseIntegrationTests
{
    private static string? ResolveOblivionEsm()
    {
        var root = Environment.GetEnvironmentVariable("BETHESDA_TEST_DATA_ROOT");
        if (!string.IsNullOrEmpty(root) && File.Exists(Path.Combine(root, "Oblivion.esm")))
        {
            return Path.Combine(root, "Oblivion.esm");
        }

        const string steam = @"E:\SteamLibrary\SteamApps\common\Oblivion\Data\Oblivion.esm";
        return File.Exists(steam) ? steam : null;
    }

    [Fact]
    public async Task Oblivion_Npcs_Are_SchemaDecoded_With_Rich_Blocks()
    {
        var esm = ResolveOblivionEsm();
        Assert.SkipUnless(esm is not null,
            "Oblivion.esm not found (set BETHESDA_TEST_DATA_ROOT or install Oblivion).");

        using var result = await SemanticFileLoader.LoadAsync(
            esm!, cancellationToken: TestContext.Current.CancellationToken);

        var npcs = result.Records.GenericRecords.Where(r => r.RecordType == "NPC_").ToList();
        Assert.True(npcs.Count > 1000,
            $"Expected the schema-driven parser to surface NPC_ records as GenericRecords; got {npcs.Count}. " +
            "If 0, the game was not detected as Oblivion or the schema dispatch did not fire.");

        var withTree = npcs.Where(n => n.DecodedTree is { Count: > 0 }).ToList();
        Assert.True(withTree.Count > 1000, $"Expected decoded field trees on NPC_ records; got {withTree.Count}.");

        // A representative NPC_ must decode the rich, labeled blocks the GUI presents.
        var sample = withTree.First(n => n.DecodedTree!.Any(node => node.Label == "Stats"));
        Assert.Contains(sample.DecodedTree!, n => n.Label == "Configuration");
        Assert.False(string.IsNullOrEmpty(sample.EditorId), "NPC_ should have an EditorID.");

        var stats = sample.DecodedTree!.First(n => n.Label == "Stats");
        Assert.Contains(stats.Children, c => c.Label == "Strength");
        Assert.Contains(stats.Children, c => c.Label == "Luck");
        Assert.Contains(stats.Children, c => c.Label == "Health");
    }
}
