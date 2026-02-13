using FalloutXbox360Utils.Core.Utils;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Utils;

/// <summary>
///     Tests for BinaryUtils endian-flag overloads, new explicit methods, and HalfToFloat.
/// </summary>
public class BinaryUtilsEndianTests
{
    // Test data: 0x12 0x34 0x56 0x78 0x9A 0xBC 0xDE 0xF0
    private static readonly byte[] TestData = [0x12, 0x34, 0x56, 0x78, 0x9A, 0xBC, 0xDE, 0xF0];

    #region ReadUInt16 endian-flag

    [Fact]
    public void ReadUInt16_BigEndian_MatchesExplicit()
    {
        ReadOnlySpan<byte> span = TestData;
        Assert.Equal(BinaryUtils.ReadUInt16BE(span, 0), BinaryUtils.ReadUInt16(span, 0, bigEndian: true));
        Assert.Equal(BinaryUtils.ReadUInt16BE(span, 2), BinaryUtils.ReadUInt16(span, 2, bigEndian: true));
    }

    [Fact]
    public void ReadUInt16_LittleEndian_MatchesExplicit()
    {
        ReadOnlySpan<byte> span = TestData;
        Assert.Equal(BinaryUtils.ReadUInt16LE(span, 0), BinaryUtils.ReadUInt16(span, 0, bigEndian: false));
        Assert.Equal(BinaryUtils.ReadUInt16LE(span, 2), BinaryUtils.ReadUInt16(span, 2, bigEndian: false));
    }

    [Fact]
    public void ReadUInt16_ByteArray_MatchesSpan()
    {
        Assert.Equal(
            BinaryUtils.ReadUInt16((ReadOnlySpan<byte>)TestData, 0, true),
            BinaryUtils.ReadUInt16(TestData, 0, true));
        Assert.Equal(
            BinaryUtils.ReadUInt16((ReadOnlySpan<byte>)TestData, 0, false),
            BinaryUtils.ReadUInt16(TestData, 0, false));
    }

    #endregion

    #region ReadInt16 endian-flag

    [Fact]
    public void ReadInt16BE_ReadsCorrectly()
    {
        // 0x12, 0x34 = 0x1234 = 4660
        Assert.Equal((short)0x1234, BinaryUtils.ReadInt16BE(TestData));
    }

    [Fact]
    public void ReadInt16LE_ReadsCorrectly()
    {
        // 0x12, 0x34 LE = 0x3412 = 13330
        Assert.Equal((short)0x3412, BinaryUtils.ReadInt16LE(TestData));
    }

    [Fact]
    public void ReadInt16_BigEndian_MatchesExplicit()
    {
        ReadOnlySpan<byte> span = TestData;
        Assert.Equal(BinaryUtils.ReadInt16BE(span, 0), BinaryUtils.ReadInt16(span, 0, bigEndian: true));
    }

    [Fact]
    public void ReadInt16_LittleEndian_MatchesExplicit()
    {
        ReadOnlySpan<byte> span = TestData;
        Assert.Equal(BinaryUtils.ReadInt16LE(span, 0), BinaryUtils.ReadInt16(span, 0, bigEndian: false));
    }

    [Fact]
    public void ReadInt16_ByteArray_MatchesSpan()
    {
        Assert.Equal(
            BinaryUtils.ReadInt16((ReadOnlySpan<byte>)TestData, 0, true),
            BinaryUtils.ReadInt16(TestData, 0, true));
    }

    #endregion

    #region ReadUInt32 endian-flag

    [Fact]
    public void ReadUInt32_BigEndian_MatchesExplicit()
    {
        ReadOnlySpan<byte> span = TestData;
        Assert.Equal(BinaryUtils.ReadUInt32BE(span, 0), BinaryUtils.ReadUInt32(span, 0, bigEndian: true));
    }

