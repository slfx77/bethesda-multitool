using BethesdaMultitool.Core.Formats.Nif.Conditions;
using BethesdaMultitool.Core.Formats.Nif.Conversion;
using BethesdaMultitool.Core.Formats.Nif.Decoding;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Decoding;

/// <summary>
///     The additive strict-compile APIs on <see cref="NifConditionExpr" /> and <see cref="NifVersionExpr" />, the
///     version-bound parser, and a pin that the converter's fail-open evaluators are unchanged.
/// </summary>
public class NifStrictExpressionTests
{
    private static readonly NifVersionContext Fo3 = new() { Version = 0x14020007, UserVersion = 11, BsVersion = 34 };
    private static readonly NifVersionContext Bs26 = new() { Version = 0x14020007, UserVersion = 11, BsVersion = 26 };
    private static readonly NifVersionContext Bs21 = new() { Version = 0x14020007, UserVersion = 11, BsVersion = 21 };
    private static readonly NifVersionContext Oblivion = new() { Version = 0x14000005, UserVersion = 11, BsVersion = 11 };

    [Theory]
    [InlineData("Has Normals", "Has Normals")]
    [InlineData("Texture Count #GT# 10", "Texture Count")]
    [InlineData("Use External == 0", "Use External")]
    [InlineData("#ARG# != 0", "#ARG#")]
    [InlineData("(Has Faces) #AND# (Num Strips != 0)", "Has Faces|Num Strips")]
    [InlineData("(Has Normals) #AND# (((Data Flags #BITOR# BS Data Flags) #BITAND# 4096) != 0)",
        "BS Data Flags|Data Flags|Has Normals")]
    public void ConditionForms_CompileStrictlyAndListTheirNames(string expression, string names)
    {
        Assert.True(NifConditionExpr.TryCompileStrict(expression, NifStrictExpressionKind.Condition,
            out var compiled, out var error), error);

        Assert.Equal(names.Split('|'), compiled.ReferencedNames);
    }

    [Theory]
    [InlineData("Num Vertices", "Num Vertices")]
    [InlineData("((Data Flags #BITAND# 63) #BITOR# (BS Data Flags #BITAND# 1))", null)]
    [InlineData("#ARG#", null)]
    [InlineData("1", null)]
    public void ValueForms_CompileStrictly_AndBareNamesAreRecognized(string expression, string? singleField)
    {
        Assert.True(NifConditionExpr.TryCompileStrict(expression, NifStrictExpressionKind.Value,
            out var compiled, out var error), error);

        Assert.Equal(singleField, compiled.SingleFieldName);
    }

    [Fact]
    public void CompiledCondition_EvaluatesTheTangentGate()
    {
        const string gate = "(Has Normals) #AND# (((Data Flags #BITOR# BS Data Flags) #BITAND# 4096) != 0)";
        Assert.True(NifConditionExpr.TryCompileStrict(gate, NifStrictExpressionKind.Condition, out var compiled,
            out _));

        Assert.True(compiled.EvaluateCondition(Fields(("Has Normals", 1), ("Data Flags", 0), ("BS Data Flags", 0x1001))));
        Assert.False(compiled.EvaluateCondition(Fields(("Has Normals", 0), ("Data Flags", 0), ("BS Data Flags", 0x1001))));
        Assert.False(compiled.EvaluateCondition(Fields(("Has Normals", 1), ("Data Flags", 0), ("BS Data Flags", 0x0001))));
    }

    [Theory]
    [InlineData("Count #MUL# 2", "#MUL#")] // unsupported operator: the fail-open parser stops at it
    [InlineData("Num Pixels #MUL# Num Faces", "#MUL#")]
    [InlineData("#FLT_MAX# == 0", "#FLT_MAX#")]
    [InlineData("(Has Normals", null)]
    [InlineData("", null)]
    public void UnsupportedForms_AreRejectedWithAReason(string expression, string? mentioned)
    {
        var kind = expression.Contains("==") || expression.StartsWith('(')
            ? NifStrictExpressionKind.Condition
            : NifStrictExpressionKind.Value;

        var accepted = NifConditionExpr.TryCompileStrict(expression, kind, out _, out var error);

        Assert.False(accepted);
        Assert.False(string.IsNullOrEmpty(error));
        if (mentioned is not null)
        {
            Assert.Contains(mentioned, error);
        }
    }

