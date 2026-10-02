using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.AI;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;
using BethesdaMultitool.Core.Formats.Esm.Presentation;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Presentation;

/// <summary>
///     The PACK detail model that <c>show</c> and the GUI record browser render gains a Conditions section:
///     every CTDA in stored order, through the shared condition describer. Before it, a conditioned package
///     showed Identity/Schedule/Location only and its activation conditions were invisible.
///     <para>
///         The fixture is the retail New Vegas package 0x000FE923 AliceHostetlerRunAway, whose CTDA bytes were
///         read from the 2022 Steam FalloutNV.esm, and quest 0x000F2429 VDialogueVegasNorth, whose script declares
///         variable 11 as GhoulDealtWith. Every expected line is written out literally.
///     </para>
/// </summary>
public sealed class PackageConditionPresentationTests
{
    private const uint QuestFormId = 0x000F2429;
    private const uint PackageFormId = 0x000FE923;

    // PACK 0x000FE923 CTDA, verbatim: type 0x00 (==), comparison 2.0f, GetQuestVariable (0x4F), quest, var 11.
    private const string AliceHostetlerCtdaHex =
        "00000000" + "00000040" + "4F000000" + "29240F00" + "0B000000" + "00000000" + "00000000";

    // PACK 0x000FE91C CTDA: the same variable with type 0x60 (>=) and comparison 1.0f.
    private const string AndyLeavePackageCtdaHex =
        "60000000" + "0000803F" + "4F000000" + "29240F00" + "0B000000" + "00000000" + "00000000";

    private static readonly FormIdResolver Resolver = new(
        new Dictionary<uint, string>
        {
            [QuestFormId] = "VDialogueVegasNorth",
            [PackageFormId] = "AliceHostetlerRunAway"
        },
        []);

    [Fact]
    public void TryBuildForRecord_PackageWithQuestVariableCondition_EmitsOrderedConditionsSection()
    {
        var package = Package(Ctda(AliceHostetlerCtdaHex));

        Assert.True(RecordDetailPresenter.TryBuildForRecord(package, FnvRecords(package), Resolver, out var model));

        var items = ConditionItems(Assert.IsType<RecordDetailModel>(model));
        var item = Assert.Single(items);
        Assert.Equal("1", item.Label);
        Assert.Equal(
            "GetQuestVariable(VDialogueVegasNorth [0x000F2429], GhoulDealtWith [var 11]) == 2 [Run On: Subject]",
            item.Value);
        Assert.Equal(QuestFormId, item.LinkedFormId);
    }

    [Fact]
    public void TryBuildForLookup_ShowPath_PrintsTheSameConditionAndNoGroupingForOneCondition()
    {
        var package = Package(Ctda(AliceHostetlerCtdaHex));

        Assert.True(RecordDetailPresenter.TryBuildForLookup(
            FnvRecords(package), Resolver, PackageFormId, null, out var model));

        var section = ConditionsSection(Assert.IsType<RecordDetailModel>(model));
        var list = Assert.Single(section.Entries);
        Assert.Equal(RecordDetailEntryKind.List, list.Kind);
        Assert.Equal(
            "GetQuestVariable(VDialogueVegasNorth [0x000F2429], GhoulDealtWith [var 11]) == 2 [Run On: Subject]",
            Assert.Single(list.Items!).Value);
    }

