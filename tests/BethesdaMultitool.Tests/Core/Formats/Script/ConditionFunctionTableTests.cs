using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Script;
using BethesdaMultitool.Core.Formats.Esm.Script.Conditions;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Script;

/// <summary>
///     The game-keyed condition-function tables (S1): Oblivion's engine-extracted command table and
///     the shared numeric-vs-FormID classifier lifted from <c>DialogueConditionDisplayFormatter</c>.
///     Oblivion conditions used to render through the FNV table — wrong names past index 0x171 and
///     wrong param typing past raw id 31 (the numberings diverge at 32: TES4 Birthsign vs FNV FormType).
/// </summary>
public class ConditionFunctionTableTests
{
    [Fact]
    public void FalloutTable_BacksTheSharedFacade()
    {
        var set = ScriptFunctionTables.For(BethesdaGame.FalloutNewVegas);
        Assert.Same(ScriptFunctionTable.Get(0x102F), set.Get(0x102F)); // GetItemCount
        Assert.Equal(ScriptFunctionTable.GetName(0x1030), set.GetName(0x1030));

        // FO3 and unknown inputs keep riding the FNV table — the pre-facade behavior.
        Assert.Same(set, ScriptFunctionTables.For(BethesdaGame.Fallout3));
        Assert.Same(set, ScriptFunctionTables.For(BethesdaGame.Unknown));
    }

    [Theory]
    [InlineData(0x1001, "GetDistance", true)]
    [InlineData(0x1048, "GetIsID", true)]
    [InlineData(0x1029, "GetClothingValue", true)]
    [InlineData(0x1462, "HasSpell", false)] // condition-only, xEdit-sourced
    public void OblivionTable_CarriesEngineEntries(ushort opcode, string name, bool isRef)
    {
        var set = ScriptFunctionTables.For(BethesdaGame.Oblivion);
        var def = set.Get(opcode);
        Assert.NotNull(def);
        Assert.Equal(name, def!.Name);
        Assert.Equal(isRef, def.IsReferenceFunction);
    }

    [Fact]
    public void OblivionAndFalloutTables_DivergeWhereTheEnginesDo()
    {
        var oblivion = ScriptFunctionTables.For(BethesdaGame.Oblivion);
        var fallout = ScriptFunctionTables.For(BethesdaGame.FalloutNewVegas);

        // The tables genuinely diverge (FNV kept many legacy TES4 names at the same indices, but
        // rewrote/added others) — count the disagreements over TES4's whole opcode range.
        var diverging = 0;
        for (ushort op = 0x1000; op <= 0x1171; op++)
        {
            var obName = oblivion.Get(op)?.Name;
            if (obName is not null && fallout.Get(op)?.Name is { } fnvName &&
                !string.Equals(obName, fnvName, StringComparison.OrdinalIgnoreCase))
            {
                diverging++;
            }
        }

        // 49 divergences as of the FNV Aug-2010 / Oblivion retail extractions.
        Assert.True(diverging >= 40, $"expected substantial divergence, found {diverging} differing names");

        // And every condition-only function is missing from the FNV table entirely.
        Assert.NotNull(oblivion.Get(0x1462)); // HasSpell
        Assert.Equal("UnknownFunc_0x1462", fallout.GetName(0x1462));
    }

    [Fact]
    public void OblivionClassifier_UsesTes4Numbering()
    {
        var table = ConditionFunctionTable.For(BethesdaGame.Oblivion);

        // GetIsID: param1 is a base-object FormID.
        Assert.Equal(ConditionParamKind.FormId, table.ClassifyParam(0x048, 0));
        // GetActorValue: param1 is an AV index — numeric.
        Assert.Equal(ConditionParamKind.Numeric, table.ClassifyParam(0x00E, 0));
        // GetStageDone: (Quest FormID, Stage number).
        Assert.Equal(ConditionParamKind.FormId, table.ClassifyParam(0x03B, 0));
        Assert.Equal(ConditionParamKind.Numeric, table.ClassifyParam(0x03B, 1));
    }

    [Fact]
    public void FalloutClassifier_MatchesTheFormatterForRealConditions()
    {
        // Parity gate: the shared classifier must agree with IsFormReference for a
        // representative FNV sample (the formatter now delegates to it — this pins the seam).
        var samples = new (ushort FunctionIndex, int ParamIndex, bool ExpectFormId)[]
        {
            (0x02F, 0, true), // GetItemCount(ObjectID)
            (0x00E, 0, true), // GetActorValue — FNV's historic switch resolves AV params as names (parity-preserved)
            (0x04F, 0, true), // GetQuestVariable(Quest, var) — quest is a FormID
            (0x04F, 1, false), // …the variable index is numeric (ScriptVar)
            (0x00A, 0, false), // GetStartingPos(Axis)
            (0x046, 0, false) // GetIsSex(Sex)
        };

        foreach (var (functionIndex, paramIndex, expectFormId) in samples)
        {
            var condition = new DialogueCondition
            {
                FunctionIndex = functionIndex,
                Parameter1 = 0x1234,
                Parameter2 = 0x5678
            };
            var actual = DialogueConditionDisplayFormatter.IsFormReference(condition, paramIndex);
            Assert.True(expectFormId == actual,
                $"0x{functionIndex:X3}[{paramIndex}]: expected FormId={expectFormId}, got {actual}");
        }
    }
}