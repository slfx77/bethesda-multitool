using FalloutXbox360Utils.Core.Formats.Esm;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Records;

/// <summary>
///     Tests for <see cref="RecordValidator" /> covering signature validation,
///     false-positive detection, header validation, and ASCII checks.
/// </summary>
public class RecordValidatorTests
{
    #region IsValidRecordSignature

    [Theory]
    [InlineData("NPC_")]
    [InlineData("REFR")]
    [InlineData("ACHR")]
    [InlineData("WEAP")]
    [InlineData("ALCH")]
    [InlineData("TES4")]
    [InlineData("GRUP")]
    public void IsValidRecordSignature_KnownTypes_ReturnsTrue(string sig)
    {
        Assert.True(RecordValidator.IsValidRecordSignature(sig));
    }

    [Theory]
    [InlineData("ZZZZ")]   // Unknown but valid uppercase 4-char
    [InlineData("ABCD")]
    [InlineData("XX__")]   // Uppercase + underscore
    public void IsValidRecordSignature_UnknownButValidFormat_ReturnsTrue(string sig)
    {
        Assert.True(RecordValidator.IsValidRecordSignature(sig));
    }

    [Theory]
    [InlineData("")]        // Empty
    [InlineData("AB")]      // Too short
    [InlineData("ABCDE")]   // Too long
    [InlineData("npc_")]    // Lowercase
    [InlineData("1234")]    // Digits (not uppercase letters or underscore)
    [InlineData("Npc_")]    // Mixed case
    [InlineData("AB C")]    // Space
    public void IsValidRecordSignature_Invalid_ReturnsFalse(string sig)
    {
        Assert.False(RecordValidator.IsValidRecordSignature(sig));
    }

    #endregion

    #region IsKnownFalsePositive

    public static TheoryData<byte[], int, string> KnownFalsePositiveTrueCases => new()
    {
        { new byte[] { (byte)'V', (byte)'G', (byte)'T', (byte)'_' }, 0, "VGT_ GPU debug register" },
        { new byte[] { (byte)'S', (byte)'P', (byte)'I', (byte)'_' }, 0, "SPI_ Shader Processor Interpolator" },
        { new byte[] { (byte)'_', (byte)'T', (byte)'G', (byte)'V' }, 0, "VGT_ reversed for Xbox 360 big-endian" },
        { new byte[] { 0x00, 0x00, 0x00, 0x00, (byte)'T', (byte)'C', (byte)'P', (byte)'_' }, 4, "TCP_ at offset 4" },
    };

    [Theory]
    [MemberData(nameof(KnownFalsePositiveTrueCases))]
    public void IsKnownFalsePositive_KnownPatterns_ReturnsTrue(byte[] data, int offset, string description)
    {
        _ = description; // Used for test case display name
        Assert.True(RecordValidator.IsKnownFalsePositive(data, offset));
    }

    [Fact]
    public void IsKnownFalsePositive_ValidRecord_ReturnsFalse()
    {
        // "NPC_" is not a false positive
        byte[] data = [(byte)'N', (byte)'P', (byte)'C', (byte)'_'];
        Assert.False(RecordValidator.IsKnownFalsePositive(data, 0));
    }

    [Fact]
    public void IsKnownFalsePositive_TooShort_ReturnsFalse()
    {
        // Buffer too short to read 4 bytes at offset
        byte[] data = [(byte)'V', (byte)'G'];
        Assert.False(RecordValidator.IsKnownFalsePositive(data, 0));
    }

    #endregion

    #region MatchesSignature

    [Fact]
    public void MatchesSignature_ExactMatch_ReturnsTrue()
    {
        byte[] data = [(byte)'R', (byte)'E', (byte)'F', (byte)'R'];
        ReadOnlySpan<byte> sig = [(byte)'R', (byte)'E', (byte)'F', (byte)'R'];
        Assert.True(RecordValidator.MatchesSignature(data, 0, sig));
    }

