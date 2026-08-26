using BethesdaMultitool.Core.Utils;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Utils;

/// <summary>
///     Contracts for the <see cref="BinaryUtils" /> integer/float readers.
///     <para>
///         Every reader family exposes the same four entry points: an explicit <c>*BE</c>, an
///         explicit <c>*LE</c>, a <c>bigEndian</c>-flag span overload, and a <c>byte[]</c>
///         overload. Rather than one <c>[Fact]</c> per (family × entry point) — 8 families × 3
///         checks of near-identical body — the families are a data table and each contract is
///         asserted once.
///     </para>
///     <para>
///         The endian-agreement checks deliberately are <em>not</em> the only coverage. Asserting
///         only that <c>ReadUInt16(span, 0, true) == ReadUInt16BE(span)</c> compares one
///         production method against another: if both are wrong the test still passes. So
///         <see cref="ExplicitReaders_DecodeTheDocumentedValue" /> pins every explicit reader
///         against a hand-computed constant first, and the agreement theories then establish that
///         the convenience overloads dispatch to those pinned readers.
///     </para>
/// </summary>
public class BinaryUtilsEndianTests
{
    /// <summary>
    ///     A deliberately asymmetric byte run, so a big-endian read can never coincidentally equal
    ///     its little-endian counterpart at any width.
    /// </summary>
    private static readonly byte[] TestData = [0x12, 0x34, 0x56, 0x78, 0x9A, 0xBC, 0xDE, 0xF0];

    /// <summary>
    ///     One row per integer reader family. Expected values are the bytes of
    ///     <see cref="TestData" /> read by hand at each width; results are widened to
    ///     <see cref="ulong" /> so all six families share one table (widening is lossless and
    ///     order-preserving for equality).
    /// </summary>
    public static TheoryData<IntegerReaderFamily> IntegerReaders => new()
    {
        new IntegerReaderFamily("ReadUInt16", 2,
            (d, o) => BinaryUtils.ReadUInt16BE(d, o),
            (d, o) => BinaryUtils.ReadUInt16LE(d, o),
            (d, o, be) => BinaryUtils.ReadUInt16(d.AsSpan(), o, be),
            (d, o, be) => BinaryUtils.ReadUInt16(d, o, be),
            ExpectedBigEndian: 0x1234,
            ExpectedLittleEndian: 0x3412),
        new IntegerReaderFamily("ReadInt16", 2,
            (d, o) => unchecked((ushort)BinaryUtils.ReadInt16BE(d, o)),
            (d, o) => unchecked((ushort)BinaryUtils.ReadInt16LE(d, o)),
            (d, o, be) => unchecked((ushort)BinaryUtils.ReadInt16(d.AsSpan(), o, be)),
            (d, o, be) => unchecked((ushort)BinaryUtils.ReadInt16(d, o, be)),
            ExpectedBigEndian: 0x1234,
            ExpectedLittleEndian: 0x3412),
        new IntegerReaderFamily("ReadUInt32", 4,
            (d, o) => BinaryUtils.ReadUInt32BE(d, o),
            (d, o) => BinaryUtils.ReadUInt32LE(d, o),
            (d, o, be) => BinaryUtils.ReadUInt32(d.AsSpan(), o, be),
            (d, o, be) => BinaryUtils.ReadUInt32(d, o, be),
            ExpectedBigEndian: 0x12345678,
            ExpectedLittleEndian: 0x78563412),
        new IntegerReaderFamily("ReadInt32", 4,
            (d, o) => unchecked((uint)BinaryUtils.ReadInt32BE(d, o)),
            (d, o) => unchecked((uint)BinaryUtils.ReadInt32LE(d, o)),
            (d, o, be) => unchecked((uint)BinaryUtils.ReadInt32(d.AsSpan(), o, be)),
            (d, o, be) => unchecked((uint)BinaryUtils.ReadInt32(d, o, be)),
            ExpectedBigEndian: 0x12345678,
            ExpectedLittleEndian: 0x78563412),
        new IntegerReaderFamily("ReadUInt64", 8,
            (d, o) => BinaryUtils.ReadUInt64BE(d, o),
            (d, o) => BinaryUtils.ReadUInt64LE(d, o),
            (d, o, be) => BinaryUtils.ReadUInt64(d.AsSpan(), o, be),
            (d, o, be) => BinaryUtils.ReadUInt64(d, o, be),
            ExpectedBigEndian: 0x123456789ABCDEF0,
            ExpectedLittleEndian: 0xF0DEBC9A78563412),
        new IntegerReaderFamily("ReadInt64", 8,
            (d, o) => unchecked((ulong)BinaryUtils.ReadInt64BE(d, o)),
            (d, o) => unchecked((ulong)BinaryUtils.ReadInt64LE(d, o)),
            (d, o, be) => unchecked((ulong)BinaryUtils.ReadInt64(d.AsSpan(), o, be)),
            (d, o, be) => unchecked((ulong)BinaryUtils.ReadInt64(d, o, be)),
            ExpectedBigEndian: 0x123456789ABCDEF0,
            ExpectedLittleEndian: 0xF0DEBC9A78563412)
    };

