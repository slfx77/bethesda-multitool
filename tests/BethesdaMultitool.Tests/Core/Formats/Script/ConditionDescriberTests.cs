using System.Globalization;
using System.Text.Json;
using BethesdaMultitool.Core.EsmView;
using BethesdaMultitool.Core.Formats.Esm.Analysis.Helpers;
using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Character;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;
using BethesdaMultitool.Core.Formats.Esm.Script.Conditions;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Core.Semantic;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Script;

/// <summary>
///     The shared CTDA describer behind CLI, report and JSON condition output. Every expected value is an
///     independent literal: the FNV retail package conditions (PACK 0x000FE923 AliceHostetlerRunAway and its
///     sibling 0x000FE91C AndyLeavePackage) were read from the 2022 Steam FalloutNV.esm, and quest
///     0x000F2429 VDialogueVegasNorth's script declares 10 NumFiendsDead, 11 GhoulDealtWith, 12 HelpMrsHostetler.
/// </summary>
public sealed class ConditionDescriberTests
{
    [Theory]
    [InlineData(BethesdaGame.FalloutNewVegas, false, 0x18)]
    [InlineData(BethesdaGame.Fallout3, false, 0x18)]
    [InlineData(BethesdaGame.Oblivion, false, 0x18)]
    [InlineData(BethesdaGame.Skyrim, true, 0)]
    public void TypeFlags_AreNamedOnlyForGamesThatDefineThem(BethesdaGame game, bool modern, int unknown)
    {
        var condition = new DialogueCondition { Type = 0x98, FunctionIndex = 0x002E };
        var description = ConditionDescriber.Describe(condition,
            ConditionDisplayContext.ForResolver(FormIdResolver.Empty, game));
        Assert.Equal(modern, description.SwapSubjectTarget);
        Assert.Equal(modern, description.UsePackData);
        Assert.Equal(unknown, description.UnknownTypeBits);
        var text = ConditionTextFormatter.FormatLine(description);
        Assert.Equal(modern, text.Contains("Swap Subject/Target", StringComparison.Ordinal));
        Assert.Equal(!modern, text.Contains("type bits 0x18", StringComparison.Ordinal));
        var viewerText = DialogueConditionDisplayFormatter.FormatCondition(condition, id => $"0x{id:X8}", game: game);
        Assert.Equal(modern, viewerText.Contains("Swap Subject/Target", StringComparison.Ordinal));
        var bytes = new byte[28];
        bytes[0] = 0x98;
        Assert.True(EsmDisplayHelpers.TryFormatSubrecordDetails("CTDA", bytes, false, out var rawText, game));
        Assert.Equal(modern, rawText.Contains("Swap Subject/Target", StringComparison.Ordinal));
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) ConditionJsonWriter.WriteCondition(writer, description);
        using var json = JsonDocument.Parse(stream.ToArray());
        Assert.Equal(unknown, json.RootElement.GetProperty("unknownTypeBits").GetInt32());
        Assert.Equal(modern ? JsonValueKind.True : JsonValueKind.Null,
            json.RootElement.GetProperty("swapSubjectTarget").ValueKind);
    }

    private const uint QuestFormId = 0x000F2429;

    [Theory]
    [InlineData("REFR", "ACTI")]
    [InlineData("ACHR", "NPC_")]
    [InlineData("ACRE", "CREA")]
    public void GetScriptVariable_FollowsExplicitPlacedBaseAndScriptLinks(string placementType, string baseType)
    {
        var records = ScriptOwnerRecords(placementType, baseType);
        var description = ConditionDescriber.Describe(ArrivalCondition(),
            ConditionDisplayContext.From(records, FormIdResolver.Empty));

        Assert.Equal("GetScriptVariable(0x01010008 (no EditorID), bPlayerInterference [var 7]) == 1 [Run On: Subject]",
            ConditionTextFormatter.FormatLine(description));
        Assert.Equal(0x01010008u, description.Parameter2?.VariableOwnerFormId);
        Assert.True(description.Parameter2?.Resolved);
    }

    [Theory]
    [InlineData("missing-script")]
    [InlineData("cleared-script-copy")]
    [InlineData("conflicting-placement")]
    [InlineData("conflicting-variable")]
    [InlineData("incomplete-table")]
    [InlineData("oversized-index")]
    public void GetScriptVariable_KeepsNumericFallbackForIncompleteOrAmbiguousOwnership(string failure)
    {
        var records = ScriptOwnerRecords("REFR", "ACTI");
        var condition = ArrivalCondition();
        switch (failure)
        {
            case "missing-script": records.Scripts.Clear(); break;
            case "cleared-script-copy":
                records.Activators.Add(records.Activators[0] with { Script = null }); break;
            case "conflicting-placement":
                records.Cells[0].PlacedObjects.Add(records.Cells[0].PlacedObjects[0] with { BaseFormId = 0x0101FFFF });
                break;
            case "conflicting-variable":
                records.Scripts.Add(records.Scripts[0] with { Variables = [new ScriptVariableInfo(7, "Other", 1)] });
                break;
            case "incomplete-table":
                records.Scripts[0] = records.Scripts[0] with { HasMalformedSerializedTable = true }; break;
            case "oversized-index": condition = condition with { Parameter2 = 0x10007 }; break;
        }

        var variable = Assert.IsType<ConditionOperand>(ConditionDescriber.Describe(condition,
            ConditionDisplayContext.From(records, FormIdResolver.Empty)).Parameter2);
        Assert.False(variable.Resolved);
        Assert.Null(variable.VariableName);
        Assert.Equal($"var {condition.Parameter2}", variable.Display);
        Assert.Equal(0x01010008u, variable.VariableOwnerFormId);
    }

    [Fact]
    public void GetScriptVariable_RebuildsOwnershipInTheRebasedCollectionNamespace()
    {
        var records = ScriptOwnerRecords("REFR", "ACTI");
        var originalContext = ConditionDisplayContext.From(records, FormIdResolver.Empty);
        Assert.Equal("bPlayerInterference", originalContext.TryGetScriptVariableName(0x01010008, 7));
        var rebased = RecordCollectionFormIdRebaser.Rebase(records,
            id => id >> 24 == 1 ? (id & 0x00FFFFFF) | 0x03000000 : id);
        // A selected winning script can replace the source's variable table; the old context stays intact.
        rebased.Scripts[0] = rebased.Scripts[0] with { Variables = [new ScriptVariableInfo(7, "SelectedName", 1)] };
        var selectedContext = ConditionDisplayContext.From(rebased, FormIdResolver.Empty);

        Assert.Equal("SelectedName", selectedContext.TryGetScriptVariableName(0x03010008, 7));
        Assert.Null(selectedContext.TryGetScriptVariableName(0x01010008, 7));
        Assert.Equal("bPlayerInterference", originalContext.TryGetScriptVariableName(0x01010008, 7));
    }

    private static DialogueCondition ArrivalCondition() => new()
    {
        FunctionIndex = 0x0035, Parameter1 = 0x01010008, Parameter2 = 7, ComparisonValue = 1
    };

    private static RecordCollection ScriptOwnerRecords(string placementType, string baseType)
    {
        // Honest Hearts' actual explicit chain: ZionArrivalBox -> ACTI 01010007 -> SCPT 01010006,
        // SLSD/SCVR slot 7 bPlayerInterference. Actor variants exercise the same typed ownership rule.
        var records = new RecordCollection
        {
            Game = BethesdaGame.FalloutNewVegas,
            Cells = [new CellRecord { FormId = 0x01010001, PlacedObjects = [new PlacedReference
                { FormId = 0x01010008, BaseFormId = 0x01010007, RecordType = placementType }] }],
            Scripts = [new ScriptRecord { FormId = 0x01010006,
                Variables = [new ScriptVariableInfo(7, "bPlayerInterference", 1)] }]
        };
        switch (baseType)
        {
            case "ACTI": records.Activators.Add(new ActivatorRecord { FormId = 0x01010007, Script = 0x01010006 }); break;
            case "NPC_": records.Npcs.Add(new NpcRecord { FormId = 0x01010007, Script = 0x01010006 }); break;
            case "CREA": records.Creatures.Add(new CreatureRecord { FormId = 0x01010007, Script = 0x01010006 }); break;
        }
        return records;
    }

    // PACK 0x000FE923 CTDA, verbatim: type 0x00, comparison 2.0f, GetQuestVariable, 0x000F2429, 11, Subject.
    private const string AliceHostetlerCtdaHex =
        "00000000" + "00000040" + "4F000000" + "29240F00" + "0B000000" + "00000000" + "00000000";

    // PACK 0x000FE91C CTDA: the same condition with type 0x60 (>=) and comparison 1.0f.
    private const string AndyLeavePackageCtdaHex =
        "60000000" + "0000803F" + "4F000000" + "29240F00" + "0B000000" + "00000000" + "00000000";

    [Fact]
    public void RetailAliceHostetlerCtda_NamesTheQuestVariableAndAlwaysStatesRunOn()
    {
        var condition = CtdaParser.Decode(Convert.FromHexString(AliceHostetlerCtdaHex), false);

        var description = Assert.Single(ConditionDescriber.DescribeAll([condition], FnvContext(VegasNorth())));

        Assert.Equal(
            "GetQuestVariable(VDialogueVegasNorth [0x000F2429], GhoulDealtWith [var 11]) == 2 [Run On: Subject]",
            ConditionTextFormatter.FormatLine(description));
        Assert.Equal("GetQuestVariable", description.FunctionName);
        Assert.True(description.FunctionKnown);
        Assert.Equal("==", description.Operator);
        Assert.Equal(2f, Assert.NotNull(description.ComparisonValue));
        Assert.Equal(ConditionDescription.ComparisonKindNumeric, description.ComparisonKind);
        Assert.Equal("Subject", description.RunOn);
        Assert.False(description.RunOnExplicit);
        Assert.Null(description.ConnectorToNext);
        Assert.Equal(1, description.OrGroup);

        var quest = Assert.IsType<ConditionOperand>(description.Parameter1);
        Assert.Equal(ConditionOperand.KindFormId, quest.Kind);
        Assert.Equal(QuestFormId, quest.FormId);
        Assert.Equal("VDialogueVegasNorth", quest.EditorId);
        Assert.True(quest.Resolved);

        var variable = Assert.IsType<ConditionOperand>(description.Parameter2);
        Assert.Equal(ConditionOperand.KindScriptVariable, variable.Kind);
        Assert.Equal(11u, variable.VariableIndex);
        Assert.Equal("GhoulDealtWith", variable.VariableName);
        Assert.Equal(QuestFormId, variable.VariableOwnerFormId);
        Assert.True(variable.Resolved);
    }

    [Fact]
    public void OperatorComesFromTheTopThreeTypeBits()
    {
        var condition = CtdaParser.Decode(Convert.FromHexString(AndyLeavePackageCtdaHex), false);

        var description = ConditionDescriber.Describe(condition, FnvContext(VegasNorth()));

        Assert.Equal(">=", description.Operator);
        Assert.Equal(3, description.OperatorCode);
        Assert.Equal(
            "GetQuestVariable(VDialogueVegasNorth [0x000F2429], GhoulDealtWith [var 11]) >= 1 [Run On: Subject]",
            ConditionTextFormatter.FormatLine(description));
    }

    [Theory]
    [InlineData(0x00, "==")]
    [InlineData(0x20, "!=")]
    [InlineData(0x40, ">")]
    [InlineData(0x60, ">=")]
    [InlineData(0x80, "<")]
    [InlineData(0xA0, "<=")]
    [InlineData(0xC0, "?(6)")]
    [InlineData(0xE0, "?(7)")]
    public void OperatorCodes_MapToTheirSymbols(int type, string expected)
    {
        var description = ConditionDescriber.Describe(
            new DialogueCondition { Type = (byte)type, FunctionIndex = 0x002E, ComparisonValue = 1 },
            FnvContext());

        Assert.Equal(expected, description.Operator);
    }

    [Fact]
    public void OrFlag_JoinsARunIntoOneGroup_AndEveryConnectorIsReported()
    {
        List<DialogueCondition> conditions =
        [
            new() { Type = 0x01, FunctionIndex = 0x002E, ComparisonValue = 1 },
            new() { Type = 0x00, FunctionIndex = 0x0023, ComparisonValue = 0 },
            new() { Type = 0x00, FunctionIndex = 0x002E, ComparisonValue = 0 }
        ];

        var descriptions = ConditionDescriber.DescribeAll(conditions, FnvContext());

        Assert.Equal(new string?[] { "OR", "AND", null }, descriptions.Select(d => d.ConnectorToNext).ToArray());
        Assert.Equal([1, 1, 2], descriptions.Select(d => d.OrGroup).ToArray());
        Assert.Equal([1, 2, 3], descriptions.Select(d => d.Index).ToArray());
        Assert.Equal("GetDead == 1 [Run On: Subject] OR", ConditionTextFormatter.FormatLine(descriptions[0]));
        Assert.Equal("GetDisabled == 0 [Run On: Subject] AND", ConditionTextFormatter.FormatLine(descriptions[1]));
        Assert.Equal("GetDead == 0 [Run On: Subject]", ConditionTextFormatter.FormatLine(descriptions[2]));

        var summary = ConditionTextFormatter.FormatLogicSummary(descriptions);
        Assert.NotNull(summary);
        Assert.StartsWith("(1 OR 2) AND 3 (GECK convention", summary, StringComparison.Ordinal);
        Assert.Contains("not verified against the engine", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("warning", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void OrFlagOnTheLastCondition_IsReportedNotHidden()
    {
        List<DialogueCondition> conditions =
        [
            new() { Type = 0x00, FunctionIndex = 0x002E, ComparisonValue = 0 },
            new() { Type = 0x01, FunctionIndex = 0x0023, ComparisonValue = 0 }
        ];

        var descriptions = ConditionDescriber.DescribeAll(conditions, FnvContext());

        Assert.False(descriptions[0].OrFlagOnLast);
        Assert.True(descriptions[1].OrFlagOnLast);
        Assert.True(descriptions[1].IsOr);
        Assert.Null(descriptions[1].ConnectorToNext);
        Assert.Equal(
            "GetDisabled == 0 [Run On: Subject] [OR flag on last condition]",
            ConditionTextFormatter.FormatLine(descriptions[1]));
        Assert.Contains(
            "warning: condition 2 is last but carries the OR flag",
            ConditionTextFormatter.FormatLogicSummary(descriptions),
            StringComparison.Ordinal);
    }

    [Fact]
    public void SingleConditionWithoutOrFlag_HasNoLogicSummary()
    {
        var descriptions = ConditionDescriber.DescribeAll(
            [new DialogueCondition { FunctionIndex = 0x002E, ComparisonValue = 1 }], FnvContext());

        Assert.Null(ConditionTextFormatter.FormatLogicSummary(descriptions));
        Assert.Null(ConditionTextFormatter.FormatLogicSummary([]));
    }

    [Fact]
    public void GlobalComparison_NamesTheGlobInsteadOfPrintingItsBitsAsAFloat()
    {
        var condition = new DialogueCondition
        {
            Type = 0x04,
            FunctionIndex = 0x002E,
            ComparisonValue = BitConverter.UInt32BitsToSingle(0x00123456)
        };

        var description = ConditionDescriber.Describe(condition, FnvContext());

        Assert.Equal(ConditionDescription.ComparisonKindGlobal, description.ComparisonKind);
        Assert.Null(description.ComparisonValue);
        Assert.Equal(0x00123456u, description.ComparisonGlobalFormId);
        Assert.Equal("GameHour", description.ComparisonGlobalEditorId);
        Assert.Equal(0x00123456u, description.ComparisonRawBits);
        Assert.Equal(
            "GetDead == GLOB GameHour [0x00123456] [Run On: Subject]",
            ConditionTextFormatter.FormatLine(description));
    }

    [Fact]
    public void ZeroVariableIndex_IsPrintedBecauseTheFunctionDeclaresIt()
    {
        var named = VegasNorth() with
        {
            Variables = [new ScriptVariableInfo(0, "bStarted", 1), new ScriptVariableInfo(11, "GhoulDealtWith", 1)]
        };
        var condition = new DialogueCondition
        {
            FunctionIndex = 0x004F,
            Parameter1 = QuestFormId,
            Parameter2 = 0,
            ComparisonValue = 1
        };

        Assert.Equal(
            "GetQuestVariable(VDialogueVegasNorth [0x000F2429], bStarted [var 0]) == 1 [Run On: Subject]",
            ConditionTextFormatter.FormatLine(ConditionDescriber.Describe(condition, FnvContext(named))));
        Assert.Equal(
            "GetQuestVariable(VDialogueVegasNorth [0x000F2429], var 0) == 1 [Run On: Subject]",
            ConditionTextFormatter.FormatLine(ConditionDescriber.Describe(condition, FnvContext())));
    }

    [Fact]
    public void UnresolvedQuestVariable_PrintsVarNAndNeverGuesses()
    {
        var withoutIndex11 = VegasNorth() with
        {
            Variables = [new ScriptVariableInfo(10, "NumFiendsDead", 1), new ScriptVariableInfo(12, "HelpMrsHostetler", 1)]
        };
        var condition = CtdaParser.Decode(Convert.FromHexString(AliceHostetlerCtdaHex), false);

        foreach (var context in new[]
                 {
                     FnvContext(withoutIndex11),
                     FnvContext(),
                     ConditionDisplayContext.ForResolver(Resolver(), BethesdaGame.FalloutNewVegas)
                 })
        {
            var variable = Assert.IsType<ConditionOperand>(ConditionDescriber.Describe(condition, context).Parameter2);
            Assert.Equal(ConditionOperand.KindScriptVariable, variable.Kind);
            Assert.Equal("var 11", variable.Display);
            Assert.Null(variable.VariableName);
            Assert.False(variable.Resolved);
            Assert.Equal(11u, variable.VariableIndex);
        }
    }

    [Fact]
    public void QuestVariableContext_MergesDuplicateQuestFormIdsWithoutThrowing()
    {
        var empty = VegasNorth() with { Variables = [] };
        var withName = VegasNorth();
        var context = FnvContext(empty, withName);

        Assert.Equal("GhoulDealtWith", context.TryGetQuestVariableName(QuestFormId, 11, out var hasVariables));
        Assert.True(hasVariables);
        Assert.Null(context.TryGetQuestVariableName(0x00ABCDEF, 11, out var unknownQuestHasVariables));
        Assert.False(unknownQuestHasVariables);
    }

    [Fact]
    public void FnvActorValue_IsNamedEvenWhenZero()
    {
        var strength = new DialogueCondition
        {
            Type = 0x60,
            FunctionIndex = 0x000E,
            Parameter1 = 5,
            ComparisonValue = 5
        };
        var aggression = strength with { Parameter1 = 0 };

        var described = ConditionDescriber.Describe(strength, FnvContext());
        var parameter = Assert.IsType<ConditionOperand>(described.Parameter1);
        Assert.Equal(ConditionOperand.KindActorValue, parameter.Kind);
        Assert.Equal("Strength", parameter.Display);
        Assert.Equal("GetActorValue(Strength) >= 5 [Run On: Subject]", ConditionTextFormatter.FormatLine(described));
        Assert.Equal(
            "GetActorValue(Aggression) >= 5 [Run On: Subject]",
            ConditionTextFormatter.FormatLine(ConditionDescriber.Describe(aggression, FnvContext())));
    }

    [Fact]
    public void GetIsSexZero_IsMale()
    {
        var condition = new DialogueCondition { FunctionIndex = 0x0046, Parameter1 = 0, ComparisonValue = 1 };

        var description = ConditionDescriber.Describe(condition, FnvContext());

        Assert.Equal(ConditionOperand.KindSex, description.Parameter1?.Kind);
        Assert.Equal("GetIsSex(Male) == 1 [Run On: Subject]", ConditionTextFormatter.FormatLine(description));
    }

    [Fact]
    public void UndeclaredZeroParameters_AreOmitted()
    {
        var description = ConditionDescriber.Describe(
            new DialogueCondition { FunctionIndex = 0x002E, ComparisonValue = 1 },
            FnvContext());

        Assert.Null(description.Parameter1);
        Assert.Null(description.Parameter2);
        Assert.Equal("GetDead == 1", description.Expression);
    }

    [Fact]
    public void UnknownFunction_StaysRawAndPositional()
    {
        var parameter1Only = new DialogueCondition { FunctionIndex = 0x7FFF, Parameter1 = QuestFormId };
        var parameter2Only = new DialogueCondition { FunctionIndex = 0x7FFF, Parameter2 = 7 };

        var first = ConditionDescriber.Describe(parameter1Only, FnvContext());
        Assert.False(first.FunctionKnown);
        Assert.Equal("Func 0x7FFF", first.FunctionName);
        Assert.Equal(ConditionOperand.KindRaw, first.Parameter1?.Kind);
        Assert.Null(first.Parameter1?.FormId);
        Assert.Null(first.Parameter2);
        Assert.Equal("Func 0x7FFF(0x000F2429) == 0", first.Expression);

        Assert.Equal(
            "Func 0x7FFF(0x00000000, 0x00000007) == 0",
            ConditionDescriber.Describe(parameter2Only, FnvContext()).Expression);
    }

    [Fact]
    public void UnresolvedFormId_IsFlaggedNotInvented()
    {
        var condition = new DialogueCondition { FunctionIndex = 0x0048, Parameter1 = 0x00ABCDEF, ComparisonValue = 1 };

        var description = ConditionDescriber.Describe(condition, FnvContext());

        var parameter = Assert.IsType<ConditionOperand>(description.Parameter1);
        Assert.Equal(ConditionOperand.KindFormId, parameter.Kind);
        Assert.False(parameter.Resolved);
        Assert.Null(parameter.EditorId);
        Assert.Equal("GetIsID(0x00ABCDEF (no EditorID)) == 1 [Run On: Subject]",
            ConditionTextFormatter.FormatLine(description));
    }

    [Fact]
    public void RunOnReference_ReportsTheSemanticReference_AndIgnoredStorageStaysRaw()
    {
        var onReference = new DialogueCondition
        {
            Type = 0x80,
            FunctionIndex = 0x0001,
            Parameter1 = 0x00000014,
            RunOn = 2,
            Reference = 0x00001234,
            ComparisonValue = 100
        };

        var description = ConditionDescriber.Describe(onReference, FnvContext());

        Assert.Equal("Reference", description.RunOn);
        Assert.True(description.RunOnExplicit);
        Assert.Equal(0x00001234u, description.Reference);
        Assert.Equal("TestReference", description.ReferenceEditorId);
        Assert.Equal(
            "GetDistance(PlayerRef [0x00000014]) < 100 [Run On: Reference; Ref: TestReference [0x00001234]]",
            ConditionTextFormatter.FormatLine(description));

        var ignored = ConditionDescriber.Describe(onReference with { RunOn = 0 }, FnvContext());
        Assert.Null(ignored.Reference);
        Assert.Null(ignored.ReferenceDisplay);
        Assert.Equal(0x00001234u, ignored.ReferenceRaw);
        Assert.Equal("Subject", ignored.RunOn);
    }

    [Theory]
    [InlineData(0x7FC00001u, "non-finite (bits 0x7FC00001)")]
    [InlineData(0x7F800000u, "non-finite (bits 0x7F800000)")]
    public void NonFiniteComparison_HasNoValueButKeepsItsBits(uint bits, string expectedDisplay)
    {
        var condition = new DialogueCondition
        {
            FunctionIndex = 0x002E,
            ComparisonValue = BitConverter.UInt32BitsToSingle(bits)
        };

        var description = ConditionDescriber.Describe(condition, FnvContext());

        Assert.Null(description.ComparisonValue);
        Assert.Equal(bits, description.ComparisonRawBits);
        Assert.Equal(expectedDisplay, description.ComparisonDisplay);
    }

    [Fact]
    public void FractionalComparison_UsesTheInvariantCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var description = ConditionDescriber.Describe(
                new DialogueCondition { FunctionIndex = 0x002E, ComparisonValue = 0.25f },
                FnvContext());

            Assert.Equal("GetDead == 0.25", description.Expression);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void CisString_WinsOverThePlaceholderSlot()
    {
        var condition = new DialogueCondition
        {
            FunctionIndex = 0x0001,
            Parameter1 = 0x00123456,
            Parameter1String = "Name \"x\"",
            ComparisonValue = 1
        };

        var description = ConditionDescriber.Describe(
            condition, ConditionDisplayContext.ForResolver(Resolver(), BethesdaGame.Fallout4));

        var parameter = Assert.IsType<ConditionOperand>(description.Parameter1);
        Assert.Equal(ConditionOperand.KindString, parameter.Kind);
        Assert.Equal("Name \"x\"", parameter.StringValue);
        Assert.Equal("\"Name \\\"x\\\"\"", parameter.Display);
        Assert.Null(parameter.FormId);
    }

    [Fact]
    public void UnknownGame_IsMappedToTheDefaultAndFlaggedAsAssumed()
    {
        var context = ConditionDisplayContext.From(new RecordCollection(), Resolver());

        Assert.Equal(BethesdaGame.FalloutNewVegas, context.Game);
        Assert.True(context.GameAssumed);
        Assert.True(context.HasQuestVariableSource);

        var explicitGame = ConditionDisplayContext.ForResolver(Resolver(), BethesdaGame.Fallout3);
        Assert.Equal(BethesdaGame.Fallout3, explicitGame.Game);
        Assert.False(explicitGame.GameAssumed);
        Assert.False(explicitGame.HasQuestVariableSource);
        Assert.True(ConditionDisplayContext.ForResolver(Resolver(), BethesdaGame.FalloutNewVegas, true).GameAssumed);
    }

    [Fact]
    public void JsonWriter_WritesEveryFieldWithExplicitNulls()
    {
        List<DialogueCondition> conditions =
        [
            CtdaParser.Decode(Convert.FromHexString(AliceHostetlerCtdaHex), false) with { Type = 0x01 },
            new() { FunctionIndex = 0x002E, ComparisonValue = BitConverter.UInt32BitsToSingle(0x7FC00001) }
        ];
        var descriptions = ConditionDescriber.DescribeAll(conditions, FnvContext(VegasNorth()));

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            ConditionJsonWriter.Write(writer, "conditions", descriptions);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(stream.ToArray());
        var array = document.RootElement.GetProperty("conditions");
        Assert.Equal(2, array.GetArrayLength());

        var first = array[0];
        Assert.Equal(1, first.GetProperty("index").GetInt32());
        Assert.Equal("GetQuestVariable", first.GetProperty("function").GetString());
        Assert.Equal(0x4F, first.GetProperty("functionIndex").GetInt32());
        Assert.True(first.GetProperty("functionKnown").GetBoolean());
        Assert.Equal("==", first.GetProperty("operator").GetString());
        Assert.Equal("numeric", first.GetProperty("comparison").GetProperty("kind").GetString());
        Assert.Equal(2d, first.GetProperty("comparison").GetProperty("value").GetDouble());
        Assert.Equal("0x40000000", first.GetProperty("comparison").GetProperty("rawBits").GetString());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("comparison").GetProperty("globalFormId").ValueKind);
        Assert.Equal("0x000F2429", first.GetProperty("parameter1").GetProperty("formId").GetString());
        Assert.Equal("VDialogueVegasNorth", first.GetProperty("parameter1").GetProperty("editorId").GetString());
        Assert.Equal("scriptVariable", first.GetProperty("parameter2").GetProperty("kind").GetString());
        Assert.Equal(11, first.GetProperty("parameter2").GetProperty("variableIndex").GetInt32());
        Assert.Equal("GhoulDealtWith", first.GetProperty("parameter2").GetProperty("variableName").GetString());
        Assert.Equal("Subject", first.GetProperty("runOn").GetProperty("name").GetString());
        Assert.Equal(0, first.GetProperty("runOn").GetProperty("raw").GetInt32());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("reference").ValueKind);
        Assert.Equal("0x00000000", first.GetProperty("referenceRaw").GetString());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("parameter3").ValueKind);
        Assert.True(first.GetProperty("or").GetBoolean());
        Assert.Equal("OR", first.GetProperty("connectorToNext").GetString());
        Assert.Equal(1, first.GetProperty("orGroup").GetInt32());
        Assert.Equal(1, first.GetProperty("typeRaw").GetInt32());
        Assert.Equal(
            "GetQuestVariable(VDialogueVegasNorth [0x000F2429], GhoulDealtWith [var 11]) == 2 [Run On: Subject] OR",
            first.GetProperty("text").GetString());

        var second = array[1];
        Assert.Equal(JsonValueKind.Null, second.GetProperty("comparison").GetProperty("value").ValueKind);
        Assert.Equal("0x7FC00001", second.GetProperty("comparison").GetProperty("rawBits").GetString());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("parameter1").ValueKind);
        Assert.Equal(JsonValueKind.Null, second.GetProperty("connectorToNext").ValueKind);
        Assert.Equal(1, second.GetProperty("orGroup").GetInt32());
    }

    private static QuestRecord VegasNorth()
    {
        return new QuestRecord
        {
            FormId = QuestFormId,
            EditorId = "VDialogueVegasNorth",
            Variables =
            [
                new ScriptVariableInfo(10, "NumFiendsDead", 1),
                new ScriptVariableInfo(11, "GhoulDealtWith", 1),
                new ScriptVariableInfo(12, "HelpMrsHostetler", 1)
            ]
        };
    }

    private static FormIdResolver Resolver()
    {
        return new FormIdResolver(
            new Dictionary<uint, string>
            {
                [QuestFormId] = "VDialogueVegasNorth",
                [0x00123456] = "GameHour",
                [0x00000014] = "PlayerRef",
                [0x00001234] = "TestReference"
            },
            new Dictionary<uint, string>());
    }

    private static ConditionDisplayContext FnvContext(params QuestRecord[] quests)
    {
        return ConditionDisplayContext.From(
            new RecordCollection { Game = BethesdaGame.FalloutNewVegas, Quests = [.. quests] },
            Resolver());
    }
}