    [Fact]
    public void MatchesSignature_Mismatch_ReturnsFalse()
    {
        byte[] data = [(byte)'R', (byte)'E', (byte)'F', (byte)'R'];
        ReadOnlySpan<byte> sig = [(byte)'A', (byte)'C', (byte)'H', (byte)'R'];
        Assert.False(RecordValidator.MatchesSignature(data, 0, sig));
    }

    [Fact]
    public void MatchesSignature_AtOffset_ReturnsTrue()
    {
        byte[] data = [0x00, 0x00, (byte)'N', (byte)'P', (byte)'C', (byte)'_'];
        ReadOnlySpan<byte> sig = [(byte)'N', (byte)'P', (byte)'C', (byte)'_'];
        Assert.True(RecordValidator.MatchesSignature(data, 2, sig));
    }

    #endregion

    #region MatchesTextureSignature

    [Theory]
    [InlineData('0')]
    [InlineData('1')]
    [InlineData('5')]
    [InlineData('7')]
    public void MatchesTextureSignature_ValidTxRange_ReturnsTrue(char digit)
    {
        byte[] data = [(byte)'T', (byte)'X', (byte)'0', (byte)digit];
        Assert.True(RecordValidator.MatchesTextureSignature(data, 0));
    }

    [Fact]
    public void MatchesTextureSignature_TX08_ReturnsFalse()
    {
        // TX08 is out of range (only TX00-TX07)
        byte[] data = [(byte)'T', (byte)'X', (byte)'0', (byte)'8'];
        Assert.False(RecordValidator.MatchesTextureSignature(data, 0));
    }

    [Fact]
    public void MatchesTextureSignature_WrongPrefix_ReturnsFalse()
    {
        byte[] data = [(byte)'A', (byte)'X', (byte)'0', (byte)'0'];
        Assert.False(RecordValidator.MatchesTextureSignature(data, 0));
    }

    [Fact]
    public void MatchesTextureSignature_BufferTooShort_ReturnsFalse()
    {
        byte[] data = [(byte)'T', (byte)'X', (byte)'0'];
        Assert.False(RecordValidator.MatchesTextureSignature(data, 0));
    }

    [Fact]
    public void MatchesTextureSignature_AtOffset_ReturnsTrue()
    {
        byte[] data = [0xFF, 0xFF, (byte)'T', (byte)'X', (byte)'0', (byte)'3'];
        Assert.True(RecordValidator.MatchesTextureSignature(data, 2));
    }

    #endregion

    #region IsPrintableAscii

    [Theory]
    [InlineData(0x20, true)]    // Space
    [InlineData(0x41, true)]    // 'A'
    [InlineData(0x7E, true)]    // '~'
    [InlineData(0x7F, false)]   // DEL
    [InlineData(0x00, false)]   // NUL
    [InlineData(0x1F, false)]   // Unit separator
    [InlineData(0x80, false)]   // Extended ASCII
    public void IsPrintableAscii_ReturnsExpected(byte b, bool expected)
    {
        Assert.Equal(expected, RecordValidator.IsPrintableAscii(b));
    }

    #endregion

    #region IsFormIdAllPrintableAscii

    [Fact]
    public void IsFormIdAllPrintableAscii_AllPrintable_ReturnsTrue()
    {
        // "ABCD" = 0x44434241 in LE
        uint formId = 0x44434241;
        Assert.True(RecordValidator.IsFormIdAllPrintableAscii(formId));
    }

    [Fact]
    public void IsFormIdAllPrintableAscii_HasNullByte_ReturnsFalse()
    {
        // 0x00123456 — high byte is 0x00 (non-printable)
        uint formId = 0x00123456;
        Assert.False(RecordValidator.IsFormIdAllPrintableAscii(formId));
    }

