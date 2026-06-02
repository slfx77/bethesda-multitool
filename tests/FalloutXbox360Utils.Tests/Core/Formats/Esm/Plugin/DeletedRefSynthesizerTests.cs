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

    [Fact]
    public void Synthesize_PreservePredicate_SkipsDeletedOverride()
    {
        var masterRefs = new[] { MakeRef(0x600, persistent: false, "PortalMarker") };
        var dmpFormIds = new HashSet<uint>();

        var bundle = DeletedRefSynthesizer.Synthesize(
            masterRefs,
            dmpFormIds,
            masterRef => masterRef.Header.FormId == 0x600);

        Assert.Empty(bundle.Persistent);
        Assert.Empty(bundle.Temporary);
    }

    [Fact]
    public void Synthesize_PersistentOnlyPreserveFilter_KeepsPersistentDeletesTemporary()
    {
        // Models the ReplaceCellTemporariesOnOverride filter: pass true for persistent refs
        // so they're preserved, false for temporary refs so they get deletion markers.
        var masterRefs = new[]
        {
            MakeRef(0x700, persistent: true,  "QuestItem_Persistent"),
            MakeRef(0x701, persistent: false, "Clutter_Temporary"),
            MakeRef(0x702, persistent: true,  "DoorMarker_Persistent"),
            MakeRef(0x703, persistent: false, "Streetlight_Temporary")
        };
        var dmpFormIds = new HashSet<uint>(); // none in DMP → all "missing"

        var bundle = DeletedRefSynthesizer.Synthesize(
            masterRefs,
            dmpFormIds,
            masterRef => (masterRef.Header.Flags & PersistentFlag) != 0);

        // Persistent refs preserved entirely (no deletion markers emitted).
        Assert.Empty(bundle.Persistent);
        // Both temporary refs got deletion markers.
        Assert.Equal(2, bundle.Temporary.Count);
    }

    [Fact]
    public void Synthesize_PersistentOnlyPreserveFilter_DmpOverrideStillSuppressesDeletion()
    {
        // If a temporary master ref IS in the DMP snapshot it's an override (not a deletion).
        // Filter should never even be consulted for those — they're already handled.
        var masterRefs = new[]
        {
            MakeRef(0x800, persistent: false, "TempRef_InDmp"),
            MakeRef(0x801, persistent: false, "TempRef_NotInDmp")
        };
        var dmpFormIds = new HashSet<uint> { 0x800 };

        var bundle = DeletedRefSynthesizer.Synthesize(
            masterRefs,
            dmpFormIds,
            masterRef => (masterRef.Header.Flags & PersistentFlag) != 0);

        // Only the not-in-DMP temporary gets a deletion marker.
        Assert.Empty(bundle.Persistent);
        Assert.Single(bundle.Temporary);
    }

    [Fact]
    public void Synthesize_LoadedReplacementPolicy_DeletesOrdinaryTempsPreservesActorsAndPersistentRefs()
    {
        var masterRefs = new[]
        {
            MakeRef(0x900, persistent: false, "OrdinaryStatic"),
            MakeRecord("ACHR", 0x901, persistent: false, "ScriptActor"),
            MakeRef(0x902, persistent: true, "PersistentQuestRef")
        };
        var dmpFormIds = new HashSet<uint>();

        var bundle = DeletedRefSynthesizer.Synthesize(
            masterRefs,
            dmpFormIds,
            masterRef => masterRef.Header.Signature is "ACHR" or "ACRE" ||
                         (masterRef.Header.Flags & PersistentFlag) != 0);

        Assert.Empty(bundle.Persistent);
        Assert.Single(bundle.Temporary);
        var deletedFormId = BinaryPrimitives.ReadUInt32LittleEndian(bundle.Temporary[0].AsSpan(12, 4));
        Assert.Equal(0x900u, deletedFormId);
    }

    private static ParsedMainRecord MakeRef(uint formId, bool persistent, string editorId)
    {
        return MakeRecord("REFR", formId, persistent, editorId);
    }

    private static ParsedMainRecord MakeRecord(string signature, uint formId, bool persistent, string editorId)
    {
        return new ParsedMainRecord
        {
            Header = new MainRecordHeader
            {
                Signature = signature,
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
