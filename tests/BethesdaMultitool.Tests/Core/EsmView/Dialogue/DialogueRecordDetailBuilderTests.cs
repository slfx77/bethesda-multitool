using BethesdaMultitool.Core.EsmView.Dialogue;
using BethesdaMultitool.Core.Games;
using Xunit;

// This test namespace ends in `.Dialogue`, which shadows the models namespace of the same leaf
// name; alias the two record types rather than fully qualifying them at every use site.
using DialogueCondition = BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest.DialogueCondition;
using DialogueRecord = BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest.DialogueRecord;

namespace BethesdaMultitool.Tests.Core.EsmView.Dialogue;

/// <summary>
///     Detail rows for the dialogue INFO panel.
///     <para>
///         Replaces <c>DialogueConditionUseGlobalDetailTests</c>, whose single fact asserted the
///         builder's source still contained <c>"refs.Add(cond.ComparisonGlobalFormId);"</c>. The
///         builder was under <c>App/</c> and therefore unreachable from this target framework, so
///         its 460-odd lines of identity, condition-reference, and quest-variable logic had no
///         behavioural coverage at all. Moving it to <c>Core/</c> made these possible.
///     </para>
/// </summary>
public class DialogueRecordDetailBuilderTests
{
    private const uint InfoFormId = 0x0100ABCD;
    private const uint GlobalFormId = 0x0017B37C;
    private const uint QuestFormId = 0x00021000;

    /// <summary>GetQuestVariable — the condition function the quest-variable rows key on.</summary>
    private const ushort GetQuestVariableFunctionIndex = 0x004F;

    [Fact]
    public void BuildRecordDetailRows_AlwaysLeadsWithTheFormId()
    {
        var rows = Build(MakeInfo());

        Assert.Equal("FormID", rows[0].Label);
        Assert.Equal($"0x{InfoFormId:X8}", rows[0].Value);
    }

    [Fact]
    public void BuildRecordDetailRows_OmitsTheEditorIdRowWhenThereIsNoEditorId()
    {
        var rows = Build(MakeInfo() with { EditorId = null });

        Assert.DoesNotContain(rows, r => r.Label == "EditorID");
    }

    [Fact]
    public void BuildRecordDetailRows_IncludesTheEditorIdRowWhenPresent()
    {
        var rows = Build(MakeInfo() with { EditorId = "GreetingInfo" });

        Assert.Equal("GreetingInfo", Single(rows, "EditorID").Value);
    }

    /// <summary>
    ///     The behaviour the old source pin was reaching for: a condition comparing against a
    ///     GLOB must surface that global as a navigable reference, not just as a number buried in
    ///     the condition text.
    /// </summary>
    [Fact]
    public void BuildRecordDetailRows_GlobalComparison_IsSurfacedAsANavigableReference()
    {
        var info = MakeInfo() with
        {
            Conditions = [MakeGlobalComparison(GlobalFormId)]
        };

        var row = Single(Build(info), "Condition Ref");

        Assert.Equal(GlobalFormId, row.LinkFormId);
        Assert.Contains($"0x{GlobalFormId:X8}", row.Value!, StringComparison.Ordinal);
    }

    /// <summary>A zero global is "no comparison", not a reference to FormID 0.</summary>
    [Fact]
    public void BuildRecordDetailRows_ZeroGlobalComparison_ProducesNoReferenceRow()
    {
        var info = MakeInfo() with
        {
            Conditions = [new DialogueCondition()]
        };

        Assert.DoesNotContain(Build(info), r => r.Label.StartsWith("Condition Ref", StringComparison.Ordinal));
    }

    /// <summary>
    ///     The resolved display name reaches the row, so the panel shows a name rather than only a
    ///     hex FormID.
    /// </summary>
    [Fact]
    public void BuildRecordDetailRows_ConditionRef_UsesTheResolvedDisplayName()
    {
        var info = MakeInfo() with
        {
            Conditions = [MakeGlobalComparison(GlobalFormId)]
        };

        var row = Single(Build(info, resolveFormName: _ => "VegasVictoryFlag"), "Condition Ref");

        Assert.Contains("VegasVictoryFlag", row.Value!, StringComparison.Ordinal);
    }

    /// <summary>
    ///     One reference gets an unnumbered label; several get numbered ones, so the panel does not
    ///     show three identically-labelled rows.
    /// </summary>
    [Fact]
    public void BuildRecordDetailRows_MultipleConditionRefs_AreNumbered()
    {
        var info = MakeInfo() with
        {
            Conditions =
            [
                MakeGlobalComparison(GlobalFormId),
                MakeGlobalComparison(GlobalFormId + 1)
            ]
        };

        var labels = Build(info)
            .Where(r => r.Label.StartsWith("Condition Ref", StringComparison.Ordinal))
            .Select(r => r.Label)
            .ToList();

        Assert.Equal(["Condition Ref 1", "Condition Ref 2"], labels);
    }

    /// <summary>
    ///     The same global referenced by two conditions is one reference — the collector is a set,
    ///     and duplicate rows would be noise.
    /// </summary>
    [Fact]
    public void BuildRecordDetailRows_SameGlobalTwice_IsListedOnce()
    {
        var info = MakeInfo() with
        {
            Conditions =
            [
                MakeGlobalComparison(GlobalFormId),
                MakeGlobalComparison(GlobalFormId)
            ]
        };

        var refs = Build(info)
            .Where(r => r.Label.StartsWith("Condition Ref", StringComparison.Ordinal))
            .ToList();

        Assert.Single(refs);
    }