    [Fact]
    public void IsFormIdAllPrintableAscii_TypicalFormId_ReturnsFalse()
    {
        // Typical FNV FormID: 0x0017B37C — plugin index 0x00 is non-printable
        uint formId = 0x0017B37C;
        Assert.False(RecordValidator.IsFormIdAllPrintableAscii(formId));
    }

    #endregion

    #region IsInExcludedRange

    [Theory]
    [InlineData(150, true)]   // Inside range
    [InlineData(100, true)]   // At start (inclusive)
    [InlineData(200, false)]  // At end (exclusive)
    [InlineData(50, false)]   // Outside range
    public void IsInExcludedRange_SingleRange_ReturnsExpected(long offset, bool expected)
    {
        var ranges = new List<(long start, long end)> { (100, 200) };
        Assert.Equal(expected, RecordValidator.IsInExcludedRange(offset, ranges));
    }

    [Fact]
    public void IsInExcludedRange_NullRanges_ReturnsFalse()
    {
        Assert.False(RecordValidator.IsInExcludedRange(100, null));
    }

    [Fact]
    public void IsInExcludedRange_EmptyRanges_ReturnsFalse()
    {
        Assert.False(RecordValidator.IsInExcludedRange(100, []));
    }

    [Fact]
    public void IsInExcludedRange_MultipleRanges_FindsCorrectOne()
    {
        var ranges = new List<(long start, long end)> { (10, 20), (100, 200), (500, 600) };
        Assert.True(RecordValidator.IsInExcludedRange(550, ranges));
        Assert.False(RecordValidator.IsInExcludedRange(50, ranges));
    }

    #endregion

    #region IsRecordTypeMarker

    [Fact]
    public void IsRecordTypeMarker_ValidAscii_ReturnsTrue()
    {
        byte[] data = [(byte)'N', (byte)'P', (byte)'C', (byte)'_'];
        Assert.True(RecordValidator.IsRecordTypeMarker(data, 0));
    }

    [Fact]
    public void IsRecordTypeMarker_WithDigits_ReturnsTrue()
    {
        byte[] data = [(byte)'T', (byte)'E', (byte)'S', (byte)'4'];
        Assert.True(RecordValidator.IsRecordTypeMarker(data, 0));
    }

    [Fact]
    public void IsRecordTypeMarker_NonAscii_ReturnsFalse()
    {
        byte[] data = [0xFF, 0x00, (byte)'A', (byte)'B'];
        Assert.False(RecordValidator.IsRecordTypeMarker(data, 0));
    }

    #endregion

    #region IsValidMainRecordHeader

    public static TheoryData<string, uint, uint, uint, bool, string> MainRecordHeaderCases => new()
    {
        // Valid cases
        { "NPC_", 500, 0u, 0x0017B37Cu, true, "Typical NPC_ record" },
        { "REFR", 100, 0x00040000u | 0x80000000u, 0x00010001u, true, "Compressed flag allows upper bits" },
        // Invalid cases
        { "REFR", 0, 0u, 0x00010001u, false, "Zero data size" },
        { "REFR", 20_000_000, 0u, 0x00010001u, false, "Huge data size" },
        { "REFR", 100, 0u, 0u, false, "FormID zero" },
        { "REFR", 100, 0u, 0xFFFFFFFFu, false, "FormID all 0xFF" },
        { "REFR", 100, 0u, 0x4B434150u, false, "FormID all printable ASCII (PACK)" },
        { "REFR", 100, 0x80000000u, 0x00010001u, false, "Upper flags without compressed flag" },
    };

    [Theory]
    [MemberData(nameof(MainRecordHeaderCases))]
    public void IsValidMainRecordHeader_ReturnsExpected(
        string recordType, uint dataSize, uint flags, uint formId, bool expected, string description)
    {
        _ = description; // Used for test case display name
        Assert.Equal(expected, RecordValidator.IsValidMainRecordHeader(recordType, dataSize, flags, formId));
    }

    #endregion
}
