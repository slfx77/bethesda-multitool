using BethesdaMultitool.Core.Formats.Nif.Conversion;
using BethesdaMultitool.Core.Utils;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif;

/// <summary>
///     Regression tests for NifConverter.
///     Anchors behavior before partial class elimination refactoring.
/// </summary>
public class NifConverterTests
{
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

    #region Vertex colors

    [Fact]
    public void ExtractRgba_PreservesPackedRgbaChannelOrder()
    {
        var (r, g, b, a) = NifGeometryWriter.ExtractRgba([255, 128, 64, 32], 0);

        Assert.Equal(1.0f, r, 3);
        Assert.Equal(128 / 255.0f, g, 3);
        Assert.Equal(64 / 255.0f, b, 3);
        Assert.Equal(32 / 255.0f, a, 3);
    }

    #endregion

    // HalfToFloat is a BinaryUtils method; its tests live in BinaryUtilsEndianTests.

    #region Convert - Error cases

    [Fact]
    public void Convert_InvalidData_ReturnsFailure()
    {
        var result = NifConverter.Convert([0x00, 0x01, 0x02, 0x03]);

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
        data[pos] = 0x00;

        var result = NifConverter.Convert(data);

        Assert.True(result.Success);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("already little-endian", result.ErrorMessage);
        Assert.Same(data, result.OutputData);
    }

    [Fact]
    public void Convert_EmptyArray_ReturnsFailure()
    {
        var result = NifConverter.Convert([]);

        Assert.False(result.Success);
    }

    #endregion

    #region ReadUInt16BE / ReadInt32BE

    [Fact]
    public void ReadUInt16BE_CorrectlyReadsBigEndian()
    {
        byte[] data = [0x12, 0x34, 0x00, 0x00];
        Assert.Equal((ushort)0x1234, BinaryUtils.ReadUInt16BE(data));
    }

    [Fact]
    public void ReadInt32BE_CorrectlyReadsBigEndian()
    {
        byte[] data = [0x12, 0x34, 0x56, 0x78];
        Assert.Equal(0x12345678, BinaryUtils.ReadInt32BE(data));
    }

    #endregion
}