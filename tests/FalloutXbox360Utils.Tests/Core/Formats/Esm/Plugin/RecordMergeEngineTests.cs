using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Merge;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin;

/// <summary>
///     Tests for <see cref="RecordMergeEngine" /> — verifies that DMP-encoded subrecords
///     overlay correctly on parsed ESM subrecords and that policy retains specific signatures.
/// </summary>
public class RecordMergeEngineTests
{
    private static ParsedMainRecord MakeEsmRecord(string sig, params (string Sub, byte[] Data)[] subrecords)
    {
        return new ParsedMainRecord
        {
            Header = new MainRecordHeader
            {
                Signature = sig,
                DataSize = 0,
                Flags = 0,
                FormId = 0x0017B37C,
                Timestamp = 0,
                VcsInfo = 0,
                Version = 0x000F
            },
            Subrecords = subrecords
                .Select(t => new ParsedSubrecord { Signature = t.Sub, Data = t.Data })
                .ToList()
        };
    }

    [Fact]
    public void Merge_OverlaysDmpBytes_OnEsmSubrecord()
    {
        var esm = MakeEsmRecord("WEAP",
            ("EDID", new byte[] { (byte)'a', 0 }),
            ("DATA", new byte[15]),  // ESM DATA is all zeros
            ("DNAM", new byte[204]));

        var dmpData = new byte[15];
        SubrecordEncoder.WriteInt32(dmpData, 0, 1234);
        SubrecordEncoder.WriteFloat(dmpData, 8, 7.5f);
        var dmpEncoded = new EncodedRecord
        {
            Subrecords = [new EncodedSubrecord("DATA", dmpData)],
            Warnings = []
        };

        var merge = RecordMergeEngine.Merge(esm, dmpEncoded, SubrecordMergePolicy.Default);

        Assert.Contains("DATA", merge.DmpSignaturesUsed);
        Assert.Contains("EDID", merge.EsmSignaturesRetained);
        Assert.Contains("DNAM", merge.EsmSignaturesRetained);
        Assert.Empty(merge.DmpSignaturesAppended);

        // Decode the DATA section from the merged stream and verify it has the DMP bytes.
        var stream = merge.SubrecordBytes;
        var dataIndex = FindSubrecordIndex(stream, "DATA");
        Assert.True(dataIndex >= 0, "DATA subrecord not found in merged output.");
        var payload = stream.AsSpan(dataIndex + 6, 15);
        Assert.Equal(1234, System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(payload));
    }

    [Fact]
    public void Merge_RetainsEsmBytes_WhenPolicyForbidsOverlay()
    {
        var esm = MakeEsmRecord("WEAP",
            ("EDID", new byte[] { (byte)'a', 0 }),
            ("MODT", new byte[] { 0xAA, 0xBB, 0xCC, 0xDD })); // pretend ESM has texture hash

        var dmpEncoded = new EncodedRecord
        {
            Subrecords = [new EncodedSubrecord("MODT", new byte[] { 0x11, 0x22, 0x33, 0x44 })],
            Warnings = []
        };

        var policy = SubrecordMergePolicy.ForRecordType("WEAP");
        var merge = RecordMergeEngine.Merge(esm, dmpEncoded, policy);

        Assert.DoesNotContain("MODT", merge.DmpSignaturesUsed);
        Assert.Contains("MODT", merge.EsmSignaturesRetained);
        Assert.Contains("MODT", merge.DmpSignaturesAppended); // still appended in pass 2

        // The first MODT in output is the ESM bytes (0xAA…), the appended one is DMP (0x11…).
        var firstModtPayload = ReadFirstSubrecordPayload(merge.SubrecordBytes, "MODT");
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD }, firstModtPayload);
    }

    [Fact]
    public void Merge_AppendsDmpOnlySignatures_AtEnd()
    {
        var esm = MakeEsmRecord("MISC",
            ("EDID", new byte[] { (byte)'a', 0 }));

        var dmpEncoded = new EncodedRecord
        {
            Subrecords = [new EncodedSubrecord("DATA", new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 })],
            Warnings = []
        };

        var merge = RecordMergeEngine.Merge(esm, dmpEncoded, SubrecordMergePolicy.Default);

        Assert.Empty(merge.DmpSignaturesUsed);
        Assert.Contains("DATA", merge.DmpSignaturesAppended);

        // EDID should appear before DATA in the merged stream.
        var edidIdx = FindSubrecordIndex(merge.SubrecordBytes, "EDID");
        var dataIdx = FindSubrecordIndex(merge.SubrecordBytes, "DATA");
        Assert.True(edidIdx < dataIdx);
    }

    [Fact]
    public void Merge_CellPolicy_PreservesMasterStructuralDataAndSkipsDmpOnlyXclc()
    {
        var esm = MakeEsmRecord("CELL",
            ("EDID", new byte[] { (byte)'a', 0 }),
            ("DATA", [0x21]));

        var dmpEncoded = new EncodedRecord
        {
            Subrecords =
            [
                new EncodedSubrecord("DATA", [0x54]),
                new EncodedSubrecord("XCLC", new byte[12])
            ],
            Warnings = []
        };

        var merge = RecordMergeEngine.Merge(esm, dmpEncoded, SubrecordMergePolicy.ForRecordType("CELL"));

        Assert.DoesNotContain("DATA", merge.DmpSignaturesUsed);
        Assert.Contains("DATA", merge.EsmSignaturesRetained);
        Assert.DoesNotContain("DATA", merge.DmpSignaturesAppended);
        Assert.DoesNotContain("XCLC", merge.DmpSignaturesAppended);

        Assert.Equal([0x21], ReadFirstSubrecordPayload(merge.SubrecordBytes, "DATA"));
        Assert.Equal(-1, FindSubrecordIndex(merge.SubrecordBytes, "XCLC"));
    }

    private static int FindSubrecordIndex(byte[] stream, string sig)
    {
        for (var i = 0; i + 6 <= stream.Length;)
        {
            if (stream[i] == sig[0] && stream[i + 1] == sig[1] && stream[i + 2] == sig[2] && stream[i + 3] == sig[3])
            {
                return i;
            }

            var len = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(stream.AsSpan(i + 4, 2));
            i += 6 + len;
        }

        return -1;
    }

    private static byte[] ReadFirstSubrecordPayload(byte[] stream, string sig)
    {
        var idx = FindSubrecordIndex(stream, sig);
        if (idx < 0)
        {
            return [];
        }

        var len = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(stream.AsSpan(idx + 4, 2));
        return stream.AsSpan(idx + 6, len).ToArray();
    }
}
