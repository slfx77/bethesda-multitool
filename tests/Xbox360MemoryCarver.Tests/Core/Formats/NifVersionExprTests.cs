using Xbox360MemoryCarver.Core.Formats.Nif;
using Xunit;

namespace Xbox360MemoryCarver.Tests.Core.Formats;

public class NifVersionExprTests
{
    // FO3/NV context: Version 20.2.0.7, BS Version 34
    private readonly NifVersionContext _fnvContext = NifVersionContext.FalloutNV;

    // Fallout 4 context: Version 20.2.0.7, BS Version 130
    private readonly NifVersionContext _fo4Context = NifVersionContext.Fallout4;

    // Skyrim context: Version 20.2.0.7, BS Version 83
    private readonly NifVersionContext _skyrimContext = NifVersionContext.Skyrim;

    [Fact]
    public void NullOrEmpty_ReturnsTrue()
    {
        Assert.True(NifVersionExpr.Evaluate(null, _fnvContext));
        Assert.True(NifVersionExpr.Evaluate("", _fnvContext));
        Assert.True(NifVersionExpr.Evaluate("   ", _fnvContext));
    }

    [Theory]
    [InlineData("#BSVER# #GT# 26", true)] // 34 > 26
    [InlineData("#BSVER# #GT# 34", false)] // 34 > 34 = false
    [InlineData("#BSVER# #GT# 50", false)] // 34 > 50 = false
    public void BsVersion_GreaterThan(string expr, bool expected)
    {
        Assert.Equal(expected, NifVersionExpr.Evaluate(expr, _fnvContext));
    }

    [Theory]
    [InlineData("#BSVER# #GTE# 26", true)] // 34 >= 26
    [InlineData("#BSVER# #GTE# 34", true)] // 34 >= 34
    [InlineData("#BSVER# #GTE# 50", false)] // 34 >= 50 = false
    public void BsVersion_GreaterThanOrEqual(string expr, bool expected)
    {
        Assert.Equal(expected, NifVersionExpr.Evaluate(expr, _fnvContext));
    }

    [Theory]
    [InlineData("#BSVER# #LT# 50", true)] // 34 < 50
    [InlineData("#BSVER# #LT# 34", false)] // 34 < 34 = false
    [InlineData("#BSVER# #LT# 26", false)] // 34 < 26 = false
    public void BsVersion_LessThan(string expr, bool expected)
    {
        Assert.Equal(expected, NifVersionExpr.Evaluate(expr, _fnvContext));
    }

    [Theory]
    [InlineData("#BSVER# #LTE# 50", true)] // 34 <= 50
    [InlineData("#BSVER# #LTE# 34", true)] // 34 <= 34
    [InlineData("#BSVER# #LTE# 26", false)] // 34 <= 26 = false
    public void BsVersion_LessThanOrEqual(string expr, bool expected)
    {
        Assert.Equal(expected, NifVersionExpr.Evaluate(expr, _fnvContext));
    }

    [Theory]
    [InlineData("#BSVER# #EQ# 34", true)] // 34 == 34
    [InlineData("#BSVER# #EQ# 83", false)] // 34 == 83 = false
    public void BsVersion_Equal(string expr, bool expected)
    {
        Assert.Equal(expected, NifVersionExpr.Evaluate(expr, _fnvContext));
    }

    [Theory]
    [InlineData("#BSVER# #NEQ# 83", true)] // 34 != 83
    [InlineData("#BSVER# #NEQ# 34", false)] // 34 != 34 = false
    public void BsVersion_NotEqual(string expr, bool expected)
    {
        Assert.Equal(expected, NifVersionExpr.Evaluate(expr, _fnvContext));
    }

    [Theory]
    [InlineData("(#BSVER# #GT# 26)", true)]
    [InlineData("((#BSVER# #GT# 26))", true)]
    [InlineData("(((#BSVER# #GT# 26)))", true)]
    public void Parentheses_Parsed(string expr, bool expected)
    {
        Assert.Equal(expected, NifVersionExpr.Evaluate(expr, _fnvContext));
    }

