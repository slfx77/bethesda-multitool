using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Records;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;
using static FalloutXbox360Utils.Tests.Helpers.BinaryTestWriter;

namespace FalloutXbox360Utils.Tests.Helpers;

/// <summary>
///     Shared helpers for building synthetic ESM record structures in tests.
///     Replaces duplicate record-building methods across 5+ test files.
/// </summary>
internal static class EsmTestRecordBuilder
{
    /// <summary>Creates a null-terminated ASCII string as bytes.</summary>
    public static byte[] NullTermString(string s)
    {
        var bytes = new byte[s.Length + 1];
        Encoding.ASCII.GetBytes(s, bytes);
        return bytes;
    }

    #region Compressed Record Builders

    /// <summary>
    ///     Build a big-endian compressed ESM record. The subrecord payload is zlib-compressed
    ///     and prepended with the 4-byte decompressed size (BE). The record header has
    ///     flag 0x00040000 set to indicate compression.
    /// </summary>
    public static byte[] BuildCompressedRecordBE(string recSig, uint formId,
        params (string sig, byte[] data)[] subrecords)
    {
        // Build uncompressed subrecord payload (BE)
        var payloadSize = 0;
        foreach (var (_, data) in subrecords)
        {
            payloadSize += 6 + data.Length;
        }

        var payload = new byte[payloadSize];
        var offset = 0;
        foreach (var (sig, data) in subrecords)
        {
            WriteSigBE(payload, offset, sig);
            BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(offset + 4), (ushort)data.Length);
            Array.Copy(data, 0, payload, offset + 6, data.Length);
            offset += 6 + data.Length;
        }

        // Compress with zlib
        using var compressedStream = new MemoryStream();
        using (var zlibStream = new ZLibStream(
                   compressedStream, CompressionLevel.Optimal, true))
        {
            zlibStream.Write(payload);
        }

        var compressed = compressedStream.ToArray();

        // Record data = [4B decompressed size BE] + [compressed bytes]
        var recordDataSize = 4 + compressed.Length;
        var totalSize = 24 + recordDataSize;
        var buf = new byte[totalSize];

        // Header with compressed flag 0x00040000
        WriteBERecordHeader(buf, 0, recSig, (uint)recordDataSize, formId, 0x00040000);

