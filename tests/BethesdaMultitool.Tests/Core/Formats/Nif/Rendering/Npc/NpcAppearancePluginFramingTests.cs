using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Conversion.Processing;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Appearance;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Npc;

public sealed class NpcAppearancePluginFramingTests
{
    private const uint NpcFormId = 0x00123456;
    private const uint RaceFormId = 0x00000907;

    [Theory]
    [InlineData(20, false)]
    [InlineData(20, true)]
    [InlineData(24, false)]
    [InlineData(24, true)]
    public void AppearanceIndex_UsesDetectedRecordFraming_AndPreservesEndianness(
        int headerSize,
        bool bigEndian)
    {
        var esm = BuildPlugin(headerSize, bigEndian);

        var detected = PluginFormat.Detect(esm);
        Assert.Equal(headerSize, detected.RecordHeaderSize);
        Assert.Equal(headerSize, detected.GroupHeaderSize);

        var records = EsmRecordParser.ScanAllRecords(esm, bigEndian);
        var record = Assert.Single(records);
        Assert.Equal("NPC_", record.Signature);
        Assert.Equal(NpcFormId, record.FormId);
        Assert.Equal(headerSize, record.RecordHeaderSize);

        var index = NpcAppearanceIndexBuilder.Build(esm, bigEndian);
        var npc = Assert.Contains(NpcFormId, index.Npcs);
        Assert.Equal("FramingNpc", npc.EditorId);
        Assert.Equal("Framing NPC", npc.FullName);
        Assert.Equal(RaceFormId, npc.RaceFormId);
        Assert.Equal([1.25f, -2.5f], Assert.IsType<float[]>(npc.FaceGenSymmetric));
    }

    [Fact]
    [Trait("Category", TestCategories.BucketB)]
    public void RetailOblivion_AppearanceIndex_PopulatesNpcsAndFaceGen()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var esmPath = RealAssetPaths.Masters.Oblivion();
        Assert.SkipWhen(esmPath is null, RealAssetPaths.SkipMessage("Oblivion.esm"));

        var esm = File.ReadAllBytes(esmPath!);
        Assert.Equal(20, PluginFormat.Detect(esm).RecordHeaderSize);

        var index = NpcAppearanceIndexBuilder.Build(esm, bigEndian: false);

