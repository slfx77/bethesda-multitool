using System.Text.Json;
using BethesdaMultitool.Core.Formats.Esm.Export.AiPackages;
using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.AI;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Script.Conditions;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Export;

/// <summary>
///     Pins the <c>esm packages -f json</c> schema written by <see cref="PackageJsonWriter" />. These run
///     in-process, where reflection-based JSON would also have worked; the discriminating red for the
///     trimmed-exe failure is <c>PackagesCommandJsonExeSmokeTests</c> (and, for the described conditions,
///     <c>ShowPackConditionsExeSmokeTests</c>). Every expected value here is an independent literal, never
///     another formatter's output. The condition fixtures are FNV retail PACK 0x000FE923
///     AliceHostetlerRunAway's CTDA (GetQuestVariable on quest 0x000F2429 VDialogueVegasNorth, variable 11,
///     == 2, Subject), whose quest script declares 10 NumFiendsDead, 11 GhoulDealtWith, 12 HelpMrsHostetler.
/// </summary>
public sealed class PackageJsonWriterTests
{
    private const uint CellFormId = 0x0009A285;
    private const uint ObjectTypeValue = 0x00000012;
    private const uint QuestFormId = 0x000F2429;
    private const ushort GetQuestVariableFunction = 0x004F;
    private const uint GhoulDealtWithIndex = 11;

    private const string AbsenceWording =
        "not present in this capture; absence from a partial memory dump is not evidence of absence from the build";

    /// <summary>A NaN whose exact bits are known (float.NaN itself is 0xFFC00000 on .NET).</summary>
    private static readonly float PositiveQuietNaN = BitConverter.UInt32BitsToSingle(0x7FC00000);

