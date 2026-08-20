using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Records;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Records;

/// <summary>
///     Blind-skip regression: a torn record header whose scribbled DataSize passes the scalar
///     gates AND the first-subrecord probe (its leading bytes are the real record's) used to
///     make the skip-ahead scanner leap the bogus extent — xex44 lost the 4.4 MB tail of a
///     16 MB chunk (an entire cell-children GRUP, including a captured PGRE) to one stale
///     REFR claiming 4.5 MB. Headers above 64 KB must now prove their extent: compressed via
///     the zlib stream header, uncompressed via the subrecord chain tiling the payload.
/// </summary>
public class EsmRecordScannerExtentTests
{
    private const int Threshold = 64 * 1024;

    [Fact]
    public void TornLargeHeader_DoesNotBlindTheScannerToFollowingRecords()
    {
        // Torn BE REFR: genuine-looking header + NAME first subrecord, but DataSize claims
        // everything to (and past) the buffer end. Real records follow 4 KB later.
        var buffer = new byte[512 * 1024];
        WriteBeRecordHeader(buffer, 0x1000, "REFR", 4_500_000, 0x1014BFA5);
        WriteBeSubrecord(buffer, 0x1000 + 24, "NAME", FormIdBytesBE(0x00146E25));
        // Garbage after the first subrecord — the chain must misalign.
        for (var i = 0x1000 + 24 + 10; i < 0x1400; i++)
        {
            buffer[i] = 0xC7;
        }

        var realOffsets = new long[] { 0x2000, 0x2040, 0x2080 };
        for (var r = 0; r < realOffsets.Length; r++)
        {
            WriteBeRecordHeader(buffer, (int)realOffsets[r], r == 1 ? "PGRE" : "REFR",
                40, 0x0014685Au + (uint)r);
            WriteBeSubrecord(buffer, (int)realOffsets[r] + 24, "NAME", FormIdBytesBE(0x000043FA));
            WriteBeSubrecord(buffer, (int)realOffsets[r] + 34, "DATA", new byte[24]);
        }

        var result = ScanMapped(buffer);

        Assert.DoesNotContain(result.MainRecords, r => r.Offset == 0x1000);
        foreach (var offset in realOffsets)
        {
            Assert.Contains(result.MainRecords, r => r.Offset == offset);
        }

        Assert.Contains(result.MainRecords, r => r.RecordType == "PGRE");
    }

    [Fact]
    public void GenuineLargeUncompressedRecord_WithTilingChain_IsStillDetected()
    {
        // NAVI-class: one record > 64 KB whose subrecord chain tiles the payload exactly.
        var subCount = 20;
        var subLen = 4000;
        var dataSize = subCount * (6 + subLen);
        Assert.True(dataSize > Threshold);

        var buffer = new byte[dataSize + 0x4000];
        WriteBeRecordHeader(buffer, 0x100, "NAVI", (uint)dataSize, 0x00014B92);
        var pos = 0x100 + 24;
        for (var s = 0; s < subCount; s++)
        {
            // First subrecord must clear the registry-backed first-subrecord probe, so lead
            // with EDID (real NAVI does too); the extent walk itself is charset-structural.
            WriteBeSubrecord(buffer, pos, s == 0 ? "EDID" : "NVMI", new byte[subLen]);
            pos += 6 + subLen;
        }

        var result = ScanMapped(buffer);

        Assert.Contains(result.MainRecords, r => r.Offset == 0x100 && r.RecordType == "NAVI");
    }

    [Fact]
    public void GenuineLargeRecord_WithXxxxEscapedSubrecord_IsStillDetected()
    {
        // WRLD-class: a > 64 KB OFST carried via the XXXX extended-size escape (stored len 0).
        const int ofstLen = 100_000;
        var dataSize = 6 + 4 + 6 + ofstLen; // XXXX(4) + OFST(0-stored, XXXX-sized)

        var buffer = new byte[dataSize + 0x4000];
        WriteBeRecordHeader(buffer, 0x100, "WRLD", (uint)dataSize, 0x000DA726);
        var pos = 0x100 + 24;
        var xxxxPayload = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(xxxxPayload, ofstLen);
        WriteBeSubrecord(buffer, pos, "XXXX", xxxxPayload);
        pos += 10;
        WriteBeSubrecord(buffer, pos, "OFST", []);
        // The OFST body follows its zero-length header; fill with non-signature bytes.
        for (var i = pos + 6; i < pos + 6 + ofstLen; i++)
        {
            buffer[i] = 0x01;
        }

        var result = ScanMapped(buffer);

        Assert.Contains(result.MainRecords, r => r.Offset == 0x100 && r.RecordType == "WRLD");
    }

