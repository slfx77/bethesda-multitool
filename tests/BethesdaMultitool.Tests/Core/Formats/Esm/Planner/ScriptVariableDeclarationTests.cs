using BethesdaMultitool.Core.Formats.Esm.Planner;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Planner;

public sealed class ScriptVariableDeclarationTests
{
    [Fact]
    public void TryGetKind_FindsExactBlockLocalReferenceDeclaration()
    {
        const string source = """
                              scn BlockLocalScript
                              short state
                              begin GameMode
                                  ref refvar
                                  ref target
                              end
                              """;

        var found = ScriptVariableDeclarationParser.TryGetKind(
            source,
            "refvar",
            out var kind);

        Assert.True(found);
        Assert.Equal(ScriptVariableDeclarationKind.Reference, kind);
    }

    [Fact]
    public void TryGetKind_DoesNotTreatSimilarIdentifierAsExactMatch()
    {
        const string source = """
                              scn ExactIdentityScript
                              begin GameMode
                                  ref targetLong
                              end
                              """;

        Assert.False(ScriptVariableDeclarationParser.TryGetKind(
            source,
            "target",
            out _));
    }

    [Fact]
    public void TryGetKind_DuplicateDeclarationsAcrossBlocksAreAmbiguous()
    {
        const string source = """
                              scn AmbiguousScript
                              ref target
                              begin GameMode
                                  ref TARGET
                              end
                              """;

        Assert.False(ScriptVariableDeclarationParser.TryGetKind(
            source,
            "target",
            out _));
    }

    [Theory]
    [InlineData("short", "Short")]
    [InlineData("long", "Long")]
    [InlineData("int", "Int")]
    public void TryGetKind_PreservesExactIntegerKeyword(
        string keyword,
        string expectedKind)
    {
        var source = $"scn ExactIntegerScript\n{keyword} state\nBegin GameMode\nEnd";

        Assert.True(ScriptVariableDeclarationParser.TryGetKind(source, "STATE", out var kind));
        Assert.Equal(expectedKind, kind.ToString());
    }
}