    /// <summary>
    ///     This grammar has no '/' operator and reads "Data Size / Vertex Size" (an SSE-only NiSkinPartition length) as
    ///     ONE field name. Strict compile therefore accepts the text; it is the decoder's scope that rejects it, because
    ///     no definition declares that name. Pin the single-name reading so nobody assumes a division happens.
    /// </summary>
    [Fact]
    public void SlashIsNotAnOperator_TheWholeTextIsOneName()
    {
        Assert.True(NifConditionExpr.TryCompileStrict("Data Size / Vertex Size", NifStrictExpressionKind.Value,
            out var compiled, out _));

        Assert.Equal("Data Size / Vertex Size", compiled.SingleFieldName);
    }

    [Fact]
    public void FailOpenEvaluators_AreUnchanged()
    {
        // The converter's evaluators keep failing open: "#MUL#" and everything after it is dropped, an unknown
        // name reads as 0, and an unknown version macro reads as true. The strict API is additive.
        Assert.Equal(3, NifConditionExpr.EvaluateValue("Count #MUL# 2", Fields(("Count", 3))));
        Assert.False(NifConditionExpr.Evaluate("Missing Field", Fields()));
        Assert.True(NifVersionExpr.Evaluate("#NOT_A_MACRO#", Fo3));
    }

    [Fact]
    public void VersionConditions_CompileStrictlyAndEvaluate()
    {
        Assert.True(NifVersionExpr.TryCompileStrict("#BSVER# #GT# 21", out var emitMult, out _));
        Assert.True(emitMult(Bs26));
        Assert.False(emitMult(Bs21));

        Assert.True(NifVersionExpr.TryCompileStrict("#BS202#", out var bs202, out _));
        Assert.True(bs202(Fo3));
        Assert.False(bs202(Oblivion));

        Assert.True(NifVersionExpr.TryCompileStrict("!#BS202#", out var notBs202, out _));
        Assert.False(notBs202(Fo3));
        Assert.True(notBs202(Oblivion));
    }

    [Theory]
    [InlineData("#BS_NOT_A_MACRO#")]
    [InlineData("#BSVER# #GT# 21 junk")]
    [InlineData("#BSVER# #GT#")]
    [InlineData("")]
    public void VersionConditions_UnknownMacroOrTrailingText_IsRejected(string expression)
    {
        Assert.False(NifVersionExpr.TryCompileStrict(expression, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void KnownMacros_CoverTheCutOneAMacros_AndTokensAreGatheredInOrder()
    {
        foreach (var macro in new[] { "#BS202#", "#NI_BS_LTE_FO3#", "#NI_BS_LT_FO3#", "#NI_BS_LT_FO4#", "#NI_BS_LT_SSE#", "#BS_GTE_FO3#" })
        {
            Assert.True(NifVersionExpr.IsKnownMacro(macro), macro);
            Assert.Contains(macro, NifVersionExpr.KnownMacros);
        }

        Assert.False(NifVersionExpr.IsKnownMacro("#BSVER#"));
        Assert.Equal(new[] { "#VER#", "#AND#", "#NI_BS_LTE_FO3#" },
            NifVersionExpr.GatherTokens("(#VER# == 20.2.0.7) #AND# #NI_BS_LTE_FO3#").ToArray());
    }

    [Theory]
    [InlineData("20.2.0.7", 0x14020007u)]
    [InlineData("10.1.0.106", 0x0A01006Au)]
    [InlineData("3.1", 0x03010000u)]
    [InlineData("3.0", 0x03000000u)]
    [InlineData("3.03", 0x03000300u)]
    [InlineData("V20_5_0_0", 0x14050000u)]
    [InlineData("V3_3_0_13", 0x0303000Du)]
    [InlineData("V20_2_0_7__11_1", 0x14020007u)]
    public void VersionBounds_ParseEveryNifXmlSpelling(string text, uint expected)
    {
        Assert.True(NifStrictExpressions.TryParseVersion(text, out var version));
        Assert.Equal(expected, version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("20.2.0.300")]
    [InlineData("1.2.3.4.5")]
    [InlineData("V20_2_0_7_FO3")]
    public void VersionBounds_RejectMalformedText(string text)
    {
        Assert.False(NifStrictExpressions.TryParseVersion(text, out _));
    }

    /// <summary>
    ///     Why the decoder does not use the converter's version helper: it reads the V-spelling and two-part versions as
    ///     0, which would make <c>since="V20_5_0_0"</c> always true.
    /// </summary>
    [Fact]
    public void ConverterVersionHelper_ReadsShortAndIdSpellingsAsZero()
    {
        Assert.Equal(0u, NifSchemaConverter.ParseVersionString("V20_5_0_0"));
        Assert.Equal(0u, NifSchemaConverter.ParseVersionString("3.1"));
    }

    private static Dictionary<string, object> Fields(params (string Name, long Value)[] values)
    {
        return values.ToDictionary(v => v.Name, v => (object)v.Value, StringComparer.Ordinal);
    }
}
