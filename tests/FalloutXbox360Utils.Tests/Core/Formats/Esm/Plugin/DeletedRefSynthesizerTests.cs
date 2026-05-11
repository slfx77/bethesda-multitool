using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Merge;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin;

public class DeletedRefSynthesizerTests
{
    private const uint DeletedFlag = 0x00000020;
    private const uint CompressedFlag = 0x00040000;
    private const uint PersistentFlag = 0x00000400;

    [Fact]
    public void Synthesize_RefsInDmpSet_AreNotDeleted()
    {
        var masterRefs = new[]
        {
            MakeRef(0x100, persistent: false, "RefA"),
            MakeRef(0x101, persistent: false, "RefB")
        };
        var dmpFormIds = new HashSet<uint> { 0x100, 0x101 };

        var bundle = DeletedRefSynthesizer.Synthesize(masterRefs, dmpFormIds);

        Assert.Empty(bundle.Persistent);
        Assert.Empty(bundle.Temporary);
    }

    [Fact]
    public void Synthesize_RefMissingFromDmp_GeneratesDeletedOverride()
    {
        var masterRefs = new[] { MakeRef(0x200, persistent: false, "Doomed") };
        var dmpFormIds = new HashSet<uint>(); // empty — ref is missing

        var bundle = DeletedRefSynthesizer.Synthesize(masterRefs, dmpFormIds);

        Assert.Empty(bundle.Persistent);
        Assert.Single(bundle.Temporary);

        var bytes = bundle.Temporary[0];
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4));
        Assert.Equal(DeletedFlag, flags & DeletedFlag);
        Assert.Equal(0u, flags & CompressedFlag);
    }

    [Fact]
    public void Synthesize_PersistentMasterRef_GoesIntoPersistentBucket()
    {
        var masterRefs = new[] { MakeRef(0x300, persistent: true, "PersistentDoomed") };
        var dmpFormIds = new HashSet<uint>();

        var bundle = DeletedRefSynthesizer.Synthesize(masterRefs, dmpFormIds);

        Assert.Single(bundle.Persistent);
        Assert.Empty(bundle.Temporary);
    }

    [Fact]
    public void Synthesize_DeletedOverridePreservesEdid()
    {
        var masterRefs = new[] { MakeRef(0x400, persistent: false, "EdidPreserved") };
        var dmpFormIds = new HashSet<uint>();

        var bundle = DeletedRefSynthesizer.Synthesize(masterRefs, dmpFormIds);

        var bytes = bundle.Temporary[0];
        // Header is 24 bytes; subrecord stream starts at offset 24.
        Assert.Equal((byte)'E', bytes[24]);
        Assert.Equal((byte)'D', bytes[25]);
        Assert.Equal((byte)'I', bytes[26]);
        Assert.Equal((byte)'D', bytes[27]);
        // EDID payload is "EdidPreserved\0" = 14 bytes.
        var edidLen = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(28, 2));
        Assert.Equal(14, edidLen);
    }

    [Fact]
    public void Synthesize_RefWithoutEdid_StillProducesValidRecord()
    {
        // A master ref with no EDID — output has a 24-byte header and zero subrecord bytes.
        var masterRef = new ParsedMainRecord
        {
            Header = new MainRecordHeader { Signature = "REFR", FormId = 0x500, Version = 0x000F },
            Subrecords = []
        };
        var dmpFormIds = new HashSet<uint>();

        var bundle = DeletedRefSynthesizer.Synthesize([masterRef], dmpFormIds);

        var bytes = bundle.Temporary[0];
        Assert.Equal(24, bytes.Length);
        var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4));
        Assert.Equal(0u, dataSize);
    }

    private static ParsedMainRecord MakeRef(uint formId, bool persistent, string editorId)
    {
        return new ParsedMainRecord
        {
            Header = new MainRecordHeader
            {
                Signature = "REFR",
                FormId = formId,
                Flags = persistent ? PersistentFlag : 0,
                Version = 0x000F
            },
            Subrecords =
            [
                new ParsedSubrecord
                {
                    Signature = "EDID",
                    Data = System.Text.Encoding.Latin1.GetBytes(editorId + "\0")
                }
            ]
        };
    }
}