    /// <summary>
    ///     The IEEE-754 families. Expected values are derived from the same bytes via
    ///     <see cref="BitConverter" />, which is an independent decoder — not the code under test.
    /// </summary>
    public static TheoryData<FloatReaderFamily> FloatReaders => new()
    {
        new FloatReaderFamily("ReadFloat",
            (d, o) => BinaryUtils.ReadFloatBE(d, o),
            (d, o) => BinaryUtils.ReadFloatLE(d, o),
            (d, o, be) => BinaryUtils.ReadFloat(d.AsSpan(), o, be),
            (d, o, be) => BinaryUtils.ReadFloat(d, o, be),
            ExpectedBigEndian: BitConverter.Int32BitsToSingle(0x12345678),
            ExpectedLittleEndian: BitConverter.Int32BitsToSingle(0x78563412)),
        new FloatReaderFamily("ReadDouble",
            (d, o) => BinaryUtils.ReadDoubleBE(d, o),
            (d, o) => BinaryUtils.ReadDoubleLE(d, o),
            (d, o, be) => BinaryUtils.ReadDouble(d.AsSpan(), o, be),
            (d, o, be) => BinaryUtils.ReadDouble(d, o, be),
            ExpectedBigEndian: BitConverter.Int64BitsToDouble(0x123456789ABCDEF0L),
            ExpectedLittleEndian: BitConverter.Int64BitsToDouble(unchecked((long)0xF0DEBC9A78563412UL)))
    };

    public static TheoryData<ushort, float, string> HalfToFloatCases => new()
    {
        { 0x0000, 0.0f, "positive zero" },
        { 0x3C00, 1.0f, "one" },
        { 0x3800, 0.5f, "one half" },
        { 0xC100, -2.5f, "negative, exponent below bias" },
        { 0x7C00, float.PositiveInfinity, "exponent all ones, zero mantissa" },
        { 0xFC00, float.NegativeInfinity, "sign set, exponent all ones, zero mantissa" }
    };

    // ---------------------------------------------------------------------------------------
    // The oracle: explicit readers must decode a hand-computed value, not merely agree with
    // each other. Every other theory below builds on this one.
    // ---------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(IntegerReaders))]
    public void ExplicitReaders_DecodeTheDocumentedValue(IntegerReaderFamily family)
    {
        Assert.Equal(family.ExpectedBigEndian, family.ExplicitBigEndian(TestData, 0));
        Assert.Equal(family.ExpectedLittleEndian, family.ExplicitLittleEndian(TestData, 0));
    }

    [Theory]
    [MemberData(nameof(IntegerReaders))]
    public void EndianFlagOverload_SelectsTheMatchingExplicitReader(IntegerReaderFamily family)
    {
        Assert.Equal(family.ExpectedBigEndian, family.Flagged(TestData, 0, true));
        Assert.Equal(family.ExpectedLittleEndian, family.Flagged(TestData, 0, false));
    }

    [Theory]
    [MemberData(nameof(IntegerReaders))]
    public void ByteArrayOverload_MatchesTheSpanOverload(IntegerReaderFamily family)
    {
        Assert.Equal(family.ExpectedBigEndian, family.FlaggedArray(TestData, 0, true));
        Assert.Equal(family.ExpectedLittleEndian, family.FlaggedArray(TestData, 0, false));
    }

    /// <summary>
    ///     A reader must decode the bytes <em>at the requested offset</em>, not silently read from
    ///     zero. Reading one width in from the start of <see cref="TestData" /> gives a value that
    ///     shares no bytes with the offset-zero read, so an ignored offset cannot pass.
    /// </summary>
    [Theory]
    [MemberData(nameof(IntegerReaders))]
    public void ExplicitReaders_HonourTheOffset(IntegerReaderFamily family)
    {
        var offset = family.WidthInBytes;

        // 64-bit families consume the whole fixture, so there is no second window to read from.
        // Report that as SKIPPED rather than returning early — a silent `return` is recorded as a
        // pass, which would claim offset coverage this fixture cannot provide.
        Assert.SkipWhen(offset + family.WidthInBytes > TestData.Length,
            $"{family.Name} is {family.WidthInBytes} bytes wide; the {TestData.Length}-byte fixture "
            + "holds only one window.");

        var expectedBigEndian = ExpectedAt(TestData, offset, family.WidthInBytes, bigEndian: true);
        var expectedLittleEndian = ExpectedAt(TestData, offset, family.WidthInBytes, bigEndian: false);

        Assert.Equal(expectedBigEndian, family.ExplicitBigEndian(TestData, offset));
        Assert.Equal(expectedLittleEndian, family.ExplicitLittleEndian(TestData, offset));
    }

