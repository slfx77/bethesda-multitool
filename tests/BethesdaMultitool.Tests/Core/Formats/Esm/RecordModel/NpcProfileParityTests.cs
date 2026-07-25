using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Presentation;
using BethesdaMultitool.Core.Formats.Esm.Presentation.Profiles;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.RecordModel;

/// <summary>
///     The FNV parity gate for the unified, profile-driven record display. For every FNV NPC, the schema
///     <see cref="NpcProfile" /> (reading the DecodedTree) must build the EXACT same
///     <see cref="RecordDetailModel" /> the hand-written typed <see cref="RecordDetailBuilders.BuildNpc" />
///     produces (reading the typed NpcRecord) — all nine sections, every label/value/link. This proves the
///     engine reproduces FNV's curated display before it is rolled out to the other games (and, later,
///     before FNV's live display is switched to it). Skipped when no FNV plugin is available.
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
public class NpcProfileParityTests
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
    public async Task NpcProfile_Reproduces_BuildNpc_Exactly_For_Fnv()
    {
        var esm = ResolveFalloutNvEsm();
        BucketBTestGuard.SkipUnlessEnabled();
        Assert.SkipUnless(esm is not null,
            "FalloutNV.esm not found (set BETHESDA_TEST_DATA_ROOT or install Fallout: New Vegas).");

        var result = await RealAssetEsmCache.LoadAsync(
            esm!, TestContext.Current.CancellationToken);

        // One resolver instance for both sides — parity is about the profile reproducing the builder, not
        // about resolver content, so identical resolver use cancels out.
        var resolver = new FormIdResolver(result.Records.FormIdToEditorId, result.Records.FormIdToDisplayName);
        var profile = new NpcProfile();
        var npcsByFormId = result.Records.Npcs.ToDictionary(n => n.FormId);

        var compared = 0;
        var mismatches = new List<string>();
        foreach (var (formId, tree) in result.Records.DecodedTreesByFormId)
        {
            if (!npcsByFormId.TryGetValue(formId, out var npc))
            {
                continue;
            }

            var typed = Serialize(RecordDetailBuilders.BuildNpc(npc, resolver));
            var profiled = Serialize(profile.Build(
                formId, npc.EditorId, npc.FullName, tree, BethesdaGame.FalloutNewVegas, resolver, result.Records));

            compared++;
            if (typed != profiled && mismatches.Count < 5)
            {
                mismatches.Add(
                    $"NPC 0x{formId:X8} ({npc.EditorId}):\n--- typed ---\n{typed}\n--- profile ---\n{profiled}");
            }
        }

        Assert.True(compared > 1000, $"Expected to compare many FNV NPCs; got {compared}.");
        Assert.True(mismatches.Count == 0,
            $"{mismatches.Count} of {compared} NPC models diverged from the typed builder:\n\n" +
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