using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Merge;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Plugin;

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
            ("DATA", new byte[15]), // ESM DATA is all zeros
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
        Assert.Equal(1234, BinaryPrimitives.ReadInt32LittleEndian(payload));
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

    [Fact]
    public void Merge_ActorPolicy_RetainsPackageListFromMaster()
    {
        // PKID (the actor's AI package list) is an identity field: a prototype patrol package
        // captured from the DMP crashes PatrolActorPackageData when it replaces a master
        // package the runtime route was built against. So the actor policy retains master's
        // PKID and does NOT take the DMP's, nor append it. (See proto_ai_package_crash.)
        var vanillaPackage = BitConverter.GetBytes(0x000E62E1u);
        var capturedFollowerPackage = BitConverter.GetBytes(0x01000958u);
        var esm = MakeEsmRecord("NPC_",
            ("EDID", new byte[] { (byte)'Q', (byte)'J', 0 }),
            ("PKID", vanillaPackage));
        var dmpEncoded = new EncodedRecord
        {
            Subrecords = [new EncodedSubrecord("PKID", capturedFollowerPackage)],
            Warnings = []
        };

        var merge = RecordMergeEngine.Merge(esm, dmpEncoded, SubrecordMergePolicy.ForRecordType("NPC_"));

        Assert.Contains("PKID", merge.EsmSignaturesRetained);
        Assert.DoesNotContain("PKID", merge.DmpSignaturesUsed);
        Assert.DoesNotContain("PKID", merge.DmpSignaturesAppended);
        Assert.Equal(vanillaPackage, ReadFirstSubrecordPayload(merge.SubrecordBytes, "PKID"));
    }

    [Fact]
    public void AdditionalMasterRetention_RetainsEveryOccurrenceAndDoesNotAppendDmpDuplicates()
    {
        var esm = MakeEsmRecord("NPC_",
            ("SNAM", new byte[] { 0x01, 0x02 }),
            ("SNAM", new byte[] { 0x03, 0x04 }));
        var dmpEncoded = new EncodedRecord
        {
            Subrecords =
            [
                new EncodedSubrecord("SNAM", new byte[] { 0xA1, 0xA2 }),
                new EncodedSubrecord("SNAM", new byte[] { 0xA3, 0xA4 })
            ],
            Warnings = []
        };
        var policy = SubrecordMergePolicy.ForRecordType("NPC_")
            .WithAdditionalMasterRetention(["SNAM"]);

        var merge = RecordMergeEngine.Merge(esm, dmpEncoded, policy);

        Assert.Equal(2, CountSubrecords(merge.SubrecordBytes, "SNAM"));
        Assert.Equal(new byte[] { 0x01, 0x02 }, ReadNthSubrecordPayload(merge.SubrecordBytes, "SNAM", 0));
        Assert.Equal(new byte[] { 0x03, 0x04 }, ReadNthSubrecordPayload(merge.SubrecordBytes, "SNAM", 1));
        Assert.DoesNotContain("SNAM", merge.DmpSignaturesUsed);
        Assert.DoesNotContain("SNAM", merge.DmpSignaturesAppended);
    }

    private static int FindSubrecordIndex(byte[] stream, string sig)
    {
        for (var i = 0; i + 6 <= stream.Length;)
        {
            if (stream[i] == sig[0] && stream[i + 1] == sig[1] && stream[i + 2] == sig[2] && stream[i + 3] == sig[3])
            {
                return i;
            }

            var len = BinaryPrimitives.ReadUInt16LittleEndian(stream.AsSpan(i + 4, 2));
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

        var len = BinaryPrimitives.ReadUInt16LittleEndian(stream.AsSpan(idx + 4, 2));
        return stream.AsSpan(idx + 6, len).ToArray();
    }

    private static int CountSubrecords(byte[] stream, string sig)
    {
        var count = 0;
        for (var offset = 0; offset + 6 <= stream.Length;)
        {
            if (stream[offset] == sig[0] && stream[offset + 1] == sig[1]
                                         && stream[offset + 2] == sig[2] && stream[offset + 3] == sig[3])
            {
                count++;
            }

            var len = BinaryPrimitives.ReadUInt16LittleEndian(stream.AsSpan(offset + 4, 2));
            offset += 6 + len;
        }

        return count;
    }

    // ===================================================================================
    // Actor-policy field reconcilers + append filters (USER POLICY 2026-08-03:
    // AIDT matches the proto FILE, not the runtime write-back; NAM6 materialized
    // heights never fabricate or clobber master data).
    // ===================================================================================

    private static byte[] MasterAidt()
    {
        var aidt = new byte[20];
        aidt[0] = 5; // Aggression
        aidt[1] = 4; // Confidence
        aidt[5] = 0xAA; // unused pad — uninitialized GECK noise the file actually carries
        aidt[6] = 0xBB;
        aidt[7] = 0xCC;
        aidt[15] = 0; // AggroRadiusBehavior
        BinaryPrimitives.WriteUInt32LittleEndian(aidt.AsSpan(16, 4), 500); // AggroRadius
        return aidt;
    }

    [Fact]
    public void Merge_NpcAidt_RestoresMasterRadiusAndPads_WhenCaptureZeroedThem()
    {
        var esm = MakeEsmRecord("NPC_", ("AIDT", MasterAidt()));

        var captured = new byte[20];
        captured[0] = 7; // genuinely-changed aggression must survive
        // pads, behavior and radius all zero — the runtime write-back signature
        var merge = RecordMergeEngine.Merge(esm,
            new EncodedRecord { Subrecords = [new EncodedSubrecord("AIDT", captured)], Warnings = [] },
            SubrecordMergePolicy.ForRecordType("NPC_"));

        var payload = ReadNthSubrecordPayload(merge.SubrecordBytes, "AIDT", 0);
        Assert.Equal(7, payload[0]); // captured lane kept
        Assert.Equal([0xAA, 0xBB, 0xCC], payload[5..8]); // master pads restored
        Assert.Equal(500u, BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(16, 4)));
    }

    [Fact]
    public void Merge_NpcAidt_KeepsCapturedRadius_WhenGenuinelySet()
    {
        var esm = MakeEsmRecord("NPC_", ("AIDT", MasterAidt()));

        var captured = new byte[20];
        captured[15] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(captured.AsSpan(16, 4), 200);

        var merge = RecordMergeEngine.Merge(esm,
            new EncodedRecord { Subrecords = [new EncodedSubrecord("AIDT", captured)], Warnings = [] },
            SubrecordMergePolicy.ForRecordType("NPC_"));

        var payload = ReadNthSubrecordPayload(merge.SubrecordBytes, "AIDT", 0);
        Assert.Equal(1, payload[15]);
        Assert.Equal(200u, BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(16, 4)));
    }

    [Theory]
    [InlineData(1.0f)] // adult runtime default
    [InlineData(0.8f)] // child-race runtime default
    public void Merge_NpcHeight_KeepsMasterZero_WhenCaptureHoldsMaterializedDefault(float materialized)
    {
        var esm = MakeEsmRecord("NPC_", ("NAM6", new byte[4])); // master stores 0.0

        var captured = new byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(captured, materialized);

        var merge = RecordMergeEngine.Merge(esm,
            new EncodedRecord { Subrecords = [new EncodedSubrecord("NAM6", captured)], Warnings = [] },
            SubrecordMergePolicy.ForRecordType("NPC_"));

        var payload = ReadNthSubrecordPayload(merge.SubrecordBytes, "NAM6", 0);
        Assert.Equal(0.0f, BinaryPrimitives.ReadSingleLittleEndian(payload));
    }

    [Fact]
    public void Merge_NpcHeight_KeepsGenuineProtoHeight()
    {
        var esm = MakeEsmRecord("NPC_", ("NAM6", new byte[4]));

        var captured = new byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(captured, 1.15f);

        var merge = RecordMergeEngine.Merge(esm,
            new EncodedRecord { Subrecords = [new EncodedSubrecord("NAM6", captured)], Warnings = [] },
            SubrecordMergePolicy.ForRecordType("NPC_"));

        var payload = ReadNthSubrecordPayload(merge.SubrecordBytes, "NAM6", 0);
        Assert.Equal(1.15f, BinaryPrimitives.ReadSingleLittleEndian(payload));
    }

    [Fact]
    public void Merge_NpcHeight_NeverAppendsMaterializedDefault_WhenMasterOmitsNam6()
    {
        // xex44 class: baseline AND proto-360 carry no NAM6 at all; the capture materializes 1.0.
        var esm = MakeEsmRecord("NPC_", ("EDID", new byte[] { (byte)'n', 0 }));

        var materialized = new byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(materialized, 1.0f);

        var merge = RecordMergeEngine.Merge(esm,
            new EncodedRecord { Subrecords = [new EncodedSubrecord("NAM6", materialized)], Warnings = [] },
            SubrecordMergePolicy.ForRecordType("NPC_"));

        Assert.DoesNotContain("NAM6", merge.DmpSignaturesAppended);

        // A genuine proto height on the same shape must still append.
        var genuine = new byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(genuine, 1.15f);
        merge = RecordMergeEngine.Merge(esm,
            new EncodedRecord { Subrecords = [new EncodedSubrecord("NAM6", genuine)], Warnings = [] },
            SubrecordMergePolicy.ForRecordType("NPC_"));

        Assert.Contains("NAM6", merge.DmpSignaturesAppended);
    }

    private static byte[] ReadNthSubrecordPayload(byte[] stream, string sig, int occurrence)
    {
        var seen = 0;
        for (var offset = 0; offset + 6 <= stream.Length;)
        {
            var len = BinaryPrimitives.ReadUInt16LittleEndian(stream.AsSpan(offset + 4, 2));
            if (stream[offset] == sig[0] && stream[offset + 1] == sig[1]
                                         && stream[offset + 2] == sig[2] && stream[offset + 3] == sig[3]
                                         && seen++ == occurrence)
            {
                return stream.AsSpan(offset + 6, len).ToArray();
            }

            offset += 6 + len;
        }

        return [];
    }
}