    [Theory]
    [MemberData(nameof(FloatReaders))]
    public void ExplicitFloatReaders_DecodeTheDocumentedValue(FloatReaderFamily family)
    {
        Assert.Equal(family.ExpectedBigEndian, family.ExplicitBigEndian(TestData, 0));
        Assert.Equal(family.ExpectedLittleEndian, family.ExplicitLittleEndian(TestData, 0));
    }

    [Theory]
    [MemberData(nameof(FloatReaders))]
    public void FloatEndianFlagAndArrayOverloads_MatchTheExplicitReaders(FloatReaderFamily family)
    {
        Assert.Equal(family.ExpectedBigEndian, family.Flagged(TestData, 0, true));
        Assert.Equal(family.ExpectedLittleEndian, family.Flagged(TestData, 0, false));
        Assert.Equal(family.ExpectedBigEndian, family.FlaggedArray(TestData, 0, true));
        Assert.Equal(family.ExpectedLittleEndian, family.FlaggedArray(TestData, 0, false));
    }

    [Theory]
    [MemberData(nameof(HalfToFloatCases))]
    public void HalfToFloat_DecodesKnownEncodings(ushort input, float expected, string because)
    {
        _ = because; // Surfaces the equivalence class in the test-case display name.

        Assert.Equal(expected, BinaryUtils.HalfToFloat(input));
    }

    /// <summary>
    ///     Negative zero compares equal to positive zero, so it needs a sign-aware probe:
    ///     1/-0 is negative infinity, 1/+0 is positive infinity.
    /// </summary>
    [Fact]
    public void HalfToFloat_NegativeZero_KeepsTheSignBit()
    {
        var result = BinaryUtils.HalfToFloat(0x8000);

        Assert.Equal(0.0f, result);
        Assert.True(float.IsNegativeInfinity(1.0f / result));
    }

    [Fact]
    public void HalfToFloat_NonZeroMantissaWithMaxExponent_IsNaN()
    {
        Assert.True(float.IsNaN(BinaryUtils.HalfToFloat(0x7C01)));
    }

    /// <summary>
    ///     0x0001 is the smallest positive subnormal half (2^-24). Decoding subnormals through the
    ///     normalized path would yield zero or a wildly wrong magnitude.
    /// </summary>
    [Fact]
    public void HalfToFloat_SmallestSubnormal_DecodesToTwoToTheMinusTwentyFour()
    {
        Assert.Equal(MathF.Pow(2f, -24f), BinaryUtils.HalfToFloat(0x0001));
    }

    /// <summary>Decodes a big- or little-endian unsigned integer independently of BinaryUtils.</summary>
    private static ulong ExpectedAt(byte[] data, int offset, int width, bool bigEndian)
    {
        ulong value = 0;
        for (var i = 0; i < width; i++)
        {
            var b = data[offset + (bigEndian ? i : width - 1 - i)];
            value = (value << 8) | b;
        }

        return value;
    }

    /// <summary>An integer reader family and the value it must produce from <see cref="TestData" />.</summary>
    public sealed record IntegerReaderFamily(
        string Name,
        int WidthInBytes,
        Func<byte[], int, ulong> ExplicitBigEndian,
        Func<byte[], int, ulong> ExplicitLittleEndian,
        Func<byte[], int, bool, ulong> Flagged,
        Func<byte[], int, bool, ulong> FlaggedArray,
        ulong ExpectedBigEndian,
        ulong ExpectedLittleEndian)
    {
        public override string ToString()
        {
            return Name;
        }
    }

    /// <summary>An IEEE-754 reader family and the value it must produce from <see cref="TestData" />.</summary>
    public sealed record FloatReaderFamily(
        string Name,
        Func<byte[], int, double> ExplicitBigEndian,
        Func<byte[], int, double> ExplicitLittleEndian,
        Func<byte[], int, bool, double> Flagged,
        Func<byte[], int, bool, double> FlaggedArray,
        double ExpectedBigEndian,
        double ExpectedLittleEndian)
    {
        public override string ToString()
        {
            return Name;
        }
    }
}
