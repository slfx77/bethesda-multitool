using BethesdaMultitool.Core.Minidump;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Minidump;

public sealed class MsvcNameDemanglerTests
{
    [Theory]
    [InlineData(".?AVTESForm@@", "TESForm")]
    [InlineData(".?AVTESObjectREFR@@", "TESObjectREFR")]
    [InlineData(".?AUBSSimpleStruct@@", "BSSimpleStruct")]
    public void Demangle_UnqualifiedName_StripsThePrefix(string mangled, string expected)
    {
        Assert.Equal(expected, MsvcNameDemangler.Demangle(mangled));
    }

    /// <summary>
    ///     MSVC encodes qualified names innermost-first, so the fragments must be reversed. The
    ///     previous helper truncated at the first <c>@@</c> and returned the literal
    ///     <c>RefValueAction@Tile</c>, which matched nothing in any PDB type inventory.
    /// </summary>
    [Theory]
    [InlineData(".?AVRefValueAction@Tile@@", "Tile::RefValueAction")]
    [InlineData(".?AVInner@Outer@@", "Outer::Inner")]
    [InlineData(".?AVA@B@C@@", "C::B::A")]
    public void Demangle_NestedName_ReversesTheScopeFragments(string mangled, string expected)
    {
        Assert.Equal(expected, MsvcNameDemangler.Demangle(mangled));
    }

    /// <summary>
    ///     Template arguments are themselves mangled names full of <c>@</c> separators, so splitting
    ///     on them would produce nonsense. These keep the exact raw form the old helper produced —
    ///     the point is that nothing regresses, not that templates get decoded.
    /// </summary>
    [Fact]
    public void Demangle_TemplateName_IsLeftUndecodedRatherThanScrambled()
    {
        Assert.Equal(
            "?$AStarSearch@PAVCombatPlanNode",
            MsvcNameDemangler.Demangle(".?AV?$AStarSearch@PAVCombatPlanNode@@@@"));
    }

    /// <summary>
    ///     A bare digit fragment is a back-reference into MSVC's substitution table; reversing
    ///     around it would invent a name. Returned verbatim instead.
    /// </summary>
    [Fact]
    public void Demangle_BackReferenceFragment_IsLeftUndecoded()
    {
        Assert.Equal("Inner@0", MsvcNameDemangler.Demangle(".?AVInner@0@@"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("TESForm")]
    [InlineData(".?AXSomething@@")]
    [InlineData(".?AV")]
    [InlineData(".?AVNoTerminator")]
    public void Demangle_NonClassTypeName_ReturnsNull(string? mangled)
    {
        Assert.Null(MsvcNameDemangler.Demangle(mangled));
    }

    /// <summary>
    ///     The demangler must agree with the long-standing <see cref="RttiReader" /> helper wherever
    ///     that helper was already right, since census output is keyed on these names.
    /// </summary>
    [Theory]
    [InlineData(".?AVTESForm@@")]
    [InlineData(".?AUBSSimpleStruct@@")]
    [InlineData(".?AV?$AStarSearch@PAVCombatPlanNode@@@@")]
    public void Demangle_AgreesWithRttiReader_OnUnqualifiedAndTemplateNames(string mangled)
    {
        Assert.Equal(RttiReader.DemangleName(mangled), MsvcNameDemangler.Demangle(mangled));
    }
}
