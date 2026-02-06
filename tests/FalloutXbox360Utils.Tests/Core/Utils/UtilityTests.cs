using System.Buffers.Binary;
using System.Text;
using FalloutXbox360Utils.Core.Utils;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Utils;

/// <summary>
///     Tests for EsmStringUtils, EsmSubrecordUtils, and Xbox360MemoryUtils.
/// </summary>
public class UtilityTests
{
    #region EsmStringUtils.ReadNullTermString (Span overload)

    [Fact]
    public void ReadNullTermString_Span_Normal_ReturnsString()
    {
        var data = "Hello\0World"u8.ToArray();
        var result = EsmStringUtils.ReadNullTermString(data);
        Assert.Equal("Hello", result);
    }

    [Fact]
    public void ReadNullTermString_Span_NoTerminator_ReturnsWholeString()
    {
        var data = "Hello"u8.ToArray();
        var result = EsmStringUtils.ReadNullTermString(data);
        Assert.Equal("Hello", result);
    }

    [Fact]
    public void ReadNullTermString_Span_Empty_ReturnsEmpty()
    {
        var result = EsmStringUtils.ReadNullTermString(ReadOnlySpan<byte>.Empty);
        Assert.Equal("", result);
    }

    [Fact]
    public void ReadNullTermString_Span_ImmediateNull_ReturnsEmpty()
    {
        byte[] data = [0x00, 0x41, 0x42];
        var result = EsmStringUtils.ReadNullTermString(data);
        Assert.Equal("", result);
    }

    #endregion

    #region EsmStringUtils.ReadNullTermString (byte[] overload)

    [Fact]
    public void ReadNullTermString_Array_Normal_ReturnsString()
    {
        byte[] data = [0x41, 0x42, 0x43, 0x00, 0x44]; // "ABC\0D"
        var result = EsmStringUtils.ReadNullTermString(data, 0, 5);
        Assert.Equal("ABC", result);
    }

    [Fact]
    public void ReadNullTermString_Array_WithOffset_ReturnsFromOffset()
    {
        byte[] data = [0x00, 0x00, 0x48, 0x69, 0x00]; // "\0\0Hi\0"
        var result = EsmStringUtils.ReadNullTermString(data, 2, 3);
        Assert.Equal("Hi", result);
    }

    [Fact]
    public void ReadNullTermString_Array_NoNull_ReturnsUpToMaxLen()
    {
        byte[] data = [0x41, 0x42, 0x43, 0x44, 0x45]; // "ABCDE"
        var result = EsmStringUtils.ReadNullTermString(data, 0, 3);
        Assert.Equal("ABC", result);
    }

    [Fact]
    public void ReadNullTermString_Array_EmptyRange_ReturnsEmpty()
    {
        byte[] data = [0x41];
        var result = EsmStringUtils.ReadNullTermString(data, 0, 0);
        Assert.Equal("", result);
    }

    #endregion

    #region EsmStringUtils.IsPrintableAscii

    [Fact]
    public void IsPrintableAscii_AllPrintable_ReturnsTrue()
    {
        var data = "Hello, World!"u8.ToArray();
        Assert.True(EsmStringUtils.IsPrintableAscii(data));
    }

    [Fact]
    public void IsPrintableAscii_WithWhitespace_ReturnsTrue()
    {
        var data = "Hello\nWorld\r\n\tTab"u8.ToArray();
        Assert.True(EsmStringUtils.IsPrintableAscii(data));
    }

    [Fact]
    public void IsPrintableAscii_BinaryData_ReturnsFalse()
    {
        byte[] data = [0x00, 0x01, 0x02, 0x03, 0x04, 0x05];
        Assert.False(EsmStringUtils.IsPrintableAscii(data));
    }