        Assert.True(index.Npcs.Count > 1000, $"Expected >1000 Oblivion NPCs; got {index.Npcs.Count}.");
        Assert.True(index.Races.Count > 5, $"Expected Oblivion races; got {index.Races.Count}.");
        Assert.Contains(index.Npcs.Values, npc => npc.FaceGenSymmetric is { Length: > 0 });
        var reynald = Assert.Contains(0x000222A8u, index.Npcs);
        Assert.NotNull(reynald.InventoryItems);
        Assert.Equal(7, reynald.InventoryItems!.Count);
        Assert.Contains(0x0001C830u, index.Armors);
        Assert.Contains(0x0001C884u, index.Armors);
        Assert.Contains(0x0001C883u, index.Armors);
    }

    [Fact]
    public void OblivionRaceLayout_MapsGenderedEarsAndSharedFacePartsWithoutIndexShift()
    {
        var esm = BuildOblivionRacePlugin();

        var index = NpcAppearanceIndexBuilder.Build(esm, bigEndian: false);
        var race = Assert.Contains(RaceFormId, index.Races);

        Assert.Equal("HeadHuman.nif", race.MaleHeadModelPath);
        Assert.Equal("HeadHuman.nif", race.FemaleHeadModelPath);
        Assert.Equal("EarsMale.nif", race.MaleEarModelPath);
        Assert.Equal("EarsFemale.nif", race.FemaleEarModelPath);
        Assert.Equal("MaleHead.dds", race.MaleEarTexturePath);
        Assert.Equal("FemaleHead.dds", race.FemaleEarTexturePath);
        Assert.Equal("MouthHuman.nif", race.MaleMouthModelPath);
        Assert.Equal("MouthHuman.nif", race.FemaleMouthModelPath);
        Assert.Equal("TeethLower.nif", race.MaleLowerTeethModelPath);
        Assert.Equal("TeethUpper.nif", race.MaleUpperTeethModelPath);
        Assert.Equal("TongueHuman.nif", race.MaleTongueModelPath);
        Assert.Equal("EyeLeft.nif", race.MaleEyeLeftModelPath);
        Assert.Equal("EyeRight.nif", race.MaleEyeRightModelPath);
        Assert.Equal(race.MaleEyeLeftModelPath, race.FemaleEyeLeftModelPath);
        Assert.Equal(race.MaleEyeRightModelPath, race.FemaleEyeRightModelPath);
        Assert.Equal("MaleBody.dds", race.MaleBodyTexturePath);
        Assert.Equal("FemaleBody.dds", race.FemaleBodyTexturePath);
        Assert.Equal(@"characters\_male\upperbody.nif", race.MaleUpperBodyPath);
        Assert.Equal(@"characters\_male\femaleupperbody.nif", race.FemaleUpperBodyPath);
        Assert.Equal(@"characters\_male\lowerbody.nif", race.MaleLowerBodyPath);
        Assert.Equal(@"characters\_male\femalehand.nif", race.FemaleHandPath);
        Assert.Equal(@"characters\_male\foot.nif", race.MaleFootPath);
        Assert.Equal("MaleLeg.dds", race.MaleLowerBodyTexturePath);
        Assert.Equal("FemaleHand.dds", race.FemaleHandTexturePath);
        Assert.Equal("MaleFoot.dds", race.MaleFootTexturePath);
        Assert.Equal(50, Assert.IsType<float[]>(race.MaleFaceGenSymmetric).Length);
        Assert.Equal(50, Assert.IsType<float[]>(race.FemaleFaceGenSymmetric).Length);
    }

    [Fact]
    public void OblivionClothingRecord_IsIndexedAsRenderableEquipment()
    {
        var esm = BuildOblivionClothingPlugin();

        var index = NpcAppearanceIndexBuilder.Build(esm, bigEndian: false);
        var clothing = Assert.Contains(0x0001C884u, index.Armors);

        Assert.Equal("MiddleShirt03", clothing.EditorId);
        Assert.Equal(0x0Cu, clothing.BipedFlags);
        Assert.Equal(@"Clothes\MiddleClass\03\M\Shirt.NIF", clothing.MaleBipedModelPath);
    }

    private static byte[] BuildPlugin(int headerSize, bool bigEndian)
    {
        var hedrData = new byte[12];
        WriteSingle(hedrData, 0, headerSize == 20 ? 1.0f : 1.34f, bigEndian);
        WriteUInt32(hedrData, 4, 1, bigEndian);
        WriteUInt32(hedrData, 8, 0x800, bigEndian);

        var tes4Payload = BuildSubrecord("HEDR", hedrData, bigEndian);
        var tes4 = BuildRecord("TES4", 0, tes4Payload, headerSize, bigEndian);

        var raceData = new byte[4];
        WriteUInt32(raceData, 0, RaceFormId, bigEndian);
        var faceGenData = new byte[8];
        WriteSingle(faceGenData, 0, 1.25f, bigEndian);
        WriteSingle(faceGenData, 4, -2.5f, bigEndian);

        var npcPayload = Concat(
            BuildSubrecord("EDID", Encoding.ASCII.GetBytes("FramingNpc\0"), bigEndian),
            BuildSubrecord("FULL", Encoding.ASCII.GetBytes("Framing NPC\0"), bigEndian),
            BuildSubrecord("RNAM", raceData, bigEndian),
            BuildSubrecord("FGGS", faceGenData, bigEndian));
        var npc = BuildRecord("NPC_", NpcFormId, npcPayload, headerSize, bigEndian);
        var group = BuildGroup("NPC_", npc, headerSize, bigEndian);

        return Concat(tes4, group);
    }

    private static byte[] BuildOblivionRacePlugin()
    {
        const int headerSize = 20;
        const bool bigEndian = false;
        var hedrData = new byte[12];
        WriteSingle(hedrData, 0, 1.0f, bigEndian);
        WriteUInt32(hedrData, 4, 1, bigEndian);
        WriteUInt32(hedrData, 8, 0x800, bigEndian);

        var tes4 = BuildRecord(
            "TES4",
            0,
            BuildSubrecord("HEDR", hedrData, bigEndian),
            headerSize,
            bigEndian);
        var faceCoefficients = new byte[200];
        WriteSingle(faceCoefficients, 0, 0.25f, bigEndian);

        var racePayload = Concat(
            BuildSubrecord("EDID", Encoding.ASCII.GetBytes("Imperial\0"), bigEndian),
            BuildSubrecord("NAM0", [], bigEndian),
            IndexedModel(0, "HeadHuman.nif"),
            BuildSubrecord("ICON", Encoding.ASCII.GetBytes("HeadHuman.dds\0"), bigEndian),
            IndexedModel(1, "EarsMale.nif"),
            BuildSubrecord("ICON", Encoding.ASCII.GetBytes("MaleHead.dds\0"), bigEndian),
            IndexedModel(2, "EarsFemale.nif"),
            BuildSubrecord("ICON", Encoding.ASCII.GetBytes("FemaleHead.dds\0"), bigEndian),
            IndexedModel(3, "MouthHuman.nif"),
            IndexedModel(4, "TeethLower.nif"),
            IndexedModel(5, "TeethUpper.nif"),
            IndexedModel(6, "TongueHuman.nif"),
            IndexedModel(7, "EyeLeft.nif"),
            IndexedModel(8, "EyeRight.nif"),
            BuildSubrecord("NAM1", [], bigEndian),
            BuildSubrecord("MNAM", [], bigEndian),
            IndexedIcon(0, "MaleBody.dds"),
            IndexedIcon(1, "MaleLeg.dds"),
            IndexedIcon(2, "MaleHand.dds"),
            IndexedIcon(3, "MaleFoot.dds"),
            BuildSubrecord("FNAM", [], bigEndian),
            IndexedIcon(0, "FemaleBody.dds"),
            IndexedIcon(1, "FemaleLeg.dds"),
            IndexedIcon(2, "FemaleHand.dds"),
            IndexedIcon(3, "FemaleFoot.dds"),
            BuildSubrecord("FGGS", faceCoefficients, bigEndian));
        var race = BuildRecord("RACE", RaceFormId, racePayload, headerSize, bigEndian);
        return Concat(tes4, BuildGroup("RACE", race, headerSize, bigEndian));

        byte[] IndexedModel(uint index, string path)
        {
            return Concat(
                UInt32Subrecord("INDX", index),
                BuildSubrecord("MODL", Encoding.ASCII.GetBytes(path + "\0"), bigEndian));
        }

        byte[] IndexedIcon(uint index, string path)
        {
            return Concat(
                UInt32Subrecord("INDX", index),
                BuildSubrecord("ICON", Encoding.ASCII.GetBytes(path + "\0"), bigEndian));
        }

        byte[] UInt32Subrecord(string signature, uint value)
        {
            var data = new byte[4];
            WriteUInt32(data, 0, value, bigEndian);
            return BuildSubrecord(signature, data, bigEndian);
        }
    }

    private static byte[] BuildOblivionClothingPlugin()
    {
        const int headerSize = 20;
        const bool bigEndian = false;
        var hedrData = new byte[12];
        WriteSingle(hedrData, 0, 1.0f, bigEndian);
        WriteUInt32(hedrData, 4, 1, bigEndian);
        WriteUInt32(hedrData, 8, 0x800, bigEndian);
        var tes4 = BuildRecord(
            "TES4",
            0,
            BuildSubrecord("HEDR", hedrData, bigEndian),
            headerSize,
            bigEndian);

        var bmdt = new byte[4];
        WriteUInt32(bmdt, 0, 0x0C, bigEndian);
        var clothingPayload = Concat(
            BuildSubrecord("EDID", Encoding.ASCII.GetBytes("MiddleShirt03\0"), bigEndian),
            BuildSubrecord("BMDT", bmdt, bigEndian),
            BuildSubrecord(
                "MODL",
                Encoding.ASCII.GetBytes(@"Clothes\MiddleClass\03\M\Shirt.NIF" + "\0"),
                bigEndian));
        var clothing = BuildRecord("CLOT", 0x0001C884, clothingPayload, headerSize, bigEndian);
        return Concat(tes4, BuildGroup("CLOT", clothing, headerSize, bigEndian));
    }

    private static byte[] BuildRecord(
        string signature,
        uint formId,
        byte[] payload,
        int headerSize,
        bool bigEndian)
    {
        var record = new byte[headerSize + payload.Length];
        WriteSignature(record, 0, signature, bigEndian);
        WriteUInt32(record, 4, (uint)payload.Length, bigEndian);
        WriteUInt32(record, 8, 0, bigEndian);
        WriteUInt32(record, 12, formId, bigEndian);
        WriteUInt32(record, 16, 0, bigEndian);
        if (headerSize == 24)
        {
            WriteUInt16(record, 20, 0, bigEndian);
            WriteUInt16(record, 22, 0, bigEndian);
        }

        payload.CopyTo(record, headerSize);
        return record;
    }

    private static byte[] BuildGroup(
        string label,
        byte[] payload,
        int headerSize,
        bool bigEndian)
    {
        var group = new byte[headerSize + payload.Length];
        WriteSignature(group, 0, "GRUP", bigEndian);
        WriteUInt32(group, 4, (uint)group.Length, bigEndian);
        WriteSignature(group, 8, label, bigEndian);
        WriteUInt32(group, 12, 0, bigEndian);
        WriteUInt32(group, 16, 0, bigEndian);
        if (headerSize == 24)
        {
            WriteUInt32(group, 20, 0, bigEndian);
        }

        payload.CopyTo(group, headerSize);
        return group;
    }

    private static byte[] BuildSubrecord(string signature, byte[] data, bool bigEndian)
    {
        var subrecord = new byte[6 + data.Length];
        WriteSignature(subrecord, 0, signature, bigEndian);
        WriteUInt16(subrecord, 4, checked((ushort)data.Length), bigEndian);
        data.CopyTo(subrecord, 6);
        return subrecord;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(part => part.Length)];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }

        return result;
    }

    private static void WriteSignature(byte[] target, int offset, string signature, bool bigEndian)
    {
        var bytes = Encoding.ASCII.GetBytes(signature);
        if (bigEndian)
        {
            Array.Reverse(bytes);
        }

        bytes.CopyTo(target, offset);
    }

    private static void WriteUInt16(byte[] target, int offset, ushort value, bool bigEndian)
    {
        if (bigEndian)
        {
            BinaryPrimitives.WriteUInt16BigEndian(target.AsSpan(offset, 2), value);
        }
        else
        {
            BinaryPrimitives.WriteUInt16LittleEndian(target.AsSpan(offset, 2), value);
        }
    }

    private static void WriteUInt32(byte[] target, int offset, uint value, bool bigEndian)
    {
        if (bigEndian)
        {
            BinaryPrimitives.WriteUInt32BigEndian(target.AsSpan(offset, 4), value);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(target.AsSpan(offset, 4), value);
        }
    }

    private static void WriteSingle(byte[] target, int offset, float value, bool bigEndian)
    {
        if (bigEndian)
        {
            BinaryPrimitives.WriteSingleBigEndian(target.AsSpan(offset, 4), value);
        }
        else
        {
            BinaryPrimitives.WriteSingleLittleEndian(target.AsSpan(offset, 4), value);
        }
    }
}
