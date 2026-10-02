using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Presentation;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Presentation;

/// <summary>
///     <c>show</c> on the retail New Vegas INFO 0x0015E9CC, which the tool-feedback audit found printing a bare
///     identity panel. Its speaker is 0x0015E9E6 (Horowitz), its quest 0x00159FA0, it is gated on
///     <c>GetIsID Horowitz == 1</c>, and both of its result-script blocks are SCHR-only (SCHR &gt; NEXT and
///     SCHR &gt; ANAM, no SCDA) — the audit's own target_result_scripts.csv lists them with scda_length 0. The
///     correct output for both slots is therefore "no compiled code", not a missing section.
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", TestCategories.BucketB)]
public sealed class InfoRecordDetailRetailTests
{
    private const uint InfoFormId = 0x0015E9CC;

    [Fact]
    public async Task Retail_Info0015E9CC_GetIsIDHorowitz_AndEmptyResultScripts()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var esm = RealAssetPaths.NewVegasBuilds.Steam2022();
        Assert.SkipUnless(esm is not null, RealAssetPaths.SkipMessage("FalloutNV.esm (2022-5-24 Steam build)"));

        // Never disposed: RealAssetEsmCache owns the result and shares it across this collection.
        var result = await RealAssetEsmCache.LoadAsync(esm, TestContext.Current.CancellationToken);
        var records = result.Records;
        var resolver = new FormIdResolver(records.FormIdToEditorId, records.FormIdToDisplayName);

        var info = Assert.Single(records.Dialogues, dialogue => dialogue.FormId == InfoFormId);
        Assert.Equal(0x0015E9E6u, info.SpeakerFormId);
        Assert.Equal(0x00159FA0u, info.QuestFormId);
        Assert.Empty(info.ResultScripts);
        Assert.Equal(2, info.ResultScriptBlocks.Count);

        Assert.True(RecordDetailPresenter.TryBuildForLookup(records, resolver, InfoFormId, null, out var built));
        var model = Assert.IsType<RecordDetailModel>(built);
        Assert.Equal("INFO", model.RecordSignature);

        var relationships = Section(model, "Relationships");
        Assert.EndsWith("[0x0015E9E6]", Value(relationships, "Speaker"), StringComparison.Ordinal);
        Assert.EndsWith("[0x00159FA0]", Value(relationships, "Quest"), StringComparison.Ordinal);

        var conditionList = Assert.Single(Section(model, "Conditions").Entries,
            entry => entry.Kind == RecordDetailEntryKind.List);
        var getIsId = Assert.Single(
            Assert.IsAssignableFrom<IReadOnlyList<RecordDetailListItem>>(conditionList.Items),
            item => item.Value.StartsWith("GetIsID(", StringComparison.Ordinal) &&
                    item.Value.Contains("[0x0015E9E6]) == 1 [Run On: Subject]", StringComparison.Ordinal));
        Assert.Contains("Horowitz", getIsId.Value, StringComparison.Ordinal);

        var resultScripts = Section(model, "Result Scripts");
        Assert.Equal(
            ["Result Script (Begin)", "Result Script (End)"],
            resultScripts.Entries.Select(entry => entry.Label).ToArray());
        Assert.All(resultScripts.Entries, entry =>
            Assert.StartsWith("no compiled code", entry.Value, StringComparison.Ordinal));
    }

    private static RecordDetailSection Section(RecordDetailModel model, string title)
    {
        return Assert.Single(model.Sections, section => section.Title == title);
    }

    private static string? Value(RecordDetailSection section, string label)
    {
        return Assert.Single(section.Entries, entry => entry.Label == label).Value;
    }
}
