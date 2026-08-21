using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Presentation;
using BethesdaMultitool.Core.Formats.Esm.Presentation.Profiles;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.RecordModel;

/// <summary>
///     The FNV parity gate for the DIAL presentation profile. For every FNV dialog topic, the schema
///     <see cref="DialogTopicProfile" /> (tree + record set) must build the EXACT same
///     <see cref="RecordDetailModel" /> the typed <see cref="RecordDetailBuilders.BuildDialogTopic" /> produces
///     for an ESM load. Both sides pull the "INFO Records" section from the same <c>records.Dialogues</c>;
///     the runtime-only fields (Responses / Journal Index / Dummy Prompt) are 0 / 0 / null on both. Skipped
///     when no FNV plugin is available.
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
public class DialogTopicProfileParityTests
{
    private static string? ResolveFalloutNvEsm()
    {
        var root = Environment.GetEnvironmentVariable("BETHESDA_TEST_DATA_ROOT");
        if (!string.IsNullOrEmpty(root) && File.Exists(Path.Combine(root, "FalloutNV.esm")))
        {
            return Path.Combine(root, "FalloutNV.esm");
        }

        string?[] candidates =
        [
            @"Sample\ESM\pc_final\FalloutNV.esm",
            RealAssetPaths.SteamGameFile("Fallout New Vegas", @"Data\FalloutNV.esm")
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    [Fact]
    public async Task DialogTopicProfile_Reproduces_BuildDialogTopic_For_Fnv()
    {
        var esm = ResolveFalloutNvEsm();
        BucketBTestGuard.SkipUnlessEnabled();
        Assert.SkipUnless(esm is not null,
            "FalloutNV.esm not found (set BETHESDA_TEST_DATA_ROOT or install Fallout: New Vegas).");

        var result = await RealAssetEsmCache.LoadAsync(
            esm!, TestContext.Current.CancellationToken);

        var resolver = new FormIdResolver(result.Records.FormIdToEditorId, result.Records.FormIdToDisplayName);
        var profile = new DialogTopicProfile();
        var topicsByFormId = result.Records.DialogTopics.ToDictionary(t => t.FormId);

        var compared = 0;
        var mismatches = new List<string>();
        foreach (var (formId, tree) in result.Records.DecodedTreesByFormId)
        {
            if (!topicsByFormId.TryGetValue(formId, out var topic))
            {
                continue;
            }

            // Dummy Prompt is an enrichment-derived prompt the tree can't reproduce — strip it from the
            // reference (the profile omits it; FNV keeps BuildDialogTopic for the full display).
            var bare = topic with { DummyPrompt = null };
            var typed = Serialize(RecordDetailBuilders.BuildDialogTopic(bare, result.Records, resolver));
            var profiled = Serialize(profile.Build(
                formId, topic.EditorId, topic.FullName, tree, BethesdaGame.FalloutNewVegas, resolver,
                result.Records));

            compared++;
            if (typed != profiled && mismatches.Count < 5)
            {
                mismatches.Add(
                    $"DIAL 0x{formId:X8} ({topic.EditorId}):\n--- typed ---\n{typed}\n--- profile ---\n{profiled}");
            }
        }

        Assert.True(compared > 50, $"Expected to compare many FNV dialog topics; got {compared}.");
        Assert.True(mismatches.Count == 0,
            $"{mismatches.Count} of {compared} topic models diverged from the typed builder:\n\n" +
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