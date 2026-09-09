using BethesdaMultitool.Core.Formats.Fallout;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Fallout;

/// <summary>
///     Vectors for Fallout's plain-text <c>.GAM</c> global-variable declarations, shaped after the
///     36 shipped files measured 2026-09-06.
/// </summary>
public sealed class FalloutGameVariablesTests
{
    [Fact]
    public void Parse_ReadsDeclarationsInOrderWithTheirComments()
    {
        const string text = """
                            MAP_GLOBAL_VARS:
                            //MAP VAR                     NUMBER

                            Only_Once            := 0;    // (0)
                            Guard_Dead           := 1;    // (1)  // the guard died
                            """;

        var vars = FalloutGameVariables.Parse(text, "CAVES.GAM");

        Assert.Equal(FalloutVariableScope.Map, vars.Scope);
        Assert.Equal(["Only_Once", "Guard_Dead"], vars.Variables.Select(v => v.Name));
        Assert.Equal([0, 1], vars.Variables.Select(v => v.Index));
        Assert.Equal([0, 1], vars.Variables.Select(v => v.Value));
        Assert.Contains("the guard died", vars.Variables[1].Comment, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_AcceptsADeclarationWithNoTerminatingSemicolon()
    {
        // ⚠⚠ FIVE retail declarations omit the semicolon. Requiring it DROPS them and shifts every
        // later index by one — 46 wrong indices in VAULT13.GAM alone. This is the regression.
        const string text = """
                            GAME_GLOBAL_VARS:
                            GOOD_MONSTER              :=0;       //  (0)
                            BAD_MONSTER               :=0        //  (1)
                            ARTIFACT_DISK             :=0;       //  (2)
                            """;

        var vars = FalloutGameVariables.Parse(text, "VAULT13.GAM");

        Assert.Equal(3, vars.Variables.Count);
        Assert.Equal("BAD_MONSTER", vars.Variables[1].Name);

        // The index the authors wrote in each comment must equal the declaration position.
        foreach (var v in vars.Variables)
        {
            Assert.Contains($"({v.Index})", v.Comment, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Parse_TakesTheScopeFromAnUncommentedHeaderOnly()
    {
        // ⚠ VAULT13.GAM carries a commented-out "//MAP_GLOBAL_VARS:" above its real header.
        const string text = """
                            //MAP_GLOBAL_VARS:
                            GAME_GLOBAL_VARS:
                            CHILD_KILLER_SHADY        :=0;       //  (0)
                            """;

        Assert.Equal(FalloutVariableScope.Game, FalloutGameVariables.Parse(text, "VAULT13.GAM").Scope);
    }

    [Fact]
    public void Parse_IgnoresCommentaryAndBlankLines()
    {
        const string text = """

                             // Comments

                            MAP_GLOBAL_VARS:
                            // not_a_var := 5;
                            Real_Var := 7;   // (0)
                            """;

        var vars = FalloutGameVariables.Parse(text, "T.GAM");

        Assert.Single(vars.Variables);
        Assert.Equal("Real_Var", vars.Variables[0].Name);
        Assert.Equal(7, vars.Variables[0].Value);
    }

    [Fact]
    public void Parse_ReadsNegativeValues()
    {
        var vars = FalloutGameVariables.Parse("MAP_GLOBAL_VARS:\nX := -1;\n", "T.GAM");

        Assert.Equal(-1, vars.Variables[0].Value);
    }

    [Fact]
    public void IsGameVariableFile_NeedsASectionHeader()
    {
        Assert.True(FalloutGameVariables.IsGameVariableFile("MAP_GLOBAL_VARS:\nA := 1;\n"));
        Assert.False(FalloutGameVariables.IsGameVariableFile("A := 1;\n"));
    }
}