    /// <summary>
    ///     A resolved GetQuestVariable shows <c>Quest.Variable (index N)</c> and links to the quest.
    /// </summary>
    [Fact]
    public void BuildRecordDetailRows_ResolvedQuestVariable_ShowsTheVariableNameAndLinksToTheQuest()
    {
        var info = MakeInfo() with
        {
            Conditions =
            [
                new DialogueCondition
                {
                    FunctionIndex = GetQuestVariableFunctionIndex,
                    Parameter1 = QuestFormId,
                    Parameter2 = 12
                }
            ]
        };

        var row = Single(
            Build(info, resolveEditorId: _ => "VMS01", resolveQuestVariable: (_, _) => "HastingsBribed"),
            "Condition Variable");

        Assert.Equal("VMS01.HastingsBribed (index 12)", row.Value);
        Assert.Equal(QuestFormId, row.LinkFormId);
    }

    /// <summary>
    ///     An unresolvable variable still names the quest and index rather than dropping the row —
    ///     the reader needs to know a condition exists even when its name is unknown.
    /// </summary>
    [Fact]
    public void BuildRecordDetailRows_UnresolvedQuestVariable_FallsBackToTheIndexForm()
    {
        var info = MakeInfo() with
        {
            Conditions =
            [
                new DialogueCondition
                {
                    FunctionIndex = GetQuestVariableFunctionIndex,
                    Parameter1 = QuestFormId,
                    Parameter2 = 7
                }
            ]
        };

        var row = Single(
            Build(info, resolveEditorId: _ => "VMS01", resolveQuestVariable: (_, _) => null),
            "Condition Variable");

        Assert.Equal("VMS01[7]", row.Value);
    }

    /// <summary>Without a resolver the builder must not invent quest-variable rows.</summary>
    [Fact]
    public void BuildRecordDetailRows_NoQuestVariableResolver_EmitsNoVariableRows()
    {
        var info = MakeInfo() with
        {
            Conditions =
            [
                new DialogueCondition
                {
                    FunctionIndex = GetQuestVariableFunctionIndex,
                    Parameter1 = QuestFormId,
                    Parameter2 = 3
                }
            ]
        };

        Assert.DoesNotContain(Build(info),
            r => r.Label.StartsWith("Condition Variable", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildRecordDetailRows_MultipleQuestVariables_AreNumbered()
    {
        var info = MakeInfo() with
        {
            Conditions =
            [
                MakeQuestVariableCondition(QuestFormId, 1),
                MakeQuestVariableCondition(QuestFormId, 2)
            ]
        };

        var labels = Build(info, resolveEditorId: _ => "VMS01", resolveQuestVariable: (_, i) => $"Var{i}")
            .Where(r => r.Label.StartsWith("Condition Variable", StringComparison.Ordinal))
            .Select(r => r.Label)
            .ToList();

        Assert.Equal(["Condition Variable 1", "Condition Variable 2"], labels);
    }

    [Fact]
    public void BuildRecordDetailRows_NoConditions_ProducesNoConditionRows()
    {
        var rows = Build(MakeInfo() with { Conditions = [] });

        Assert.DoesNotContain(rows, r => r.Label.StartsWith("Condition", StringComparison.Ordinal));
    }

    /// <summary>
    ///     A condition whose CTDA comparison union holds a GLOB FormID. That is how the format
    ///     encodes it: the "uses global" bit in Type, and the FormID's bits reinterpreted as the
    ///     comparison float — so a test cannot simply assign ComparisonGlobalFormId, which is
    ///     derived.
    /// </summary>
    private static DialogueCondition MakeGlobalComparison(uint globalFormId)
    {
        const byte usesGlobalComparisonBit = 0x04;

        return new DialogueCondition
        {
            Type = usesGlobalComparisonBit,
            ComparisonValue = BitConverter.UInt32BitsToSingle(globalFormId)
        };
    }

    private static DialogueCondition MakeQuestVariableCondition(uint questFormId, uint variableIndex)
    {
        return new DialogueCondition
        {
            FunctionIndex = GetQuestVariableFunctionIndex,
            Parameter1 = questFormId,
            Parameter2 = variableIndex
        };
    }

    private static DialogueRecord MakeInfo()
    {
        return new DialogueRecord
        {
            FormId = InfoFormId,
            Conditions = []
        };
    }

    private static List<DialogueRecordDetailBuilder.DetailRow> Build(
        DialogueRecord info,
        Func<uint, string>? resolveFormName = null,
        Func<uint, string>? resolveEditorId = null,
        Func<uint, uint, string?>? resolveQuestVariable = null)
    {
        return DialogueRecordDetailBuilder.BuildRecordDetailRows(
            info,
            csvSubtitle: null,
            resolveFormName ?? (id => $"Form{id:X8}"),
            resolveSpeakerName: _ => "Speaker",
            topicEditorId: null,
            resolveEditorId,
            resolveQuestVariable,
            BethesdaGame.FalloutNewVegas);
    }

    private static DialogueRecordDetailBuilder.DetailRow Single(
        List<DialogueRecordDetailBuilder.DetailRow> rows, string label)
    {
        var match = rows.Where(r => r.Label == label).ToList();

        Assert.True(match.Count == 1,
            $"Expected exactly one `{label}` row; got {match.Count}. Rows: "
            + string.Join(", ", rows.Select(r => r.Label)));
        return match[0];
    }
}
