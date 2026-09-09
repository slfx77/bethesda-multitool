using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Item;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Tests.Helpers;
using Xunit;
using static BethesdaMultitool.Tests.Helpers.EsmTestRecordBuilder;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing;

public sealed class ArmorWorldModelParsingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ParseArmor_SkyrimUsesWorldModelAndDoesNotDecodeArmatureFormIdAsPath(bool bigEndian)
    {
        const uint formId = 0x00012E4B;
        const uint armorAddonFormId = 0x00012E4A;
        const string worldModel = @"Armor\Iron\IronBootsGO.nif";

        var recordBytes = BuildRecordBytes(
            formId,
            "ARMO",
            bigEndian,
            ("EDID", NullTermString("ArmorIronBoots")),
            ("FULL", FormIdBytes(0x0000ABCD, bigEndian)),
            ("MOD2", NullTermString(worldModel)),
            ("MOD4", NullTermString(@"Armor\Iron\IronBootsFemaleGO.nif")),
            ("MODL", FormIdBytes(armorAddonFormId, bigEndian)));

        var armor = ParseSingleArmor(recordBytes, formId, bigEndian, BethesdaGame.Skyrim);

        Assert.Null(armor.ModelPath);
        Assert.Equal(worldModel, armor.WorldModelPath);

        var modelPaths = ObjectBoundsIndex.BuildModelPathIndex(new RecordCollection { Armor = [armor] });
        Assert.Equal(worldModel, Assert.Single(modelPaths).Value);
    }

    [Theory]
    [InlineData(BethesdaGame.Skyrim)]
    [InlineData(BethesdaGame.Fallout4)]
    [InlineData(BethesdaGame.Fallout76)]
    public void ParseArmor_ModernArmatureWithoutWorldModelIsNotIndexed(BethesdaGame game)
    {
        const uint formId = 0x0003452F;
        var recordBytes = BuildRecordBytes(
            formId,
            "ARMO",
            false,
            ("EDID", NullTermString("ArmorWithoutGroundModel")),
            ("MODL", FormIdBytes(0x0003452D, false)));

        var armor = ParseSingleArmor(recordBytes, formId, false, game);

        Assert.Null(armor.ModelPath);
        Assert.Null(armor.WorldModelPath);
        Assert.Empty(ObjectBoundsIndex.BuildModelPathIndex(new RecordCollection { Armor = [armor] }));
    }

    [Fact]
    public void ParseArmor_LegacyModlRemainsFallbackWhenNoWorldModelExists()
    {
        const uint formId = 0x00001000;
        const string bipedModel = @"Armor\Legacy\ArmorMale.nif";
        var recordBytes = BuildRecordBytes(
            formId,
            "ARMO",
            false,
            ("EDID", NullTermString("LegacyArmor")),
            ("MODL", NullTermString(bipedModel)));

        var armor = ParseSingleArmor(recordBytes, formId, false, BethesdaGame.FalloutNewVegas);

        Assert.Equal(bipedModel, armor.ModelPath);
        Assert.Null(armor.WorldModelPath);
        Assert.Equal(
            bipedModel,
            Assert.Single(ObjectBoundsIndex.BuildModelPathIndex(new RecordCollection { Armor = [armor] })).Value);
    }

    private static ArmorRecord ParseSingleArmor(
        byte[] recordBytes,
        uint formId,
        bool bigEndian,
        BethesdaGame game)
    {
        var record = new DetectedMainRecord(
            "ARMO", (uint)(recordBytes.Length - 24), 0, formId, 0, bigEndian);
        var scanResult = MakeScanResult([record]);
        scanResult.Game = game;

        using var mapping = MemoryMappedFile.CreateNew(null, recordBytes.Length);
        using var accessor = mapping.CreateViewAccessor(0, recordBytes.Length);
        accessor.WriteArray(0, recordBytes, 0, recordBytes.Length);

        return Assert.Single(
            new RecordParser(scanResult, accessor: accessor, fileSize: recordBytes.Length).ParseArmor());
    }

    private static byte[] FormIdBytes(uint value, bool bigEndian)
    {
        var bytes = new byte[sizeof(uint)];
        if (bigEndian)
        {
            BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        }

        return bytes;
    }
}

[Trait("Category", TestCategories.BucketB)]
[Collection(SequentialIntegrationGroup.Name)]
public sealed class ArmorWorldModelRetailTests
{
    [Fact]
    public async Task Skyrim_DragonsreachPlacedBootsResolveTheirAuthoredGroundModels()
    {
        var esm = RealAssetPaths.Masters.Skyrim();
        BucketBTestGuard.SkipUnlessEnabled();
        Assert.SkipUnless(esm is not null, RealAssetPaths.SkipMessage("Skyrim.esm"));

        var result = await RealAssetEsmCache.LoadAsync(
            esm, TestContext.Current.CancellationToken);

        Assert.Equal(
            @"Armor\Iron\Male\BootsGND.nif",
            result.Records.ModelPathIndex[0x00012E4B]);
        Assert.Equal(
            @"Clothes\FarmClothes04\bootsGO.nif",
            result.Records.ModelPathIndex[0x0003452F]);
    }
}