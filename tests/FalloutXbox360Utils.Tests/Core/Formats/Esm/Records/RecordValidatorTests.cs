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

    [Fact]
    public void IsKnownFalsePositive_VgtDebugPattern_ReturnsTrue()
    {
        // "VGT_" is a known false positive (GPU debug register)
        byte[] data = [(byte)'V', (byte)'G', (byte)'T', (byte)'_'];
        Assert.True(RecordValidator.IsKnownFalsePositive(data, 0));
    }

    [Fact]
    public void IsKnownFalsePositive_SpiPattern_ReturnsTrue()
    {
        // "SPI_" is a known false positive (Shader Processor Interpolator)
        byte[] data = [(byte)'S', (byte)'P', (byte)'I', (byte)'_'];
        Assert.True(RecordValidator.IsKnownFalsePositive(data, 0));
    }

    [Fact]
    public void IsKnownFalsePositive_BigEndianReversed_ReturnsTrue()
    {
        // "VGT_" reversed for Xbox 360 big-endian: "_TGV"
        byte[] data = [(byte)'_', (byte)'T', (byte)'G', (byte)'V'];
        Assert.True(RecordValidator.IsKnownFalsePositive(data, 0));
    }

    [Fact]
    public void IsKnownFalsePositive_ValidRecord_ReturnsFalse()
    {
        // "NPC_" is not a false positive
        byte[] data = [(byte)'N', (byte)'P', (byte)'C', (byte)'_'];
        Assert.False(RecordValidator.IsKnownFalsePositive(data, 0));
    }

    [Fact]
    public void IsKnownFalsePositive_AtOffset_DetectsCorrectly()
    {
        // Pattern at offset 4, not offset 0
        byte[] data = [0x00, 0x00, 0x00, 0x00, (byte)'T', (byte)'C', (byte)'P', (byte)'_'];
        Assert.True(RecordValidator.IsKnownFalsePositive(data, 4));
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

    [Fact]
    public void IsInExcludedRange_InsideRange_ReturnsTrue()
    {
        var ranges = new List<(long start, long end)> { (100, 200) };
        Assert.True(RecordValidator.IsInExcludedRange(150, ranges));
    }

    [Fact]
    public void IsInExcludedRange_AtStart_ReturnsTrue()
    {
        var ranges = new List<(long start, long end)> { (100, 200) };
        Assert.True(RecordValidator.IsInExcludedRange(100, ranges));
    }

    [Fact]
    public void IsInExcludedRange_AtEnd_ReturnsFalse()
    {
        // End is exclusive
        var ranges = new List<(long start, long end)> { (100, 200) };
        Assert.False(RecordValidator.IsInExcludedRange(200, ranges));
    }

    [Fact]
    public void IsInExcludedRange_OutsideRange_ReturnsFalse()
    {
        var ranges = new List<(long start, long end)> { (100, 200) };
        Assert.False(RecordValidator.IsInExcludedRange(50, ranges));
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

    [Fact]
    public void IsValidMainRecordHeader_ValidRecord_ReturnsTrue()
    {
        // Typical NPC_ record: reasonable data size, no special flags, valid FormID
        Assert.True(RecordValidator.IsValidMainRecordHeader("NPC_", 500, 0, 0x0017B37C));
    }

    [Fact]
    public void IsValidMainRecordHeader_ZeroDataSize_ReturnsFalse()
    {
        Assert.False(RecordValidator.IsValidMainRecordHeader("REFR", 0, 0, 0x00010001));
    }

    [Fact]
    public void IsValidMainRecordHeader_HugeDataSize_ReturnsFalse()
    {
        Assert.False(RecordValidator.IsValidMainRecordHeader("REFR", 20_000_000, 0, 0x00010001));
    }

    [Fact]
    public void IsValidMainRecordHeader_FormIdZero_ReturnsFalse()
    {
        Assert.False(RecordValidator.IsValidMainRecordHeader("REFR", 100, 0, 0));
    }

    [Fact]
    public void IsValidMainRecordHeader_FormIdAllFs_ReturnsFalse()
    {
        Assert.False(RecordValidator.IsValidMainRecordHeader("REFR", 100, 0, 0xFFFFFFFF));
    }

    [Fact]
    public void IsValidMainRecordHeader_FormIdAllPrintableAscii_ReturnsFalse()
    {
        // "PACK" as FormID bytes = 0x4B434150 — all printable, likely inside string data
        uint asciiFormId = 0x4B434150; // "PACK" interpreted as uint
        Assert.False(RecordValidator.IsValidMainRecordHeader("REFR", 100, 0, asciiFormId));
    }

    [Fact]
    public void IsValidMainRecordHeader_CompressedFlag_AllowsUpperBits()
    {
        // Compressed flag (0x00040000) should allow records that set upper flag bits
        uint flags = 0x00040000 | 0x80000000; // Compressed + upper bit
        Assert.True(RecordValidator.IsValidMainRecordHeader("REFR", 100, flags, 0x00010001));
    }

    [Fact]
    public void IsValidMainRecordHeader_BadUpperFlags_ReturnsFalse()
    {
        // Upper bits set without compressed flag -> invalid
        uint flags = 0x80000000;
        Assert.False(RecordValidator.IsValidMainRecordHeader("REFR", 100, flags, 0x00010001));
    }

    #endregion
}