    [Fact]
    public void IsPrintableAscii_Empty_ReturnsFalse()
    {
        Assert.False(EsmStringUtils.IsPrintableAscii(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void IsPrintableAscii_MixedAboveThreshold_ReturnsTrue()
    {
        // 80% threshold: 8 printable + 2 non-printable = 80%
        byte[] data = [0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x01, 0x02];
        Assert.True(EsmStringUtils.IsPrintableAscii(data)); // Exactly at 80%
    }

    [Fact]
    public void IsPrintableAscii_MixedBelowThreshold_ReturnsFalse()
    {
        // 7 printable + 3 non-printable = 70% < 80%
        byte[] data = [0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47, 0x01, 0x02, 0x03];
        Assert.False(EsmStringUtils.IsPrintableAscii(data));
    }

    [Fact]
    public void IsPrintableAscii_CustomThreshold_Respected()
    {
        // 5 printable + 5 non-printable = 50%
        byte[] data = [0x41, 0x42, 0x43, 0x44, 0x45, 0x01, 0x02, 0x03, 0x04, 0x05];
        Assert.True(EsmStringUtils.IsPrintableAscii(data, 0.5f));
        Assert.False(EsmStringUtils.IsPrintableAscii(data, 0.6f));
    }

    #endregion

    #region EsmStringUtils.ValidateAndDecodeAscii

    [Fact]
    public void ValidateAndDecodeAscii_Valid_ReturnsString()
    {
        byte[] buffer = [0x48, 0x65, 0x6C, 0x6C, 0x6F]; // "Hello"
        var result = EsmStringUtils.ValidateAndDecodeAscii(buffer, 5);
        Assert.Equal("Hello", result);
    }

    [Fact]
    public void ValidateAndDecodeAscii_NonPrintable_ReturnsNull()
    {
        byte[] buffer = [0x00, 0x01, 0x02, 0x03, 0x04];
        Assert.Null(EsmStringUtils.ValidateAndDecodeAscii(buffer, 5));
    }

    [Fact]
    public void ValidateAndDecodeAscii_ZeroLength_ReturnsNull()
    {
        byte[] buffer = [0x41];
        Assert.Null(EsmStringUtils.ValidateAndDecodeAscii(buffer, 0));
    }

    [Fact]
    public void ValidateAndDecodeAscii_NegativeLength_ReturnsNull()
    {
        byte[] buffer = [0x41];
        Assert.Null(EsmStringUtils.ValidateAndDecodeAscii(buffer, -1));
    }

    [Fact]
    public void ValidateAndDecodeAscii_LengthExceedsBuffer_ReturnsNull()
    {
        byte[] buffer = [0x41, 0x42];
        Assert.Null(EsmStringUtils.ValidateAndDecodeAscii(buffer, 10));
    }

    [Fact]
    public void ValidateAndDecodeAscii_PartialBuffer_DecodesCorrectly()
    {
        byte[] buffer = [0x48, 0x69, 0x00, 0x00, 0x00]; // "Hi..."
        var result = EsmStringUtils.ValidateAndDecodeAscii(buffer, 2);
        Assert.Equal("Hi", result);
    }

    #endregion

    #region EsmStringUtils Constants

    [Fact]
    public void DefaultPrintableThreshold_Is80Percent()
    {
        Assert.Equal(0.8f, EsmStringUtils.DefaultPrintableThreshold);
    }

    [Fact]
    public void MaxBSStringLength_Is4096()
    {
        Assert.Equal(4096, EsmStringUtils.MaxBSStringLength);
    }

    #endregion

    #region EsmSubrecordUtils.IterateSubrecords

    [Fact]
    public void IterateSubrecords_LeChain_ReturnsAll()
    {
        // Two subrecords: EDID(5 bytes) + DATA(4 bytes)
        var edid = "Test\0"u8.ToArray();
        var data = new byte[] { 0x01, 0x02, 0x03, 0x04 };

        var buf = new byte[6 + edid.Length + 6 + data.Length];
        var off = 0;

        // EDID
        Encoding.ASCII.GetBytes("EDID").CopyTo(buf, off);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(off + 4), (ushort)edid.Length);
        edid.CopyTo(buf, off + 6);
        off += 6 + edid.Length;

        // DATA
        Encoding.ASCII.GetBytes("DATA").CopyTo(buf, off);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(off + 4), (ushort)data.Length);
        data.CopyTo(buf, off + 6);

        var subs = EsmSubrecordUtils.IterateSubrecords(buf, buf.Length, bigEndian: false).ToList();

        Assert.Equal(2, subs.Count);
        Assert.Equal("EDID", subs[0].Signature);
        Assert.Equal(6, subs[0].DataOffset);
        Assert.Equal(edid.Length, subs[0].DataLength);
        Assert.Equal("DATA", subs[1].Signature);
    }

    [Fact]
    public void IterateSubrecords_BeChain_ReturnsReversedSignatures()
    {
        // Big-endian: signature bytes reversed
        var data = new byte[] { 0xAA, 0xBB };
        var buf = new byte[6 + data.Length];

        // "EDID" in BE is stored as "DIDE"
        buf[0] = (byte)'D';
        buf[1] = (byte)'I';
        buf[2] = (byte)'D';
        buf[3] = (byte)'E';
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(4), (ushort)data.Length);
        data.CopyTo(buf, 6);

        var subs = EsmSubrecordUtils.IterateSubrecords(buf, buf.Length, bigEndian: true).ToList();

        Assert.Single(subs);
        Assert.Equal("EDID", subs[0].Signature);
    }

