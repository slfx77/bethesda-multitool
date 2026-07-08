using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.RecordModel;

/// <summary>
///     Verifies the typed-primary schema enrichment for FNV: registering FalloutNvSchema must NOT disturb
///     the rich hand-written typed path (NPCs still parse into typed <c>NpcRecord</c>s), and the schema now
///     <em>additionally</em> decodes the profiled record types (NPC_) into a parallel DecodedTree map keyed
///     by FormID. That map is the common substrate the unified, profile-driven record presentation reads —
///     it must carry the same rich, labeled blocks the other games' GenericRecords do. Skipped when no FNV
///     plugin is available.
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
public class FalloutNvSchemaEnrichmentTests
{
    private static string? ResolveFalloutNvEsm()
    {
        var root = Environment.GetEnvironmentVariable("BETHESDA_TEST_DATA_ROOT");
        if (!string.IsNullOrEmpty(root) && File.Exists(Path.Combine(root, "FalloutNV.esm")))
        {
            return Path.Combine(root, "FalloutNV.esm");
        }

        string[] candidates =
        [
            @"Sample\ESM\pc_final\FalloutNV.esm",
            @"E:\SteamLibrary\SteamApps\common\Fallout New Vegas\Data\FalloutNV.esm"
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    [Fact]
    public async Task Fnv_KeepsTypedNpcs_AndGainsDecodedTreeSubstrate()
    {
        var esm = ResolveFalloutNvEsm();
        BucketBTestGuard.SkipUnlessEnabled();
        Assert.SkipUnless(esm is not null,
            "FalloutNV.esm not found (set BETHESDA_TEST_DATA_ROOT or install Fallout: New Vegas).");

        var result = await RealAssetEsmCache.LoadAsync(
            esm!, cancellationToken: TestContext.Current.CancellationToken);

        // 1. No regression: FNV still reads through its rich typed handlers (the schema did NOT take over).
        Assert.True(result.Records.Npcs.Count > 1000,
            $"Expected FNV NPCs to still parse into typed NpcRecords; got {result.Records.Npcs.Count}. " +
            "If 0, the schema bridge wrongly became the base (IsSchemaPrimary gate failed).");

        // 2. Enrichment: every NPC_ also has a schema DecodedTree in the parallel map.
        var trees = result.Records.DecodedTreesByFormId;
        Assert.True(trees.Count > 1000,
            $"Expected NPC_ DecodedTrees in the enrichment map; got {trees.Count}.");

        // 3. The trees key on the typed NPCs' FormIDs (same records, two representations).
        var npcFormIds = result.Records.Npcs.Select(n => n.FormId).ToHashSet();
        var sampleFormId = trees.Keys.First(npcFormIds.Contains);
        var tree = trees[sampleFormId];
        Assert.NotEmpty(tree);

        // 4. The substrate carries the rich, labeled blocks the unified presenter will consume.
        var withConfig = trees.Values.First(t => t.Any(n => n.Label == "Configuration"));
        Assert.Contains(withConfig, n => n.Label == "Configuration"); // ACBS
        Assert.Contains(withConfig, n => n.Label is "Race" or "Class");
    }
}