    [Fact]
    public void ReadUInt32_LittleEndian_MatchesExplicit()
    {
        ReadOnlySpan<byte> span = TestData;
        Assert.Equal(BinaryUtils.ReadUInt32LE(span, 0), BinaryUtils.ReadUInt32(span, 0, bigEndian: false));
    }

    [Fact]
    public void ReadUInt32_ByteArray_MatchesSpan()
    {
        Assert.Equal(
            BinaryUtils.ReadUInt32((ReadOnlySpan<byte>)TestData, 0, true),
            BinaryUtils.ReadUInt32(TestData, 0, true));
    }

    #endregion

    #region ReadInt32 endian-flag

    [Fact]
    public void ReadInt32BE_ReadsCorrectly()
    {
        // 0x12345678 = 305419896
        Assert.Equal(0x12345678, BinaryUtils.ReadInt32BE(TestData));
    }

    [Fact]
    public void ReadInt32_BigEndian_MatchesExplicit()
    {
        ReadOnlySpan<byte> span = TestData;
        Assert.Equal(BinaryUtils.ReadInt32BE(span, 0), BinaryUtils.ReadInt32(span, 0, bigEndian: true));
    }

    [Fact]
    public void ReadInt32_LittleEndian_MatchesExplicit()
    {
        ReadOnlySpan<byte> span = TestData;
        Assert.Equal(BinaryUtils.ReadInt32LE(span, 0), BinaryUtils.ReadInt32(span, 0, bigEndian: false));
    }

    [Fact]
    public void ReadInt32_ByteArray_MatchesSpan()
    {
        Assert.Equal(
            BinaryUtils.ReadInt32((ReadOnlySpan<byte>)TestData, 0, true),
            BinaryUtils.ReadInt32(TestData, 0, true));
    }

    #endregion

    #region ReadUInt64 endian-flag

    [Fact]
    public void ReadUInt64_BigEndian_MatchesExplicit()
    {
        ReadOnlySpan<byte> span = TestData;
        Assert.Equal(BinaryUtils.ReadUInt64BE(span, 0), BinaryUtils.ReadUInt64(span, 0, bigEndian: true));
    }

    [Fact]
    public void ReadUInt64_LittleEndian_MatchesExplicit()
    {
        ReadOnlySpan<byte> span = TestData;
        Assert.Equal(BinaryUtils.ReadUInt64LE(span, 0), BinaryUtils.ReadUInt64(span, 0, bigEndian: false));
    }

    [Fact]
    public void ReadUInt64_ByteArray_MatchesSpan()
    {
        Assert.Equal(
            BinaryUtils.ReadUInt64((ReadOnlySpan<byte>)TestData, 0, true),
            BinaryUtils.ReadUInt64(TestData, 0, true));
    }

    #endregion

    #region ReadInt64 endian-flag

    [Fact]
    public void ReadInt64BE_ReadsCorrectly()
    {
        // 0x123456789ABCDEF0
        Assert.Equal(0x123456789ABCDEF0L, BinaryUtils.ReadInt64BE(TestData));
    }

    [Fact]
    public void ReadInt64LE_ReadsCorrectly()
    {
        // little-endian read of same bytes
        Assert.Equal(unchecked((long)0xF0DEBC9A78563412UL), BinaryUtils.ReadInt64LE(TestData));
    }

    [Fact]
    public void ReadInt64_BigEndian_MatchesExplicit()
    {
        ReadOnlySpan<byte> span = TestData;
        Assert.Equal(BinaryUtils.ReadInt64BE(span, 0), BinaryUtils.ReadInt64(span, 0, bigEndian: true));
    }

    [Fact]
    public void ReadInt64_LittleEndian_MatchesExplicit()
    {
        ReadOnlySpan<byte> span = TestData;
        Assert.Equal(BinaryUtils.ReadInt64LE(span, 0), BinaryUtils.ReadInt64(span, 0, bigEndian: false));
    }

    #endregion

    #region ReadFloat endian-flag

