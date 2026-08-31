using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Plugin.Writers;

/// <summary>
///     The subrecords that only became writable once the layout export started emitting nested
///     struct layouts: MODS (alternate textures), LNAM (load-screen locations) and the
///     DEST/DSTD/DSTF destruction block.
/// </summary>
public sealed class NestedPayloadSubrecordTests
{
    [Fact]
    public void AlternateTextures_RoundTripThroughTheReaderThatParsesThem()
    {
        // The writer is the inverse of AlternateTextureParser, so the parser is the oracle: if
        // what we write does not read back identically, one of the two has the format wrong.
        List<AlternateTextureEntry> entries =
        [
            new("Body", 0x0004B1C2, 0),
            new("Barrel", 0x0004B1C3, 2)
        ];

        var encoded = NewRecordSubrecords.EncodeAlternateTexturesSubrecord("MODS", entries);

        Assert.Equal("MODS", encoded.Signature);
        Assert.Equal(entries, AlternateTextureParser.Parse(encoded.Bytes, isBigEndian: false));
    }

    [Fact]
    public void AlternateTextureNames_AreLengthPrefixedNotNullTerminated()
    {
        // A stray terminator would land inside the next entry's length word, so this is the
        // difference between a readable array and one that decodes to garbage after entry one.
        var encoded = NewRecordSubrecords.EncodeAlternateTexturesSubrecord(
            "MODS", [new AlternateTextureEntry("Body", 0x0004B1C2, 0)]);

        Assert.Equal(4 + 4 + 4 + 8, encoded.Bytes.Length);
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(encoded.Bytes.AsSpan(4)));
        Assert.Equal("Body"u8.ToArray(), encoded.Bytes[8..12]);
    }

    [Fact]
    public void LoadScreenLocation_IsTwelveBytesInDeclarationOrder()
    {
        var encoded = NewRecordSubrecords.EncodeLoadScreenLocationSubrecord(
            new LoadScreenLocationEntry(0x0001C0DE, 0x000DA726, 0xFFF8_0004));

        Assert.Equal("LNAM", encoded.Signature);
        Assert.Equal(12, encoded.Bytes.Length);
        Assert.Equal(0x0001C0DEu, BinaryPrimitives.ReadUInt32LittleEndian(encoded.Bytes));
        Assert.Equal(0x000DA726u, BinaryPrimitives.ReadUInt32LittleEndian(encoded.Bytes.AsSpan(4)));
        Assert.Equal(0xFFF8_0004u, BinaryPrimitives.ReadUInt32LittleEndian(encoded.Bytes.AsSpan(8)));
    }

    [Fact]
    public void DestructionBlock_EmitsHeaderThenOneStageGroupPerStage()
    {
        var destruction = new DestructionData(325, 0xCE,
        [
            new DestructionStage(93, 0, 0, 0, 0, 0, 0, null),
            new DestructionStage(65, 4, 0x05, 10, 0x000B2959, 0, 0, @"Vehicles\CarHulk02.NIF")
        ]);

        var subs = NewRecordSubrecords.EncodeDestructionBlock(destruction);

        Assert.Equal(
            ["DEST", "DSTD", "DSTF", "DSTD", "DMDL", "DSTF"],
            subs.Select(s => s.Signature));

        var header = subs[0].Bytes;
        Assert.Equal(8, header.Length);
        Assert.Equal(325, BinaryPrimitives.ReadInt32LittleEndian(header));
        Assert.Equal(2, header[4]);
        Assert.Equal(0xCE, header[5]);

        var second = subs[3].Bytes;
        Assert.Equal(20, second.Length);
        Assert.Equal(65, second[0]);
        Assert.Equal(1, second[1]); // Index is the stage's position, which has no runtime member
        Assert.Equal(4, second[2]);
        Assert.Equal(0x05, second[3]);
        Assert.Equal(10, BinaryPrimitives.ReadInt32LittleEndian(second.AsSpan(4)));
        Assert.Equal(0x000B2959u, BinaryPrimitives.ReadUInt32LittleEndian(second.AsSpan(8)));
    }

    [Fact]
    public void DestructionHeaderCount_TracksTheStagesActuallyEmitted()
    {
        // The engine sizes its stage array from DEST's count and fills it from the DSTD blocks
        // that follow, so a count larger than the blocks leaves slots unpopulated. This is the
        // same rule IDLM's IDLC follows against its IDLA.
        var subs = NewRecordSubrecords.EncodeDestructionBlock(new DestructionData(325, 0xCE, []));

        Assert.Equal(["DEST"], subs.Select(s => s.Signature));
        Assert.Equal(0, subs[0].Bytes[4]);
    }

    [Fact]
    public void MoreStagesThanAByteCanCount_ClampsTheHeaderAndTheBlocksTogether()
    {
        // DEST's count and DSTD's stage index are both u8, so 255 is the format's ceiling. The
        // reader's own cap is 32 and nothing in the corpus approaches it, but the two limits were
        // not tied: the header used to clamp while the loop kept emitting, which would have
        // produced more DSTD blocks than the count claims and duplicate stage indices past 255.
        var stages = Enumerable.Range(0, 300)
            .Select(i => new DestructionStage((byte)(i % 100), 0, 0, 0, 0, 0, 0, null))
            .ToList();

        var subs = NewRecordSubrecords.EncodeDestructionBlock(new DestructionData(1, 0x43, stages));

        Assert.Equal(255, subs[0].Bytes[4]);
        Assert.Equal(255, subs.Count(s => s.Signature == "DSTD"));

        // Every emitted stage index is its own, so none collides at the byte boundary.
        var indices = subs.Where(s => s.Signature == "DSTD").Select(s => s.Bytes[1]).ToList();
        Assert.Equal(255, indices.Distinct().Count());
        Assert.Equal(254, indices[^1]);
    }

    [Fact]
    public void AlternateTextures_AreDroppedWholesaleWhenOneTextureSetDidNotResolve()
    {
        // A swap that names a shape but no replacement texture would leave the engine with an
        // instruction it cannot carry out, and dropping just that entry changes which shapes get
        // swapped without saying so.
        var record = GenericRecordWith("TESModelTextureSwap.TextureSwapList",
            new List<AlternateTextureEntry>
            {
                new("Body", 0x0004B1C2, 0),
                new("Barrel", 0u, 2)
            });

        Assert.Null(GenericRecordFields.TryAlternateTextures(
            record, "MODS", "TESModelTextureSwap.TextureSwapList"));
    }

    [Fact]
    public void LoadScreenLocations_DropOnlyTheEntriesThatCouldNotBeReferences()
    {
        // LNAM repeats, one subrecord per location, so entries are independent of one another and
        // a bad one costs only itself.
        var record = GenericRecordWith("TESLoadScreen.LoadFormList",
            new List<LoadScreenLocationEntry>
            {
                new(0x0001C0DE, 0x000DA726, 0),
                new(0x8233_9658, 0, 0) // a raw Xbox VA, not a FormID
            });

        var locations = GenericRecordFields.TryLoadScreenLocations(
            record, "LNAM", "TESLoadScreen.LoadFormList");

        Assert.Equal([new LoadScreenLocationEntry(0x0001C0DE, 0x000DA726, 0)], locations);
    }

    private static GenericEsmRecord GenericRecordWith(string key, object value)
    {
        return new GenericEsmRecord
        {
            FormId = 0x0100_1234,
            RecordType = "MSTT",
            Fields = new Dictionary<string, object?> { [key] = value }
        };
    }
}
