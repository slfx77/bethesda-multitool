using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Presentation;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Presentation;

/// <summary>
///     The retail New Vegas packages the tool-feedback audit named: <c>show</c> on 0x000FE923
///     AliceHostetlerRunAway printed Identity/Schedule/Location only, while its CTDA gates the package on
///     <c>GetQuestVariable VDialogueVegasNorth.GhoulDealtWith == 2</c>. The expected lines were read off the audited
///     2022 Steam FalloutNV.esm (CTDA <c>00000000 00000040 4F000000 29240F00 0B000000 00000000 00000000</c>;
///     quest 0x000F2429's script declares variable 11 as GhoulDealtWith) and its sibling 0x000FE91C
///     AndyLeavePackage (type byte 0x60, comparison 1.0).
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", TestCategories.BucketB)]
public sealed class PackageConditionsRetailTests
{
    [Fact]
    public async Task FnvRetail_AliceHostetlerPackages_ShowQuestVariableConditions()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var esm = RealAssetPaths.NewVegasBuilds.Steam2022();
        Assert.SkipUnless(esm is not null, RealAssetPaths.SkipMessage("FalloutNV.esm (2022-5-24 Steam build)"));

        // Never disposed: RealAssetEsmCache owns the result and shares it across this collection.
        var result = await RealAssetEsmCache.LoadAsync(esm, TestContext.Current.CancellationToken);
        var records = result.Records;
        var resolver = new FormIdResolver(records.FormIdToEditorId, records.FormIdToDisplayName);

        var alice = Build(records, resolver, 0x000FE923);
        Assert.Equal("AliceHostetlerRunAway", alice.EditorId);
        Assert.Equal(
            "GetQuestVariable(VDialogueVegasNorth [0x000F2429], GhoulDealtWith [var 11]) == 2 [Run On: Subject]",
            Assert.Single(ConditionValues(alice)));

        var andy = Build(records, resolver, 0x000FE91C);
        Assert.Equal("AndyLeavePackage", andy.EditorId);
        Assert.Contains(
            ConditionValues(andy),
            line => line.StartsWith(
                "GetQuestVariable(VDialogueVegasNorth [0x000F2429], GhoulDealtWith [var 11]) >= 1 [Run On: Subject]",
                StringComparison.Ordinal));
    }

    private static RecordDetailModel Build(
        RecordCollection records,
        FormIdResolver resolver,
        uint formId)
    {
        Assert.True(RecordDetailPresenter.TryBuildForLookup(records, resolver, formId, null, out var model));
        var built = Assert.IsType<RecordDetailModel>(model);
        Assert.Equal("PACK", built.RecordSignature);
        return built;
    }

    private static IReadOnlyList<string> ConditionValues(RecordDetailModel model)
    {
        var section = Assert.Single(model.Sections, candidate => candidate.Title == "Conditions");
        var list = Assert.Single(section.Entries, entry => entry.Kind == RecordDetailEntryKind.List);
        return Assert.IsAssignableFrom<IReadOnlyList<RecordDetailListItem>>(list.Items)
            .Select(item => item.Value)
            .ToArray();
    }
}