    [Fact]
    public void Write_EmitsSingleObject_WithStableFieldsAndNullSafeFloats()
    {
        var package = new PackageRecord
        {
            FormId = 0x000FE923,
            EditorId = "SyntheticRunAway",
            Data = new PackageData
            {
                Type = 6,
                GeneralFlags = 0x00000402,
                FalloutBehaviorFlags = 0x0003,
                TypeSpecificFlags = 0
            },
            Schedule = new PackageSchedule { Month = -1, DayOfWeek = -1, Date = 0, Time = -1, Duration = 0 },
            Location = new PackageLocation { Type = 1, Union = CellFormId, Radius = 0 },
            Target = new PackageTarget
            {
                Type = 2,
                FormIdOrType = ObjectTypeValue,
                CountDistance = 750,
                AcquireRadius = PositiveQuietNaN
            },
            Conditions =
            [
                new DialogueCondition
                {
                    Type = 0x60,
                    ComparisonValue = 2f,
                    FunctionIndex = 0x4F,
                    Parameter1 = 0x000F2429,
                    Parameter2 = 11,
                    RunOn = 0,
                    Reference = 0
                }
            ],
            IsRepeatable = true
        };

        using var json = WriteAndParse(Document([package], matched: 3, typeFilter: "Travel"));
        var root = json.RootElement;

        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.Equal("bethesda-multitool/esm-packages", root.GetProperty("schema").GetString());
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("test-tool-1.2.3", root.GetProperty("toolVersion").GetString());
        Assert.Equal(@"C:\synthetic\FalloutNV.esm", root.GetProperty("source").GetString());
        Assert.Equal("FalloutNewVegas", root.GetProperty("game").GetString());
        Assert.Equal(4163, root.GetProperty("totalPackages").GetInt32());
        Assert.Equal(3, root.GetProperty("matchedPackages").GetInt32());
        Assert.Equal(1, root.GetProperty("shownPackages").GetInt32());
        Assert.True(root.GetProperty("truncated").GetBoolean());

        var filters = root.GetProperty("filters");
        Assert.Equal("Travel", filters.GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Null, filters.GetProperty("npc").ValueKind);
        Assert.Equal(1, filters.GetProperty("limit").GetInt32());

        var written = Assert.Single(root.GetProperty("packages").EnumerateArray());
        Assert.Equal("0x000FE923", written.GetProperty("formId").GetString());
        Assert.Equal("SyntheticRunAway", written.GetProperty("editorId").GetString());
        Assert.Equal("Travel", written.GetProperty("type").GetString());
        Assert.Equal(6, written.GetProperty("typeCode").GetInt32());

        var schedule = written.GetProperty("schedule");
        Assert.Equal(-1, schedule.GetProperty("time").GetInt32());
        Assert.Equal(-1, schedule.GetProperty("month").GetInt32());
        Assert.Equal("Any", schedule.GetProperty("monthName").GetString());
        Assert.Equal("Any", schedule.GetProperty("dayOfWeekName").GetString());
        Assert.Equal(0, schedule.GetProperty("durationHours").GetInt32());

        var location = written.GetProperty("location");
        Assert.Equal(1, location.GetProperty("type").GetInt32());
        Assert.Equal("0x0009A285", location.GetProperty("union").GetString());
        Assert.True(location.GetProperty("unionIsFormId").GetBoolean());
        Assert.Equal("SyntheticCell", location.GetProperty("unionEditorId").GetString());
        Assert.Equal(JsonValueKind.Null, written.GetProperty("location2").ValueKind);

        var target = written.GetProperty("target");
        Assert.Equal("Object Type", target.GetProperty("typeName").GetString());
        Assert.Equal("0x00000012", target.GetProperty("formIdOrType").GetString());
        Assert.Equal(750, target.GetProperty("countDistance").GetInt32());
        Assert.Equal(JsonValueKind.Null, target.GetProperty("acquireRadius").ValueKind);
        Assert.Equal("0x7FC00000", target.GetProperty("acquireRadiusRawBits").GetString());
        Assert.Equal(JsonValueKind.Null, written.GetProperty("target2").ValueKind);

        Assert.Equal("Must Reach Location, Once Per Day", written.GetProperty("generalFlags").GetString());
        Assert.Equal(0x402, written.GetProperty("generalFlagsRaw").GetInt32());
        Assert.Equal("Hellos to Player, Random Conversations", written.GetProperty("foBehaviorFlags").GetString());
        Assert.Equal(3, written.GetProperty("foBehaviorFlagsRaw").GetInt32());
        Assert.Equal(JsonValueKind.Null, written.GetProperty("typeSpecificFlags").ValueKind);
        Assert.Equal(0, written.GetProperty("typeSpecificFlagsRaw").GetInt32());
        Assert.True(written.GetProperty("isRepeatable").GetBoolean());
        Assert.False(written.GetProperty("startingLocationLinkedRef").GetBoolean());

        var condition = Assert.Single(written.GetProperty("conditionsRaw").EnumerateArray());
        Assert.Equal(1, condition.GetProperty("index").GetInt32());
        Assert.Equal(0x4F, condition.GetProperty("functionIndex").GetInt32());
        Assert.Equal(0x60, condition.GetProperty("typeRaw").GetInt32());
        Assert.Equal("0x40000000", condition.GetProperty("comparisonRawBits").GetString());
        Assert.Equal("0x000F2429", condition.GetProperty("parameter1").GetString());
        Assert.Equal("0x0000000B", condition.GetProperty("parameter2").GetString());
        Assert.Equal(0, condition.GetProperty("runOn").GetInt32());
        Assert.Equal("0x00000000", condition.GetProperty("reference").GetString());
        Assert.Equal(JsonValueKind.Null, condition.GetProperty("parameter3").ValueKind);
        Assert.Equal(JsonValueKind.Null, condition.GetProperty("parameter1String").ValueKind);
    }