    [Fact]
    public void IterateSubrecords_Empty_ReturnsEmpty()
    {
        var subs = EsmSubrecordUtils.IterateSubrecords([], 0, false).ToList();
        Assert.Empty(subs);
    }

    [Fact]
    public void IterateSubrecords_Truncated_StopsGracefully()
    {
        var buf = new byte[8]; // Only room for header + 2 bytes, claims more
        Encoding.ASCII.GetBytes("EDID").CopyTo(buf, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(4), 100); // Claims 100 bytes

        var subs = EsmSubrecordUtils.IterateSubrecords(buf, buf.Length, false).ToList();
        Assert.Empty(subs); // Should not yield since data exceeds buffer
    }

    #endregion

    #region EsmSubrecordUtils.ReadSignatureAsUInt32

    [Fact]
    public void ReadSignatureAsUInt32_Le_ReadsCorrectly()
    {
        byte[] data = [0x45, 0x44, 0x49, 0x44]; // "EDID" LE
        var result = EsmSubrecordUtils.ReadSignatureAsUInt32(data, bigEndian: false);
        // LE: 0x44494445
        Assert.Equal(0x44494445u, result);
    }

    [Fact]
    public void ReadSignatureAsUInt32_Be_ReadsCorrectly()
    {
        byte[] data = [0x45, 0x44, 0x49, 0x44]; // Same bytes
        var result = EsmSubrecordUtils.ReadSignatureAsUInt32(data, bigEndian: true);
        // BE: 0x45444944
        Assert.Equal(0x45444944u, result);
    }

    #endregion

    #region EsmSubrecordUtils.SignatureToUInt32

    [Fact]
    public void SignatureToUInt32_ValidSig_ReturnsCorrect()
    {
        var result = EsmSubrecordUtils.SignatureToUInt32("EDID");
        // LE encoding: E(0x45) | D(0x44)<<8 | I(0x49)<<16 | D(0x44)<<24
        var expected = (uint)('E' | ('D' << 8) | ('I' << 16) | ('D' << 24));
        Assert.Equal(expected, result);
    }

    [Fact]
    public void SignatureToUInt32_InvalidLength_Throws()
    {
        Assert.Throws<ArgumentException>(() => EsmSubrecordUtils.SignatureToUInt32("ED"));
        Assert.Throws<ArgumentException>(() => EsmSubrecordUtils.SignatureToUInt32("EDIDX"));
    }

    [Fact]
    public void SignatureToUInt32_NPC_WithUnderscore_Works()
    {
        var result = EsmSubrecordUtils.SignatureToUInt32("NPC_");
        Assert.True(result != 0);
    }

    [Fact]
    public void SignatureToUInt32_TwoSigsNotEqual()
    {
        var edid = EsmSubrecordUtils.SignatureToUInt32("EDID");
        var data = EsmSubrecordUtils.SignatureToUInt32("DATA");
        Assert.NotEqual(edid, data);
    }

    #endregion

    #region EsmSubrecordUtils Constants

    [Fact]
    public void SubrecordHeaderSize_Is6()
    {
        Assert.Equal(6, EsmSubrecordUtils.SubrecordHeaderSize);
    }

    #endregion

    #region Xbox360MemoryUtils.VaToLong

    [Fact]
    public void VaToLong_HeapAddress_ReturnsPositive()
    {
        var result = Xbox360MemoryUtils.VaToLong(0x40001000);
        Assert.Equal(0x40001000L, result);
    }

    [Fact]
    public void VaToLong_ModuleAddress_ReturnsSignExtended()
    {
        // 0x82000000 has bit 31 set, so sign extension makes it negative
        var result = Xbox360MemoryUtils.VaToLong(0x82000000);
        Assert.True(result < 0); // Sign-extended
        Assert.Equal(unchecked((int)0x82000000), result);
    }

    [Fact]
    public void VaToLong_Zero_ReturnsZero()
    {
        Assert.Equal(0L, Xbox360MemoryUtils.VaToLong(0));
    }

