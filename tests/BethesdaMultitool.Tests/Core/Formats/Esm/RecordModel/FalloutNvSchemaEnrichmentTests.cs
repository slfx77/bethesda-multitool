using BethesdaMultitool.Core.Formats.Esm.RecordModel;
using BethesdaMultitool.Core.Formats.Esm.RecordModel.Schema;
using BethesdaMultitool.Core.Games;
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
    [Fact]
    public void FnvCobjSchema_IsBaseObjectShape_NotModernRecipeShape()
    {
        var definitions = EsmSchemas.IndexForGame(BethesdaGame.FalloutNewVegas);
        Assert.NotNull(definitions);
        Assert.True(definitions.TryGetValue("COBJ", out var cobj));

        var signatures = EnumerateSignatures(cobj!.Members).ToHashSet(StringComparer.Ordinal);

        Assert.Contains("EDID", signatures);
        Assert.Contains("OBND", signatures);
        Assert.Contains("FULL", signatures);
        Assert.Contains("MODL", signatures);
        Assert.Contains("SCRI", signatures);
        Assert.Contains("YNAM", signatures);
        Assert.Contains("ZNAM", signatures);
        Assert.Contains("DATA", signatures);

        foreach (var modernRecipeSignature in
                 new[] { "COCT", "CNTO", "CTDA", "CIS1", "CIS2", "CNAM", "BNAM", "NAM1" })
        {
            Assert.DoesNotContain(modernRecipeSignature, signatures);
        }
    }

    [Fact]
    public void FnvIngredientSchema_SeparatesWeightEffectDataAndEffectGroup()
    {
        var definitions = EsmSchemas.IndexForGame(BethesdaGame.FalloutNewVegas);
        Assert.NotNull(definitions);
        Assert.True(definitions.TryGetValue("INGR", out var ingredient));

        var data = Assert.IsType<FieldDef>(
            Assert.Single(ingredient!.Members, member => member.Signature == "DATA"));
        Assert.Equal(PrimType.Float, data.Type);

        var enit = Assert.IsType<StructDef>(
            Assert.Single(ingredient.Members, member => member.Signature == "ENIT"));
        Assert.Collection(
            enit.Members,
            member => Assert.Equal(PrimType.S32, Assert.IsType<FieldDef>(member).Type),
            member => Assert.Equal(PrimType.U8, Assert.IsType<FieldDef>(member).Type),
            member => Assert.Equal(3, Assert.IsType<UnusedDef>(member).Size));

        var effects = Assert.IsType<ArrayDef>(
            Assert.Single(ingredient.Members, member => member.Name == "Effects"));
        var effectSignatures = EnumerateSignatures([effects]).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("EFID", effectSignatures);
        Assert.Contains("EFIT", effectSignatures);
        Assert.Contains("CTDA", effectSignatures);
    }

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
            esm!, TestContext.Current.CancellationToken);

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

    private static IEnumerable<string> EnumerateSignatures(IEnumerable<MemberDef> members)
    {
        foreach (var member in members)
        {
            if (member.Signature is { } signature)
            {
                yield return signature;
            }

            IEnumerable<MemberDef>? children = member switch
            {
                StructDef structure => structure.Members,
                ArrayDef array => [array.Element],
                UnionDef union => union.Variants,
                _ => null
            };

            if (children is null)
            {
                continue;
            }

            foreach (var childSignature in EnumerateSignatures(children))
            {
                yield return childSignature;
            }
        }
    }
}