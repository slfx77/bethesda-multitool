using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Presentation;
using BethesdaMultitool.Core.Formats.Esm.Presentation.Profiles;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.RecordModel;

/// <summary>
///     The FNV parity gate for the QUST presentation profile. For every FNV quest, the schema
///     <see cref="QuestProfile" /> (reading the DecodedTree) must build the EXACT same
///     <see cref="RecordDetailModel" /> the typed <see cref="RecordDetailBuilders.BuildQuest" /> produces —
///     for the tree-derivable sections (Identity / Objectives / Stages). BuildQuest's "Variables" and
///     "Related NPCs" come from cross-record enrichment (the linked SCPT's locals; a dialogue-speaker
///     reverse-lookup), not the QUST subrecords, so they are stripped from the reference here — the profile
///     legitimately omits them (FNV keeps BuildQuest for the full display). Skipped when no FNV plugin is
///     available.
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
public class QuestProfileParityTests
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
    public async Task QuestProfile_Reproduces_BuildQuest_TreeSections_For_Fnv()
    {
        var esm = ResolveFalloutNvEsm();
        BucketBTestGuard.SkipUnlessEnabled();
        Assert.SkipUnless(esm is not null,
            "FalloutNV.esm not found (set BETHESDA_TEST_DATA_ROOT or install Fallout: New Vegas).");

        var result = await RealAssetEsmCache.LoadAsync(
            esm!, TestContext.Current.CancellationToken);

        var resolver = new FormIdResolver(result.Records.FormIdToEditorId, result.Records.FormIdToDisplayName);
        var profile = new QuestProfile();
        var questsByFormId = result.Records.Quests.ToDictionary(q => q.FormId);

        var compared = 0;
        var mismatches = new List<string>();
        foreach (var (formId, tree) in result.Records.DecodedTreesByFormId)
        {
            if (!questsByFormId.TryGetValue(formId, out var quest))
            {
                continue;
            }

            // Strip the cross-record enrichment the profile can't (and shouldn't) reproduce from the tree.
            var bare = quest with { Variables = [], RelatedNpcFormIds = [] };
            var typed = Serialize(RecordDetailBuilders.BuildQuest(bare, resolver));
            var profiled = Serialize(profile.Build(
                formId, quest.EditorId, quest.FullName, tree, BethesdaGame.FalloutNewVegas, resolver, result.Records));

            compared++;
            if (typed != profiled && mismatches.Count < 5)
            {
                mismatches.Add(
                    $"QUST 0x{formId:X8} ({quest.EditorId}):\n--- typed ---\n{typed}\n--- profile ---\n{profiled}");
            }
        }

        Assert.True(compared > 50, $"Expected to compare many FNV quests; got {compared}.");
        Assert.True(mismatches.Count == 0,
            $"{mismatches.Count} of {compared} quest models diverged from the typed builder:\n\n" +
            string.Join("\n\n", mismatches));
    }

    /// <summary>Deterministic, order-preserving serialization of the detail model for equality.</summary>
    private static string Serialize(RecordDetailModel model)
    {
        var sb = new StringBuilder();
        foreach (var section in model.Sections)
        {
            sb.Append("§ ").Append(section.Title).Append('\n');
            foreach (var entry in section.Entries)
            {
                sb.Append("  ").Append(entry.Kind).Append('|').Append(entry.Label).Append('|')
                    .Append(entry.Value ?? "").Append('|').Append(entry.LinkedFormId?.ToString("X8") ?? "")
                    .Append('|').Append(entry.ExpandByDefault).Append('\n');
                if (entry.Items is null)
                {
                    continue;
                }

                foreach (var item in entry.Items)
                {
                    sb.Append("    - ").Append(item.Label).Append('|').Append(item.Value)
                        .Append('|').Append(item.LinkedFormId?.ToString("X8") ?? "").Append('\n');
                }
            }
        }

        return sb.ToString();
    }
}