    /// <summary>
    ///     An enum or unused union arm must never borrow the name of a record that happens to own the same
    ///     number: object type 18 is not FormID 0x00000012, even when the resolver knows one.
    /// </summary>
    [Fact]
    public void Write_NonFormIdUnionArms_AreNeverResolvedToNames()
    {
        var package = new PackageRecord
        {
            FormId = 0x00168CEE,
            Location = new PackageLocation { Type = 5, Union = ObjectTypeValue, Radius = 512 },
            Location2 = new PackageLocation { Type = 4, Union = ObjectTypeValue, Radius = 0 },
            Target = new PackageTarget { Type = 2, FormIdOrType = ObjectTypeValue, CountDistance = 1, AcquireRadius = 0f },
            Target2 = new PackageTarget { Type = 1, FormIdOrType = ObjectTypeValue, CountDistance = 1, AcquireRadius = 0f }
        };

        using var json = WriteAndParse(Document([package], matched: 1));
        var written = Assert.Single(json.RootElement.GetProperty("packages").EnumerateArray());

        var location = written.GetProperty("location");
        Assert.False(location.GetProperty("unionIsFormId").GetBoolean());
        Assert.Equal(JsonValueKind.Null, location.GetProperty("unionEditorId").ValueKind);
        Assert.Equal(512, location.GetProperty("radius").GetInt32());

        var target = written.GetProperty("target");
        Assert.False(target.GetProperty("unionIsFormId").GetBoolean());
        Assert.Equal(JsonValueKind.Null, target.GetProperty("editorId").ValueKind);

        // Controls: the same number on a FormID-bearing arm (Object ID) does resolve.
        Assert.Equal("HorseMarker", written.GetProperty("location2").GetProperty("unionEditorId").GetString());
        Assert.Equal("HorseMarker", written.GetProperty("target2").GetProperty("editorId").GetString());
    }

    [Fact]
    public void Write_PackageWithoutSubrecords_WritesExplicitNullsAndEmptyConditions()
    {
        using var json = WriteAndParse(Document([new PackageRecord { FormId = 0x00000ABC }], matched: 1));
        var root = json.RootElement;
        Assert.False(root.GetProperty("truncated").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("filters").GetProperty("type").ValueKind);

        var written = Assert.Single(root.GetProperty("packages").EnumerateArray());
        Assert.Equal("0x00000ABC", written.GetProperty("formId").GetString());
        Assert.Equal(JsonValueKind.Null, written.GetProperty("editorId").ValueKind);
        Assert.Equal("AI Package", written.GetProperty("type").GetString());
        foreach (var name in new[]
                 {
                     "typeCode", "schedule", "location", "location2", "target", "target2", "generalFlags",
                     "generalFlagsRaw", "foBehaviorFlags", "foBehaviorFlagsRaw", "typeSpecificFlags",
                     "typeSpecificFlagsRaw"
                 })
        {
            Assert.True(written.TryGetProperty(name, out var value), $"'{name}' must always be present.");
            Assert.Equal(JsonValueKind.Null, value.ValueKind);
        }

        Assert.False(written.GetProperty("isRepeatable").GetBoolean());
        Assert.Equal(0, written.GetProperty("conditionsRaw").GetArrayLength());
        Assert.Equal(0, written.GetProperty("conditions").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, written.GetProperty("conditionLogic").ValueKind);
    }