        // Decompressed size (BE)
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(24), (uint)payloadSize);

        // Compressed data
        Array.Copy(compressed, 0, buf, 28, compressed.Length);

        return buf;
    }

    #endregion

    #region Little-Endian Record Builders

    /// <summary>
    ///     Build a synthetic main record header (24 bytes) at the specified offset.
    ///     Layout: [SIG:4][DataSize:4][Flags:4][FormId:4][VC1:4][VC2:4]
    /// </summary>
    public static void WriteMainRecordHeader(
        byte[] buf, int offset, string sig, uint dataSize, uint flags, uint formId)
    {
        WriteSig(buf, offset, sig);
        WriteUInt32LE(buf, offset + 4, dataSize);
        WriteUInt32LE(buf, offset + 8, flags);
        WriteUInt32LE(buf, offset + 12, formId);
        WriteUInt32LE(buf, offset + 16, 0); // VC1
        WriteUInt32LE(buf, offset + 20, 0); // VC2
    }

    /// <summary>
    ///     Build a synthetic EDID subrecord at the specified offset.
    ///     Layout: [EDID:4][Length:2][NullTermString]
    ///     Returns total bytes written.
    /// </summary>
    public static int WriteEdidSubrecord(byte[] buf, int offset, string editorId)
    {
        WriteSig(buf, offset, "EDID");
        var text = Encoding.ASCII.GetBytes(editorId + "\0");
        WriteUInt16LE(buf, offset + 4, (ushort)text.Length);
        Array.Copy(text, 0, buf, offset + 6, text.Length);
        return 6 + text.Length;
    }

    /// <summary>
    ///     Build a synthetic FULL subrecord at the specified offset.
    ///     Layout: [FULL:4][Length:2][NullTermString]
    /// </summary>
    public static void WriteFullSubrecord(byte[] buf, int offset, string displayName)
    {
        WriteSig(buf, offset, "FULL");
        var text = Encoding.ASCII.GetBytes(displayName + "\0");
        WriteUInt16LE(buf, offset + 4, (ushort)text.Length);
        Array.Copy(text, 0, buf, offset + 6, text.Length);
    }

    /// <summary>
    ///     Build a complete valid record (header + EDID) in a fresh buffer, with padding
    ///     for scanner lookahead.
    /// </summary>
    public static byte[] BuildRecordWithEdid(
        string sig, uint dataSize, uint flags, uint formId, string editorId)
    {
        var edidPayload = Encoding.ASCII.GetBytes(editorId + "\0");
        var edidSubrecordSize = 6 + edidPayload.Length;

        var effectiveDataSize = Math.Max(dataSize, (uint)edidSubrecordSize);

        var buf = new byte[24 + effectiveDataSize + 24];
        WriteMainRecordHeader(buf, 0, sig, effectiveDataSize, flags, formId);
        WriteEdidSubrecord(buf, 24, editorId);
        return buf;
    }

    /// <summary>
    ///     Build a minimal little-endian ESM record with one subrecord.
    /// </summary>
    public static byte[] BuildMinimalRecordLE(string recSig, uint formId, string subSig, byte[] subData)
    {
        var subrecordSize = 4 + 2 + subData.Length;
        var dataSize = (uint)subrecordSize;
        var totalSize = 24 + (int)dataSize;
        var buf = new byte[totalSize];

        WriteSig(buf, 0, recSig);
        WriteUInt32LE(buf, 4, dataSize);
        WriteUInt32LE(buf, 8, 0);
        WriteUInt32LE(buf, 12, formId);
        WriteUInt32LE(buf, 16, 0);
        WriteUInt32LE(buf, 20, 0);

        WriteSig(buf, 24, subSig);
        WriteUInt16LE(buf, 28, (ushort)subData.Length);
        Array.Copy(subData, 0, buf, 30, subData.Length);

        return buf;
    }

    /// <summary>
    ///     Build a little-endian record with multiple subrecords.
    /// </summary>
    public static byte[] BuildRecordWithSubrecordsLE(string recSig, uint formId,
        params (string sig, byte[] data)[] subrecords)
    {
        var totalSubSize = 0;
        foreach (var (_, data) in subrecords)
        {
            totalSubSize += 4 + 2 + data.Length;
        }

        var buf = new byte[24 + totalSubSize];

        WriteSig(buf, 0, recSig);
        WriteUInt32LE(buf, 4, (uint)totalSubSize);
        WriteUInt32LE(buf, 8, 0);
        WriteUInt32LE(buf, 12, formId);
        WriteUInt32LE(buf, 16, 0);
        WriteUInt32LE(buf, 20, 0);

        var offset = 24;
        foreach (var (sig, data) in subrecords)
        {
            WriteSig(buf, offset, sig);
            WriteUInt16LE(buf, offset + 4, (ushort)data.Length);
            Array.Copy(data, 0, buf, offset + 6, data.Length);
            offset += 6 + data.Length;
        }

        return buf;
    }

    #endregion

    #region Big-Endian Record Builders

    /// <summary>
    ///     Build a minimal big-endian ESM record with one subrecord.
    /// </summary>
    public static byte[] BuildMinimalRecordBE(string recSig, uint formId, string subSig, byte[] subData)
    {
        var subrecordSize = 4 + 2 + subData.Length;
        var dataSize = (uint)subrecordSize;
        var totalSize = 24 + (int)dataSize;
        var buf = new byte[totalSize];

        WriteSigBE(buf, 0, recSig);
        WriteUInt32BE(buf, 4, dataSize);
        WriteUInt32BE(buf, 8, 0);
        WriteUInt32BE(buf, 12, formId);
        WriteUInt32BE(buf, 16, 0);
        WriteUInt32BE(buf, 20, 0);

        WriteSigBE(buf, 24, subSig);
        WriteUInt16BE(buf, 28, (ushort)subData.Length);
        Array.Copy(subData, 0, buf, 30, subData.Length);

        return buf;
    }

    /// <summary>
    ///     Writes a big-endian record header at the specified offset.
    ///     Record header: [SIG:4 reversed][SIZE:4 BE][FLAGS:4 BE][FORMID:4 BE][TS:4][VCS:4] = 24 bytes
    /// </summary>
    public static void WriteBERecordHeader(byte[] buffer, int offset, string signature, uint dataSize,
        uint formId = 0, uint flags = 0)
    {
        WriteSigBE(buffer, offset, signature);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(offset + 4), dataSize);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(offset + 8), flags);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(offset + 12), formId);
    }

    /// <summary>
    ///     Writes a big-endian GRUP header at the specified offset.
    ///     GRUP header: [GRUP:4 reversed][SIZE:4 BE][LABEL:4 BE][TYPE:4 BE][STAMP:4][UNK:4] = 24 bytes
    /// </summary>
    public static void WriteBEGrupHeader(byte[] buffer, int offset, uint groupSize, string labelSignature,
        int groupType = 0)
    {
        buffer[offset + 0] = (byte)'P';
        buffer[offset + 1] = (byte)'U';
        buffer[offset + 2] = (byte)'R';
        buffer[offset + 3] = (byte)'G';
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(offset + 4), groupSize);

        WriteSigBE(buffer, offset + 8, labelSignature);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(offset + 12), (uint)groupType);
    }

    /// <summary>
    ///     Writes a big-endian subrecord at the specified offset.
    ///     Subrecord: [SIG:4 reversed][SIZE:2 BE][DATA:N]
    /// </summary>
    public static void WriteBESubrecord(byte[] buffer, int offset, string signature, byte[] data)
    {
        WriteSigBE(buffer, offset, signature);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset + 4), (ushort)data.Length);
        Array.Copy(data, 0, buffer, offset + 6, data.Length);
    }

    #endregion

    #region Dual-Endian Record Builders

    /// <summary>
    ///     Builds a byte buffer containing ESM record data with subrecords in either endianness.
    ///     Layout: [24-byte record header] [subrecord data...]
    /// </summary>
    public static byte[] BuildRecordBytes(uint formId, string recordType, bool bigEndian,
        params (string sig, byte[] data)[] subrecords)
    {
        var dataSize = 0;
        foreach (var (_, data) in subrecords)
        {
            dataSize += 6 + data.Length;
        }

        var totalSize = 24 + dataSize;
        var buffer = new byte[totalSize];

        var sigBytes = Encoding.ASCII.GetBytes(recordType);
        if (bigEndian)
        {
            buffer[0] = sigBytes[3];
            buffer[1] = sigBytes[2];
            buffer[2] = sigBytes[1];
            buffer[3] = sigBytes[0];
            BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(4), (uint)dataSize);
            BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(8), 0);
            BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(12), formId);
        }
        else
        {
            Array.Copy(sigBytes, buffer, 4);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4), (uint)dataSize);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8), 0);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(12), formId);
        }

        var offset = 24;
        foreach (var (sig, data) in subrecords)
        {
            var subSigBytes = Encoding.ASCII.GetBytes(sig);
            if (bigEndian)
            {
                buffer[offset] = subSigBytes[3];
                buffer[offset + 1] = subSigBytes[2];
                buffer[offset + 2] = subSigBytes[1];
                buffer[offset + 3] = subSigBytes[0];
                BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset + 4), (ushort)data.Length);
            }
            else
            {
                Array.Copy(subSigBytes, 0, buffer, offset, 4);
                BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset + 4), (ushort)data.Length);
            }

            Array.Copy(data, 0, buffer, offset + 6, data.Length);
            offset += 6 + data.Length;
        }

        return buffer;
    }

    /// <summary>Write a subrecord header via BinaryWriter (dual-endian).</summary>
    public static void WriteSubrecordHeader(BinaryWriter bw, string signature, ushort dataLength, bool bigEndian)
    {
        if (bigEndian)
        {
            bw.Write((byte)signature[3]);
            bw.Write((byte)signature[2]);
            bw.Write((byte)signature[1]);
            bw.Write((byte)signature[0]);
            bw.Write((byte)(dataLength >> 8));
            bw.Write((byte)(dataLength & 0xFF));
        }
        else
        {
            bw.Write(Encoding.ASCII.GetBytes(signature));
            bw.Write(dataLength);
        }
    }

    /// <summary>Write a record header via BinaryWriter (dual-endian).</summary>
    public static void WriteRecordHeader(BinaryWriter bw, string signature, uint dataSize, uint flags, uint formId,
        bool bigEndian)
    {
        if (bigEndian)
        {
            bw.Write((byte)signature[3]);
            bw.Write((byte)signature[2]);
            bw.Write((byte)signature[1]);
            bw.Write((byte)signature[0]);
            WriteBigEndianUInt32(bw, dataSize);
            WriteBigEndianUInt32(bw, flags);
            WriteBigEndianUInt32(bw, formId);
            bw.Write(0L);
        }
        else
        {
            bw.Write(Encoding.ASCII.GetBytes(signature));
            bw.Write(dataSize);
            bw.Write(flags);
            bw.Write(formId);
            bw.Write(0L);
        }
    }

    /// <summary>Write a GRUP header via BinaryWriter (dual-endian).</summary>
    public static void WriteGroupHeader(BinaryWriter bw, int grupSize, string label, bool bigEndian)
    {
        if (bigEndian)
        {
            bw.Write((byte)'P');
            bw.Write((byte)'U');
            bw.Write((byte)'R');
            bw.Write((byte)'G');
            WriteBigEndianUInt32(bw, (uint)grupSize);
            bw.Write((byte)label[3]);
            bw.Write((byte)label[2]);
            bw.Write((byte)label[1]);
            bw.Write((byte)label[0]);
            WriteBigEndianUInt32(bw, 0);
            WriteBigEndianUInt32(bw, 0);
            WriteBigEndianUInt32(bw, 0);
        }
        else
        {
            bw.Write(Encoding.ASCII.GetBytes("GRUP"));
            bw.Write(grupSize);
            bw.Write(Encoding.ASCII.GetBytes(label));
            bw.Write(0);
            bw.Write(0);
            bw.Write(0);
        }
    }

    #endregion

    #region Complete ESM Builders

    /// <summary>
    ///     Creates a minimal big-endian ESM file with just a TES4 header record.
    ///     The TES4 record contains a HEDR subrecord (12 bytes) as required by the parser.
    /// </summary>
    public static byte[] BuildMinimalBigEndianEsm()
    {
        const int tes4DataSize = 18;
        const int totalSize = 24 + tes4DataSize;

        var data = new byte[totalSize];
        WriteBERecordHeader(data, 0, "TES4", tes4DataSize);

        var hedrData = new byte[12];
        BinaryPrimitives.WriteSingleBigEndian(hedrData.AsSpan(0), 1.34f);
        BinaryPrimitives.WriteInt32BigEndian(hedrData.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(hedrData.AsSpan(8), 0x00000800);
        WriteBESubrecord(data, 24, "HEDR", hedrData);

        return data;
    }

    /// <summary>
    ///     Creates a big-endian ESM with TES4 header + a simple GRUP containing an ALCH record.
    /// </summary>
    public static byte[] BuildSimpleEsmWithGrup()
    {
        var edid = Encoding.ASCII.GetBytes("TestAlch\0");
        var subrecordSize = 6 + edid.Length;
        var recordDataSize = subrecordSize;

        var tes4Data = BuildMinimalBigEndianEsm();
        var alchRecordTotalSize = 24 + recordDataSize;
        var grupTotalSize = (uint)(24 + alchRecordTotalSize);
        var totalSize = tes4Data.Length + (int)grupTotalSize;

        var data = new byte[totalSize];
        Array.Copy(tes4Data, data, tes4Data.Length);

        var grupOffset = tes4Data.Length;
        WriteBEGrupHeader(data, grupOffset, grupTotalSize, "ALCH");

        var recordOffset = grupOffset + 24;
        WriteBERecordHeader(data, recordOffset, "ALCH", (uint)recordDataSize, 0x00010001);
        WriteBESubrecord(data, recordOffset + 24, "EDID", edid);

        return data;
    }

    #endregion

    #region Scan Result Builders

    /// <summary>Creates a minimal EsmRecordScanResult with the given main records.</summary>
    public static EsmRecordScanResult MakeScanResult(
        List<DetectedMainRecord>? mainRecords = null,
        List<EdidRecord>? editorIds = null,
        List<TextSubrecord>? fullNames = null,
        List<ActorBaseSubrecord>? actorBases = null,
        List<RuntimeEditorIdEntry>? runtimeEditorIds = null,
        Dictionary<uint, List<uint>>? topicToInfoMap = null)
    {
        return new EsmRecordScanResult
        {
            MainRecords = mainRecords ?? [],
            EditorIds = editorIds ?? [],
            FullNames = fullNames ?? [],
            ActorBases = actorBases ?? [],
            RuntimeEditorIds = runtimeEditorIds ?? [],
            TopicToInfoMap = topicToInfoMap ?? []
        };
    }

    /// <summary>Creates a DetectedMainRecord with sensible defaults.</summary>
    public static DetectedMainRecord MakeRecord(string type, uint formId, long offset, uint dataSize = 100,
        bool isBigEndian = false, uint flags = 0)
    {
        return new DetectedMainRecord(type, dataSize, flags, formId, offset, isBigEndian);
    }

    #endregion
}