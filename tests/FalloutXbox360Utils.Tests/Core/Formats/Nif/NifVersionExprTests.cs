using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Nif;

public class NifVersionExprTests
{
    [Fact]
    public void BS_GT_FO3_ReturnsFalse_ForFalloutNV()
    {
        // BS version 34 is NOT greater than 34
        var ctx = NifVersionContext.FalloutNV;
        Assert.Equal(34, ctx.BsVersion);

        var result = NifVersionExpr.Evaluate("#BS_GT_FO3#", ctx);
        Assert.False(result, "#BS_GT_FO3# should return false for FO3/NV (BS version 34)");
    }

    [Fact]
    public void BSVER_GT_34_ReturnsFalse_ForBsVersion34()
    {
        var ctx = NifVersionContext.FalloutNV;

        var result = NifVersionExpr.Evaluate("(#BSVER# #GT# 34)", ctx);
        Assert.False(result, "#BSVER# > 34 should return false when BsVersion is 34");
    }

    [Fact]
    public void BS_SSE_ReturnsFalse_ForFalloutNV()
    {
        // BS version 34 != 100
        var ctx = NifVersionContext.FalloutNV;

        var result = NifVersionExpr.Evaluate("#BS_SSE#", ctx);
        Assert.False(result, "#BS_SSE# should return false for FO3/NV");
    }

    [Fact]
    public void BS_GT_FO3_ReturnsTrue_ForSkyrim()
    {
        // BS version 83 > 34
        var ctx = NifVersionContext.Skyrim;
        Assert.Equal(83, ctx.BsVersion);

        var result = NifVersionExpr.Evaluate("#BS_GT_FO3#", ctx);
        Assert.True(result, "#BS_GT_FO3# should return true for Skyrim (BS version 83)");
    }

    [Fact]
    public void NI_BS_LTE_FO3_ReturnsTrue_ForFalloutNV()
    {
        // BS version 34 <= 34
        var ctx = NifVersionContext.FalloutNV;

        var result = NifVersionExpr.Evaluate("#NI_BS_LTE_FO3#", ctx);
        Assert.True(result, "#NI_BS_LTE_FO3# should return true for FO3/NV (BS version 34)");
    }
}