    [Fact]
    public void LargeCompressedClaim_WithoutZlibHeader_IsRejected()
    {
        var buffer = new byte[256 * 1024];
        WriteBeRecordHeader(buffer, 0x100, "LAND", 120_000, 0x000DB114, 0x00040000);
        // Payload: 4-byte decompressed size then garbage where the zlib CMF/FLG belongs.
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(0x100 + 24, 4), 200_000);
        buffer[0x100 + 28] = 0xC7;
        buffer[0x100 + 29] = 0xC7;

        var result = ScanMapped(buffer);

        Assert.DoesNotContain(result.MainRecords, r => r.Offset == 0x100);
    }

    [Fact]
    public void LargeCompressedRecord_WithZlibHeader_IsStillDetected()
    {
        var buffer = new byte[256 * 1024];
        WriteBeRecordHeader(buffer, 0x100, "LAND", 120_000, 0x000DB114, 0x00040000);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(0x100 + 24, 4), 200_000);
        buffer[0x100 + 28] = 0x78; // CMF: deflate, 32K window
        buffer[0x100 + 29] = 0x9C; // FLG: (0x789C % 31) == 0

        var result = ScanMapped(buffer);

        Assert.Contains(result.MainRecords, r => r.Offset == 0x100 && r.RecordType == "LAND");
    }

    [Fact]
    public void SmallRecords_AreNotSubjectToExtentVerification()
    {
        // A torn SMALL record (garbage after the first subrecord) must still be detected —
        // partial-record recovery depends on it, and a small bogus skip is harmless.
        var buffer = new byte[0x8000];
        WriteBeRecordHeader(buffer, 0x100, "REFR", 40, 0x00146850);
        WriteBeSubrecord(buffer, 0x100 + 24, "NAME", FormIdBytesBE(0x000043FA));
        for (var i = 0x100 + 34; i < 0x100 + 64; i++)
        {
            buffer[i] = 0xC7;
        }

        var result = ScanMapped(buffer);

        Assert.Contains(result.MainRecords, r => r.Offset == 0x100 && r.RecordType == "REFR");
    }

    private static EsmRecordScanResult ScanMapped(byte[] buffer)
    {
        using var mmf = MemoryMappedFile.CreateNew(null, buffer.Length);
        using var accessor = mmf.CreateViewAccessor(0, buffer.Length);
        accessor.WriteArray(0, buffer, 0, buffer.Length);
        return EsmRecordScanner.ScanForRecordsMemoryMapped(accessor, buffer.Length);
    }

    private static void WriteBeRecordHeader(
        byte[] buffer, int offset, string signature, uint dataSize, uint formId, uint flags = 0)
    {
        var sig = Encoding.ASCII.GetBytes(signature);
        buffer[offset] = sig[3];
        buffer[offset + 1] = sig[2];
        buffer[offset + 2] = sig[1];
        buffer[offset + 3] = sig[0];
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(offset + 4, 4), dataSize);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(offset + 8, 4), flags);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(offset + 12, 4), formId);
    }

    private static void WriteBeSubrecord(byte[] buffer, int offset, string signature, byte[] data)
    {
        var sig = Encoding.ASCII.GetBytes(signature);
        buffer[offset] = sig[3];
        buffer[offset + 1] = sig[2];
        buffer[offset + 2] = sig[1];
        buffer[offset + 3] = sig[0];
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset + 4, 2), (ushort)data.Length);
        data.CopyTo(buffer, offset + 6);
    }

    private static byte[] FormIdBytesBE(uint formId)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, formId);
        return bytes;
    }
}