    [Theory]
    [InlineData("(#BSVER# #GTE# 26) #AND# (#BSVER# #LTE# 50)", true)] // 26 <= 34 <= 50
    [InlineData("(#BSVER# #GTE# 50) #AND# (#BSVER# #LTE# 100)", false)] // 34 not in [50, 100]
    [InlineData("#BSVER# #GTE# 26 #AND# #BSVER# #LTE# 50", true)] // Without parens
    public void And_Expression(string expr, bool expected)
    {
        Assert.Equal(expected, NifVersionExpr.Evaluate(expr, _fnvContext));
    }

    [Theory]
    [InlineData("(#BSVER# #LT# 26) #OR# (#BSVER# #GT# 50)", false)] // Neither true for 34
    [InlineData("(#BSVER# #LT# 50) #OR# (#BSVER# #GT# 100)", true)] // First is true
    [InlineData("(#BSVER# #LT# 20) #OR# (#BSVER# #GT# 30)", true)] // Second is true
    public void Or_Expression(string expr, bool expected)
    {
        Assert.Equal(expected, NifVersionExpr.Evaluate(expr, _fnvContext));
    }

    [Fact]
    public void ComplexExpression_FO4Range()
    {
        // This is from actual nif.xml - checks for FO4 range
        const string expr = "((#BSVER# #GTE# 130) #AND# (#BSVER# #LTE# 159))";

        Assert.False(NifVersionExpr.Evaluate(expr, _fnvContext)); // FNV: BS 34, not in range
        Assert.True(NifVersionExpr.Evaluate(expr, _fo4Context)); // FO4: BS 130, in range
        Assert.False(NifVersionExpr.Evaluate(expr, _skyrimContext)); // Skyrim: BS 83, not in range
    }

    [Fact]
    public void FlagsCondition_BsVerGt26()
    {
        // NiAVObject.Flags is uint when #BSVER# #GT# 26
        // FNV has BS 34, so this should be true
        const string expr = "#BSVER# #GT# 26";
        Assert.True(NifVersionExpr.Evaluate(expr, _fnvContext));
    }

    [Fact]
    public void FlagsCondition_BsVerLte26()
    {
        // NiAVObject.Flags is ushort when #BSVER# #LTE# 26
        // FNV has BS 34, so this should be false
        const string expr = "#BSVER# #LTE# 26";
        Assert.False(NifVersionExpr.Evaluate(expr, _fnvContext));
    }

    [Fact]
    public void Compile_ReturnsWorkingDelegate()
    {
        const string expr = "(#BSVER# #GTE# 26) #AND# (#BSVER# #LTE# 50)";
        var compiled = NifVersionExpr.Compile(expr);

        // FNV (BS 34): 26 <= 34 <= 50 = true
        Assert.True(compiled(_fnvContext));
        // FO4 (BS 130): 130 > 50 = false
        Assert.False(compiled(_fo4Context));
        // Skyrim (BS 83): 83 > 50 = false
        Assert.False(compiled(_skyrimContext));
    }

    [Theory]
    [InlineData("#VER# #GTE# 0x14020007", true)] // 20.2.0.7 >= 20.2.0.7
    [InlineData("#VER# #LT# 0x14020007", false)] // 20.2.0.7 < 20.2.0.7 = false
    [InlineData("#VER# #GTE# 0x14000000", true)] // 20.2.0.7 >= 20.0.0.0
    public void Version_HexNumbers(string expr, bool expected)
    {
        Assert.Equal(expected, NifVersionExpr.Evaluate(expr, _fnvContext));
    }

    [Fact]
    public void InvalidExpression_ReturnsTrue()
    {
        // Invalid expressions default to true (include the field)
        Assert.True(NifVersionExpr.Evaluate("garbage", _fnvContext));
        Assert.True(NifVersionExpr.Evaluate("#UNKNOWN# #GT# 5", _fnvContext));
    }
}