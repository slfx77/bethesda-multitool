using BethesdaMultitool.Core.Formats.Esm.Script;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Script;

public sealed class ScriptComparerTests
{
    private static readonly Dictionary<string, string> EmptyNameMap =
        new(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void CompareScripts_HeaderAndDeclarationsOnly_HasNoComparableStatements()
    {
        const string source = """
                              scn VFreeformGoodspringsScript

                              short PressDemo
                              short bMetRingo
                              """;
        const string decompiled = "ScriptName VFreeformGoodspringsScript";

        var result = ScriptComparer.CompareScripts(source, decompiled, EmptyNameMap);

        Assert.Equal(0, result.MatchCount);
        Assert.Equal(0, result.TotalMismatches);
        Assert.Equal(0, result.TotalLines);
        Assert.Equal(0, result.MatchRate);
    }

    [Fact]
    public void CompareScripts_DecorativeDashSeparators_AreNotExecutableStatements()
    {
        const string source = """
                              scn VMS12QuestScript
                              ------------------------------------------------------------------------------------------------------------------------------------------------
                              short DoOnce
                              ------------------------------------------------------------------------------------------------------------------------------------------------
                              """;
        const string decompiled = "ScriptName VMS12QuestScript";

        var result = ScriptComparer.CompareScripts(source, decompiled, EmptyNameMap);

        Assert.Equal(0, result.TotalLines);
        Assert.Equal(0, result.TotalMismatches);
    }

    [Fact]
    public void CompareScripts_TrailingSourceStatements_CountsEverySourceOnlyStatement()
    {
        const string source = """
                              ScriptName TestScript
                              Begin GameMode
                              Set foo To 1
                              End
                              """;
        const string decompiled = "ScriptName TestScript";

        var result = ScriptComparer.CompareScripts(source, decompiled, EmptyNameMap);

        Assert.Equal(3, result.MismatchesByCategory["SourceOnly"]);
        Assert.Equal(3, result.TotalMismatches);
        Assert.Equal(3, result.TotalLines);
        Assert.All(result.Examples, example =>
        {
            Assert.Equal("SourceOnly", example.Category);
            Assert.NotEmpty(example.Source);
            Assert.Empty(example.Decompiled);
        });
    }

    [Fact]
    public void CompareScripts_TrailingDecompiledStatements_CountsEveryDecompiledOnlyStatement()
    {
        const string source = "ScriptName TestScript";
        const string decompiled = """
                                  ScriptName TestScript
                                  Begin GameMode
                                  Return
                                  End
                                  """;

        var result = ScriptComparer.CompareScripts(source, decompiled, EmptyNameMap);

        Assert.Equal(3, result.MismatchesByCategory["DecompiledOnly"]);
        Assert.Equal(3, result.TotalMismatches);
        Assert.Equal(3, result.TotalLines);
        Assert.All(result.Examples, example =>
        {
            Assert.Equal("DecompiledOnly", example.Category);
            Assert.Empty(example.Source);
            Assert.NotEmpty(example.Decompiled);
        });
    }

    [Fact]
    public void NormalizeScriptLine_SemicolonInsideQuotedText_IsNotAComment()
    {
        const string line = "ShowMessage \"Recovered; still part of the line\" ; actual comment";

        var normalized = ScriptComparer.NormalizeScriptLine(line);

        Assert.Equal("ShowMessage \"Recovered; still part of the line\"", normalized);
    }

    [Fact]
    public void CompareScripts_DifferentQuotedTextAfterSemicolon_DoesNotFalseMatch()
    {
        const string source = "ShowMessage \"Recovered; source text\"";
        const string decompiled = "ShowMessage \"Recovered; compiled text\"";

        var result = ScriptComparer.CompareScripts(source, decompiled, EmptyNameMap);

        Assert.Equal(1, result.TotalMismatches);
        Assert.Equal(1, result.MismatchesByCategory["Other"]);
    }

    [Fact]
    public void CompareScripts_ShorterSourceOperand_IsNotDroppedParameter()
    {
        const string source = "StopCombat";
        const string decompiled = "StopCombat Player";

        var result = ScriptComparer.CompareScripts(source, decompiled, EmptyNameMap);

        Assert.Equal(1, result.TotalMismatches);
        Assert.Equal(1, result.MismatchesByCategory["Other"]);
        Assert.Empty(result.ToleratedDifferences);
    }

    [Theory]
    [InlineData("End OnAdd", "End")]
    [InlineData("Else if HitOnce != 1", "Else")]
    [InlineData("Begin ScriptEffectStart EnchARCHIMEDESEffect", "Begin ScriptEffectStart")]
    [InlineData("GuardRef.StopCombat Player", "GuardRef.StopCombat")]
    [InlineData("RemoveScriptPackage FollowPackage", "RemoveScriptPackage")]
    public void CompareScripts_KnownCompilerDroppedSuffix_IsTolerated(
        string source,
        string decompiled)
    {
        var result = ScriptComparer.CompareScripts(source, decompiled, EmptyNameMap);

        Assert.Equal(0, result.TotalMismatches);
        Assert.Equal(1, result.MatchCount);
        Assert.Equal(1, result.ToleratedDifferences["DroppedParameter"]);
    }

    [Fact]
    public void CompareScripts_UnknownTrailingSourceOperand_IsNotTolerated()
    {
        const string source = "Set Count To 1 UnexpectedToken";
        const string decompiled = "Set Count To 1";

        var result = ScriptComparer.CompareScripts(source, decompiled, EmptyNameMap);

        Assert.Equal(1, result.TotalMismatches);
        Assert.Equal(1, result.MismatchesByCategory["Other"]);
    }

    [Theory]
    [InlineData("Set Value To 1.0", "Set Value To 1")]
    [InlineData("Set Value To 5e-1", "Set Value To 0.5")]
    public void CompareScripts_ExactNumericRepresentationDifference_IsTolerated(
        string source,
        string decompiled)
    {
        var result = ScriptComparer.CompareScripts(source, decompiled, EmptyNameMap);

        Assert.Equal(0, result.TotalMismatches);
        Assert.Equal(1, result.ToleratedDifferences["NumberFormat"]);
    }

    [Fact]
    public void CompareScripts_CloseButDistinctNumericValue_IsNotTolerated()
    {
        const string source = "Set Value To 0.0005";
        const string decompiled = "Set Value To 0";

        var result = ScriptComparer.CompareScripts(source, decompiled, EmptyNameMap);

        Assert.Equal(1, result.TotalMismatches);
        Assert.Equal(1, result.MismatchesByCategory["Other"]);
        Assert.Empty(result.ToleratedDifferences);
    }

    [Theory]
    [InlineData("Print \"1.0\"", "Print \"1\"")]
    [InlineData("Print \"1.0\"", "Print 1")]
    public void CompareScripts_NumericLookingStringDifference_IsNotTolerated(
        string source,
        string decompiled)
    {
        var result = ScriptComparer.CompareScripts(source, decompiled, EmptyNameMap);

        Assert.Equal(1, result.TotalMismatches);
        Assert.Equal(1, result.MismatchesByCategory["Other"]);
        Assert.Empty(result.ToleratedDifferences);
    }

    [Fact]
    public void CompareScripts_UnquotedNumberAfterQuotedText_CanStillUseExactNumberFormatting()
    {
        const string source = "ShowMessage \"Ready\" 1.0";
        const string decompiled = "ShowMessage \"Ready\" 1";

        var result = ScriptComparer.CompareScripts(source, decompiled, EmptyNameMap);

        Assert.Equal(0, result.TotalMismatches);
        Assert.Equal(1, result.ToleratedDifferences["NumberFormat"]);
    }
}