    [Fact]
    public void ReadFloat_BigEndian_MatchesExplicit()
    {
        ReadOnlySpan<byte> span = TestData;
        Assert.Equal(BinaryUtils.ReadFloatBE(span, 0), BinaryUtils.ReadFloat(span, 0, bigEndian: true));
    }

    [Fact]
    public void ReadFloat_LittleEndian_MatchesExplicit()
    {
        ReadOnlySpan<byte> span = TestData;
        Assert.Equal(BinaryUtils.ReadFloatLE(span, 0), BinaryUtils.ReadFloat(span, 0, bigEndian: false));
    }

    [Fact]
    public void ReadFloat_ByteArray_MatchesSpan()
    {
        Assert.Equal(
            BinaryUtils.ReadFloat((ReadOnlySpan<byte>)TestData, 0, true),
            BinaryUtils.ReadFloat(TestData, 0, true));
    }

    #endregion

    #region ReadDouble endian-flag

    [Fact]
    public void ReadDoubleBE_ReadsCorrectly()
    {
        // IEEE 754 double from 0x123456789ABCDEF0
        var expected = BitConverter.Int64BitsToDouble(0x123456789ABCDEF0L);
        Assert.Equal(expected, BinaryUtils.ReadDoubleBE(TestData));
    }

    [Fact]
    public void ReadDoubleLE_ReadsCorrectly()
    {
        var expected = BitConverter.Int64BitsToDouble(unchecked((long)0xF0DEBC9A78563412UL));
        Assert.Equal(expected, BinaryUtils.ReadDoubleLE(TestData));
    }

    [Fact]
    public void ReadDouble_BigEndian_MatchesExplicit()
    {
        ReadOnlySpan<byte> span = TestData;
        Assert.Equal(BinaryUtils.ReadDoubleBE(span, 0), BinaryUtils.ReadDouble(span, 0, bigEndian: true));
    }

    [Fact]
    public void ReadDouble_LittleEndian_MatchesExplicit()
    {
        ReadOnlySpan<byte> span = TestData;
        Assert.Equal(BinaryUtils.ReadDoubleLE(span, 0), BinaryUtils.ReadDouble(span, 0, bigEndian: false));
    }

    #endregion

    #region HalfToFloat

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
        Assert.Equal(1.0f, BinaryUtils.HalfToFloat(0x3C00));
    }

    [Fact]
    public void HalfToFloat_PositiveInfinity()
    {
        Assert.Equal(float.PositiveInfinity, BinaryUtils.HalfToFloat(0x7C00));
    }

    [Fact]
    public void HalfToFloat_NegativeInfinity()
    {
        Assert.Equal(float.NegativeInfinity, BinaryUtils.HalfToFloat(0xFC00));
    }

    [Fact]
    public void HalfToFloat_NaN()
    {
        Assert.True(float.IsNaN(BinaryUtils.HalfToFloat(0x7C01)));
    }

    [Theory]
    [InlineData(0x3C00, 1.0f)]
    [InlineData(0x3800, 0.5f)]
    [InlineData(0xC100, -2.5f)]
    public void HalfToFloat_KnownValues(ushort input, float expected)
    {
        Assert.Equal(expected, BinaryUtils.HalfToFloat(input), 0.001f);
    }

    #endregion

    #region Offset reads at non-zero positions

    [Fact]
    public void ReadUInt16BE_AtOffset_ReadsCorrectBytes()
    {
        // At offset 2: 0x56, 0x78 = 0x5678
        Assert.Equal((ushort)0x5678, BinaryUtils.ReadUInt16BE(TestData, 2));
    }

    [Fact]
    public void ReadUInt32BE_AtOffset_ReadsCorrectBytes()
    {
        // At offset 4: 0x9A, 0xBC, 0xDE, 0xF0 = 0x9ABCDEF0
        Assert.Equal(0x9ABCDEF0u, BinaryUtils.ReadUInt32BE(TestData, 4));
    }

    #endregion
}