    [Fact]
    public void VaToLong_MaxUInt32_ReturnsNegative1()
    {
        Assert.Equal(-1L, Xbox360MemoryUtils.VaToLong(0xFFFFFFFF));
    }

    #endregion

    #region Xbox360MemoryUtils.IsValidPointer

    [Fact]
    public void IsValidPointer_HeapBase_ReturnsTrue()
    {
        Assert.True(Xbox360MemoryUtils.IsValidPointer(0x40000000));
    }

    [Fact]
    public void IsValidPointer_InHeap_ReturnsTrue()
    {
        Assert.True(Xbox360MemoryUtils.IsValidPointer(0x45000000));
    }

    [Fact]
    public void IsValidPointer_HeapEnd_ReturnsFalse()
    {
        // HeapEnd (0x50000000) is exclusive
        Assert.False(Xbox360MemoryUtils.IsValidPointer(0x50000000));
    }

    [Fact]
    public void IsValidPointer_ModuleBase_ReturnsTrue()
    {
        Assert.True(Xbox360MemoryUtils.IsValidPointer(0x82000000));
    }

    [Fact]
    public void IsValidPointer_AboveModuleBase_ReturnsTrue()
    {
        Assert.True(Xbox360MemoryUtils.IsValidPointer(0x90000000));
    }

    [Fact]
    public void IsValidPointer_Zero_ReturnsFalse()
    {
        Assert.False(Xbox360MemoryUtils.IsValidPointer(0));
    }

    [Fact]
    public void IsValidPointer_MaxUInt32_ReturnsTrue()
    {
        // 0xFFFFFFFF >= ModuleBase (0x82000000)
        Assert.True(Xbox360MemoryUtils.IsValidPointer(0xFFFFFFFF));
    }

    [Fact]
    public void IsValidPointer_BelowHeap_ReturnsFalse()
    {
        Assert.False(Xbox360MemoryUtils.IsValidPointer(0x30000000));
    }

    [Fact]
    public void IsValidPointer_BetweenHeapAndModule_ReturnsFalse()
    {
        // Between 0x50000000 and 0x82000000
        Assert.False(Xbox360MemoryUtils.IsValidPointer(0x60000000));
    }

    #endregion

    #region Xbox360MemoryUtils.IsHeapPointer

    [Fact]
    public void IsHeapPointer_InRange_ReturnsTrue()
    {
        Assert.True(Xbox360MemoryUtils.IsHeapPointer(0x40000000));
        Assert.True(Xbox360MemoryUtils.IsHeapPointer(0x4FFFFFFF));
    }

    [Fact]
    public void IsHeapPointer_OutOfRange_ReturnsFalse()
    {
        Assert.False(Xbox360MemoryUtils.IsHeapPointer(0x50000000));
        Assert.False(Xbox360MemoryUtils.IsHeapPointer(0x30000000));
        Assert.False(Xbox360MemoryUtils.IsHeapPointer(0x82000000));
    }

    #endregion

    #region Xbox360MemoryUtils.IsModulePointer

    [Fact]
    public void IsModulePointer_AtBase_ReturnsTrue()
    {
        Assert.True(Xbox360MemoryUtils.IsModulePointer(0x82000000));
    }

    [Fact]
    public void IsModulePointer_Above_ReturnsTrue()
    {
        Assert.True(Xbox360MemoryUtils.IsModulePointer(0xFFFFFFFF));
    }

    [Fact]
    public void IsModulePointer_Below_ReturnsFalse()
    {
        Assert.False(Xbox360MemoryUtils.IsModulePointer(0x81FFFFFF));
        Assert.False(Xbox360MemoryUtils.IsModulePointer(0x40000000));
        Assert.False(Xbox360MemoryUtils.IsModulePointer(0));
    }

    #endregion

    #region Xbox360MemoryUtils Constants

    [Fact]
    public void HeapBase_Is0x40000000()
    {
        Assert.Equal(0x40000000u, Xbox360MemoryUtils.HeapBase);
    }

    [Fact]
    public void HeapEnd_Is0x50000000()
    {
        Assert.Equal(0x50000000u, Xbox360MemoryUtils.HeapEnd);
    }

    [Fact]
    public void ModuleBase_Is0x82000000()
    {
        Assert.Equal(0x82000000u, Xbox360MemoryUtils.ModuleBase);
    }

    #endregion
}