    /// <summary>
    ///     The retail AliceHostetlerRunAway condition, described from a record collection whose quest names its
    ///     variables: <c>conditions</c> carries the function, operator, comparison, both resolved operands and
    ///     the one-line text, and the raw words stay beside it unchanged (the change is additive).
    /// </summary>
    [Fact]
    public void PackageJsonWriter_WritesDescribedConditions()
    {
        var resolver = QuestResolver();
        var records = new RecordCollection
        {
            Game = BethesdaGame.FalloutNewVegas,
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
        var package = new PackageRecord
        {
            FormId = 0x000FE923,
            EditorId = "AliceHostetlerRunAway",
            Conditions = [GhoulDealtWith(0x00, 2f)]
        };

        using var json = WriteAndParse(
            Document([package], matched: 1),
            resolver,
            ConditionDisplayContext.From(records, resolver));
        var root = json.RootElement;

        var context = root.GetProperty("conditionContext");
        Assert.Equal("FalloutNewVegas", context.GetProperty("game").GetString());
        Assert.False(context.GetProperty("gameAssumed").GetBoolean());
        Assert.True(context.GetProperty("questVariableSource").GetBoolean());
        Assert.False(context.GetProperty("memoryDumpInput").GetBoolean());
        Assert.Equal(JsonValueKind.Null, context.GetProperty("absenceNote").ValueKind);
        Assert.StartsWith("GECK convention", context.GetProperty("grouping").GetString(), StringComparison.Ordinal);

        var written = Assert.Single(root.GetProperty("packages").EnumerateArray());
        var condition = Assert.Single(written.GetProperty("conditions").EnumerateArray());
        Assert.Equal(1, condition.GetProperty("index").GetInt32());
        Assert.Equal("GetQuestVariable", condition.GetProperty("function").GetString());
        Assert.Equal(0x4F, condition.GetProperty("functionIndex").GetInt32());
        Assert.True(condition.GetProperty("functionKnown").GetBoolean());
        Assert.Equal("==", condition.GetProperty("operator").GetString());
        Assert.Equal(0, condition.GetProperty("operatorCode").GetInt32());

        var comparison = condition.GetProperty("comparison");
        Assert.Equal("numeric", comparison.GetProperty("kind").GetString());
        Assert.Equal(2f, comparison.GetProperty("value").GetSingle());
        Assert.Equal("0x40000000", comparison.GetProperty("rawBits").GetString());
        Assert.Equal(JsonValueKind.Null, comparison.GetProperty("globalFormId").ValueKind);

        var quest = condition.GetProperty("parameter1");
        Assert.Equal("formId", quest.GetProperty("kind").GetString());
        Assert.Equal("0x000F2429", quest.GetProperty("formId").GetString());
        Assert.Equal("VDialogueVegasNorth", quest.GetProperty("editorId").GetString());
        Assert.True(quest.GetProperty("resolved").GetBoolean());

        // Index 11 names GhoulDealtWith, never its neighbors 10 or 12.
        var variable = condition.GetProperty("parameter2");
        Assert.Equal("scriptVariable", variable.GetProperty("kind").GetString());
        Assert.Equal(11, variable.GetProperty("variableIndex").GetInt32());
        Assert.Equal("GhoulDealtWith", variable.GetProperty("variableName").GetString());
        Assert.Equal("0x000F2429", variable.GetProperty("variableOwnerFormId").GetString());
        Assert.Equal("0x0000000B", variable.GetProperty("raw").GetString());
        Assert.True(variable.GetProperty("resolved").GetBoolean());

        var runOn = condition.GetProperty("runOn");
        Assert.Equal(0, runOn.GetProperty("raw").GetInt32());
        Assert.Equal("Subject", runOn.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, condition.GetProperty("reference").ValueKind);
        Assert.Equal(JsonValueKind.Null, condition.GetProperty("connectorToNext").ValueKind);
        Assert.False(condition.GetProperty("or").GetBoolean());
        Assert.Equal(1, condition.GetProperty("orGroup").GetInt32());
        Assert.Equal(
            "GetQuestVariable(VDialogueVegasNorth [0x000F2429], GhoulDealtWith [var 11]) == 2 [Run On: Subject]",
            condition.GetProperty("text").GetString());
        Assert.Equal(JsonValueKind.Null, written.GetProperty("conditionLogic").ValueKind);

        // Additive: the raw words are still written, untouched.
        var raw = Assert.Single(written.GetProperty("conditionsRaw").EnumerateArray());
        Assert.Equal(0x4F, raw.GetProperty("functionIndex").GetInt32());
        Assert.Equal("0x000F2429", raw.GetProperty("parameter1").GetString());
        Assert.Equal("0x0000000B", raw.GetProperty("parameter2").GetString());
        Assert.Equal("0x40000000", raw.GetProperty("comparisonRawBits").GetString());
    }

    /// <summary>
    ///     Without a record collection there are no quest-variable names, and none may be invented: the
    ///     variable stays <c>var 11</c> with a null name, and a quest the resolver cannot name says so.
    /// </summary>
    [Fact]
    public void Write_WithoutConditionContext_NeverGuessesAVariableName()
    {
        var package = new PackageRecord { FormId = 0x000FE91C, Conditions = [GhoulDealtWith(0x60, 1f)] };

        using var json = WriteAndParse(Document([package], matched: 1));
        var root = json.RootElement;
        Assert.False(root.GetProperty("conditionContext").GetProperty("questVariableSource").GetBoolean());

        var condition = Assert.Single(
            Assert.Single(root.GetProperty("packages").EnumerateArray()).GetProperty("conditions").EnumerateArray());
        Assert.Equal(">=", condition.GetProperty("operator").GetString());
        Assert.Equal(3, condition.GetProperty("operatorCode").GetInt32());

        var quest = condition.GetProperty("parameter1");
        Assert.Equal(JsonValueKind.Null, quest.GetProperty("editorId").ValueKind);
        Assert.False(quest.GetProperty("resolved").GetBoolean());

        var variable = condition.GetProperty("parameter2");
        Assert.Equal(JsonValueKind.Null, variable.GetProperty("variableName").ValueKind);
        Assert.Equal("var 11", variable.GetProperty("display").GetString());
        Assert.False(variable.GetProperty("resolved").GetBoolean());
        Assert.Equal(
            "GetQuestVariable(0x000F2429 (no EditorID), var 11) >= 1 [Run On: Subject]",
            condition.GetProperty("text").GetString());
    }

    /// <summary>
    ///     An OR-flagged condition joins the next one: both connectors and the group are reported, and the
    ///     grouping summary is labeled as the GECK convention rather than asserted as engine behavior.
    /// </summary>
    [Fact]
    public void Write_OrFlaggedConditions_ReportConnectorsAndTheGeckGroupingSummary()
    {
        var package = new PackageRecord
        {
            FormId = 0x000FE924,
            Conditions = [GhoulDealtWith(0x01, 1f), GhoulDealtWith(0x00, 2f)]
        };

        using var json = WriteAndParse(Document([package], matched: 1), QuestResolver());
        var written = Assert.Single(json.RootElement.GetProperty("packages").EnumerateArray());
        var conditions = written.GetProperty("conditions").EnumerateArray().ToArray();

        Assert.Equal(2, conditions.Length);
        Assert.True(conditions[0].GetProperty("or").GetBoolean());
        Assert.Equal("OR", conditions[0].GetProperty("connectorToNext").GetString());
        Assert.Equal(1, conditions[0].GetProperty("orGroup").GetInt32());
        Assert.EndsWith("[Run On: Subject] OR", conditions[0].GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.False(conditions[1].GetProperty("or").GetBoolean());
        Assert.Equal(JsonValueKind.Null, conditions[1].GetProperty("connectorToNext").ValueKind);
        Assert.Equal(1, conditions[1].GetProperty("orGroup").GetInt32());
        Assert.False(conditions[1].GetProperty("orFlagOnLast").GetBoolean());

        var logic = written.GetProperty("conditionLogic").GetString();
        Assert.StartsWith("1 OR 2 (GECK convention", logic, StringComparison.Ordinal);
        Assert.Contains("not verified against the engine", logic, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_MemoryDumpInput_RecordsThePartialCaptureAbsenceNote()
    {
        using var json = WriteAndParse(Document([], matched: 0), isMemoryDumpInput: true);

        var context = json.RootElement.GetProperty("conditionContext");
        Assert.True(context.GetProperty("memoryDumpInput").GetBoolean());
        Assert.Equal(AbsenceWording, context.GetProperty("absenceNote").GetString());
    }

    /// <summary>
    ///     An undetected game is described with the default (New Vegas) condition table, and the document says
    ///     the game was assumed, because every function name then depends on that assumption.
    /// </summary>
    [Fact]
    public void Write_UnknownGame_SaysTheConditionTableGameWasAssumed()
    {
        var package = new PackageRecord { FormId = 0x00000ABC, Conditions = [GhoulDealtWith(0x00, 2f)] };

        using var json = WriteAndParse(Document([package], matched: 1, game: BethesdaGame.Unknown));
        var root = json.RootElement;

        Assert.Equal("Unknown", root.GetProperty("game").GetString());
        var context = root.GetProperty("conditionContext");
        Assert.Equal("FalloutNewVegas", context.GetProperty("game").GetString());
        Assert.True(context.GetProperty("gameAssumed").GetBoolean());
        var condition = Assert.Single(
            Assert.Single(root.GetProperty("packages").EnumerateArray()).GetProperty("conditions").EnumerateArray());
        Assert.Equal("GetQuestVariable", condition.GetProperty("function").GetString());
    }

    [Fact]
    public void Write_FiniteAcquireRadius_WritesNumberAndItsRawBits()
    {
        var package = new PackageRecord
        {
            FormId = 0x00000001,
            Target = new PackageTarget { Type = 0, FormIdOrType = CellFormId, CountDistance = 0, AcquireRadius = 256f }
        };

        using var json = WriteAndParse(Document([package], matched: 1));
        var target = Assert.Single(json.RootElement.GetProperty("packages").EnumerateArray()).GetProperty("target");
        Assert.Equal(256f, target.GetProperty("acquireRadius").GetSingle());
        Assert.Equal("0x43800000", target.GetProperty("acquireRadiusRawBits").GetString());
    }

    [Fact]
    public void Write_LeavesTheCallerStreamOpen()
    {
        using var stream = new MemoryStream();
        PackageJsonWriter.Write(stream, Document([], matched: 0), Resolver());

        Assert.True(stream.CanWrite);
        stream.Position = 0;
        using var json = JsonDocument.Parse(stream);
        Assert.Equal(0, json.RootElement.GetProperty("packages").GetArrayLength());
    }

    private static PackageJsonDocument Document(
        IReadOnlyList<PackageRecord> packages,
        int matched,
        string? typeFilter = null,
        BethesdaGame game = BethesdaGame.FalloutNewVegas)
    {
        return new PackageJsonDocument(
            @"C:\synthetic\FalloutNV.esm",
            game,
            "test-tool-1.2.3",
            4163,
            matched,
            packages,
            typeFilter,
            null,
            1);
    }

    /// <summary>GetQuestVariable(0x000F2429, 11) with the given raw type byte and comparison, run on Subject.</summary>
    private static DialogueCondition GhoulDealtWith(byte type, float comparison)
    {
        return new DialogueCondition
        {
            Type = type,
            ComparisonValue = comparison,
            FunctionIndex = GetQuestVariableFunction,
            Parameter1 = QuestFormId,
            Parameter2 = GhoulDealtWithIndex,
            RunOn = 0,
            Reference = 0
        };
    }

    private static FormIdResolver Resolver()
    {
        return new FormIdResolver(
            new Dictionary<uint, string>
            {
                [CellFormId] = "SyntheticCell",
                [ObjectTypeValue] = "HorseMarker"
            },
            []);
    }

    private static FormIdResolver QuestResolver()
    {
        return new FormIdResolver(
            new Dictionary<uint, string> { [QuestFormId] = "VDialogueVegasNorth" },
            []);
    }

    private static JsonDocument WriteAndParse(
        PackageJsonDocument document,
        FormIdResolver? resolver = null,
        ConditionDisplayContext? conditionContext = null,
        bool isMemoryDumpInput = false)
    {
        using var stream = new MemoryStream();
        PackageJsonWriter.Write(stream, document, resolver ?? Resolver(), conditionContext, isMemoryDumpInput);
        stream.Position = 0;

        // JsonDocument.Parse rejects a second top-level value, so success also proves ONE object.
        return JsonDocument.Parse(stream);
    }
}
