using FalloutXbox360Utils.Core.Formats.Nif.Conversion;
using FalloutXbox360Utils.Core.Utils;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Nif;

/// <summary>
///     Regression tests for NifConverter.
///     Anchors behavior before partial class elimination refactoring.
/// </summary>
public class NifConverterTests
{
    #region HalfToFloat (BinaryUtils)

    [Fact]
    public void HalfToFloat_Zero_ReturnsZero()
    {
        Assert.Equal(0.0f, BinaryUtils.HalfToFloat(0x0000));
    }

    [Fact]
    public void HalfToFloat_NegativeZero_ReturnsNegativeZero()
    {
        var result = BinaryUtils.HalfToFloat(0x8000);
        Assert.True(float.IsNegativeInfinity(1.0f / result) || result == -0.0f);
    }

    [Fact]
    public void HalfToFloat_One_ReturnsOne()
    {
        // Half-precision 1.0 = 0x3C00
        var result = BinaryUtils.HalfToFloat(0x3C00);
        Assert.Equal(1.0f, result, 0.001f);
    }

    [Fact]
    public void HalfToFloat_Half_ReturnsHalf()
    {
        // Half-precision 0.5 = 0x3800
        var result = BinaryUtils.HalfToFloat(0x3800);
        Assert.Equal(0.5f, result, 0.001f);
    }

    [Fact]
    public void HalfToFloat_NegativeValue_ReturnsNegative()
    {
        // Half-precision -2.5 = 0xC100
        var result = BinaryUtils.HalfToFloat(0xC100);
        Assert.Equal(-2.5f, result, 0.01f);
    }

    [Fact]
    public void HalfToFloat_PositiveInfinity_ReturnsPositiveInfinity()
    {
        // Half-precision +Inf = 0x7C00
        Assert.Equal(float.PositiveInfinity, BinaryUtils.HalfToFloat(0x7C00));
    }

    [Fact]
    public void HalfToFloat_NegativeInfinity_ReturnsNegativeInfinity()
    {
        // Half-precision -Inf = 0xFC00
        Assert.Equal(float.NegativeInfinity, BinaryUtils.HalfToFloat(0xFC00));
    }

    [Fact]
    public void HalfToFloat_NaN_ReturnsNaN()
    {
        // Half-precision NaN = 0x7C01 (exponent all 1s, mantissa non-zero)
        Assert.True(float.IsNaN(BinaryUtils.HalfToFloat(0x7C01)));
    }

    [Fact]
    public void HalfToFloat_Denormalized_ReturnsSmallValue()
    {
        // Half-precision denormalized: 0x0001 = smallest positive subnormal
        var result = BinaryUtils.HalfToFloat(0x0001);
        Assert.True(result > 0.0f && result < 0.001f);
    }

    #endregion

    #region Convert - Error cases

    [Fact]
    public void Convert_InvalidData_ReturnsFailure()
    {
        var converter = new NifConverter();
        var result = converter.Convert([0x00, 0x01, 0x02, 0x03]);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public void Convert_AlreadyLittleEndian_ReturnsInputData()
    {
        // Build a minimal valid LE NIF header
        var header = "Gamebryo File Format, Version 20.2.0.7\n"u8.ToArray();
        var data = new byte[200];
        Array.Copy(header, data, header.Length);

        var pos = header.Length;
        // Binary version: 0x14020007 (little-endian)
        data[pos++] = 0x07;
        data[pos++] = 0x00;
        data[pos++] = 0x02;
        data[pos++] = 0x14;
        // Endian byte: 1 = little-endian
        data[pos++] = 0x01;
        // User version: 12 (LE)
        data[pos++] = 0x0C;
        data[pos++] = 0x00;
        data[pos++] = 0x00;
        data[pos++] = 0x00;
        // Num blocks: 0 (LE)
        data[pos++] = 0x00;
        data[pos++] = 0x00;
        data[pos++] = 0x00;
        data[pos++] = 0x00;

        var converter = new NifConverter();
        var result = converter.Convert(data);

        Assert.True(result.Success);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("already little-endian", result.ErrorMessage);
        Assert.Same(data, result.OutputData);
    }

    [Fact]
    public void Convert_EmptyArray_ReturnsFailure()
    {
        var converter = new NifConverter();
        var result = converter.Convert([]);

        Assert.False(result.Success);
    }

    #endregion

    #region IsNodeType

    [Theory]
    [InlineData("NiNode", true)]
    [InlineData("BSFadeNode", true)]
    [InlineData("BSLeafAnimNode", true)]
    [InlineData("BSTreeNode", true)]
    [InlineData("BSOrderedNode", true)]
    [InlineData("BSMultiBoundNode", true)]
    [InlineData("BSMasterParticleSystem", true)]
    [InlineData("NiSwitchNode", true)]
    [InlineData("NiBillboardNode", true)]
    [InlineData("NiLODNode", true)]
    [InlineData("BSBlastNode", true)]
    [InlineData("BSDamageStage", true)]
    [InlineData("NiAVObject", true)]
    [InlineData("NiTriShape", false)]
    [InlineData("NiTriStrips", false)]
    [InlineData("NiSkinPartition", false)]
    [InlineData("", false)]
    public void IsNodeType_ReturnsExpected(string typeName, bool expected)
    {
        Assert.Equal(expected, NifConverter.IsNodeType(typeName));
    }

    #endregion

    #region ReadUInt16BE / ReadInt32BE

    [Fact]
    public void ReadUInt16BE_CorrectlyReadsBigEndian()
    {
        byte[] data = [0x12, 0x34, 0x00, 0x00];
        Assert.Equal((ushort)0x1234, BinaryUtils.ReadUInt16BE(data, 0));
    }

    [Fact]
    public void ReadInt32BE_CorrectlyReadsBigEndian()
    {
        byte[] data = [0x12, 0x34, 0x56, 0x78];
        Assert.Equal(0x12345678, BinaryUtils.ReadInt32BE(data, 0));
    }

    #endregion
}