    [Fact]
    public void ConditionsKeepStoredOrder_TheOperatorByte_AndTheConnectorToTheNextCondition()
    {
        // Condition 1 carries the OR flag (type 0x01), joining it to condition 2; condition 2 is the retail
        // AndyLeavePackage shape (type 0x60 = ">=").
        var first = Ctda(AliceHostetlerCtdaHex) with { Type = 0x01 };
        var package = Package(first, Ctda(AndyLeavePackageCtdaHex));

        Assert.True(RecordDetailPresenter.TryBuildForLookup(
            FnvRecords(package), Resolver, PackageFormId, null, out var model));

        var section = ConditionsSection(Assert.IsType<RecordDetailModel>(model));
        var items = ConditionItems(Assert.IsType<RecordDetailModel>(model));
        Assert.Equal(["1", "2"], items.Select(item => item.Label).ToArray());
        Assert.Equal(
            "GetQuestVariable(VDialogueVegasNorth [0x000F2429], GhoulDealtWith [var 11]) == 2 [Run On: Subject] OR",
            items[0].Value);
        Assert.Equal(
            "GetQuestVariable(VDialogueVegasNorth [0x000F2429], GhoulDealtWith [var 11]) >= 1 [Run On: Subject]",
            items[1].Value);

        var grouping = Assert.Single(section.Entries, entry => entry.Label == "Grouping").Value;
        Assert.NotNull(grouping);
        Assert.StartsWith("1 OR 2 (GECK convention", grouping, StringComparison.Ordinal);
        Assert.Contains("not verified against the engine", grouping, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutACollection_TheGameIsAssumedAndSaid_AndTheVariableStaysAnIndex()
    {
        var package = Package(Ctda(AliceHostetlerCtdaHex));

        Assert.True(RecordDetailPresenter.TryBuildForRecord(package, null, Resolver, out var model));

        var section = ConditionsSection(Assert.IsType<RecordDetailModel>(model));
        Assert.Equal(
            "GetQuestVariable(VDialogueVegasNorth [0x000F2429], var 11) == 2 [Run On: Subject]",
            Assert.Single(ConditionItems(Assert.IsType<RecordDetailModel>(model))).Value);
        var game = Assert.Single(section.Entries, entry => entry.Label == "Game").Value;
        Assert.NotNull(game);
        Assert.StartsWith("FalloutNewVegas assumed", game, StringComparison.Ordinal);
    }

    [Fact]
    public void DetectedGame_AddsNoAssumedGameNote()
    {
        var package = Package(Ctda(AliceHostetlerCtdaHex));

        Assert.True(RecordDetailPresenter.TryBuildForLookup(
            FnvRecords(package), Resolver, PackageFormId, null, out var model));

        Assert.DoesNotContain(
            ConditionsSection(Assert.IsType<RecordDetailModel>(model)).Entries,
            entry => entry.Label == "Game");
    }

    [Fact]
    public void PackageWithoutConditions_HasNoConditionsSection()
    {
        var package = Package();

        var model = RecordDetailBuilders.BuildPackage(package, Resolver);

        Assert.DoesNotContain(model.Sections, section => section.Title == "Conditions");
        Assert.Contains(model.Sections, section => section.Title == "Identity");
    }

    private static PackageRecord Package(params DialogueCondition[] conditions)
    {
        return new PackageRecord
        {
            FormId = PackageFormId,
            EditorId = "AliceHostetlerRunAway",
            Conditions = [.. conditions]
        };
    }

    private static DialogueCondition Ctda(string hex)
    {
        return CtdaParser.Decode(Convert.FromHexString(hex), false);
    }

    private static RecordCollection FnvRecords(PackageRecord package)
    {
        return new RecordCollection
        {
            Game = BethesdaGame.FalloutNewVegas,
            Packages = [package],
            Quests =
            [
                new QuestRecord
                {
                    FormId = QuestFormId,
                    EditorId = "VDialogueVegasNorth",
                    Variables =
                    [
                        new ScriptVariableInfo(10, "NumFiendsDead", 1),
                        new ScriptVariableInfo(11, "GhoulDealtWith", 1),
                        new ScriptVariableInfo(12, "HelpMrsHostetler", 1)
                    ]
                }
            ]
        };
    }

    private static RecordDetailSection ConditionsSection(RecordDetailModel model)
    {
        return Assert.Single(model.Sections, section => section.Title == "Conditions");
    }

    private static IReadOnlyList<RecordDetailListItem> ConditionItems(RecordDetailModel model)
    {
        var list = Assert.Single(ConditionsSection(model).Entries, entry => entry.Kind == RecordDetailEntryKind.List);
        return Assert.IsAssignableFrom<IReadOnlyList<RecordDetailListItem>>(list.Items);
    }
}
