using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using BethesdaMultitool.CLI.Formatters;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Tests.Helpers;
using Xunit;
using static BethesdaMultitool.Tests.Helpers.EsmTestRecordBuilder;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing;

public sealed class IngredientParserTests
{
    private const string RetailEditorId = "DoNotCreateNewIngredientsWeArentUsingThemInFallout";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ParseAll_IngredientReadsFourByteWeightData(bool bigEndian)
    {
        const uint formId = 0x0003135B;
        var recordBytes = BuildRecordBytes(
            formId,
            "INGR",
            bigEndian,
            ("EDID", NullTermString("TestIngredient")),
            ("ETYP", UInt32Bytes(3, bigEndian)),
            ("DATA", FloatBytes(1.25f, bigEndian)),
            ("ENIT", new byte[8]),
            ("EFID", UInt32Bytes(0x00001234, bigEndian)),
            ("EFIT", new byte[20]));

        var mainRecord = new DetectedMainRecord(
            "INGR", (uint)(recordBytes.Length - 24), 0, formId, 0, bigEndian);
        var scanResult = MakeScanResult([mainRecord]);

        using var mmf = MemoryMappedFile.CreateNew(null, recordBytes.Length);
        using var accessor = mmf.CreateViewAccessor(0, recordBytes.Length);
        accessor.WriteArray(0, recordBytes, 0, recordBytes.Length);

        var parsed = new RecordParser(scanResult, accessor: accessor, fileSize: recordBytes.Length)
            .ParseAll();
        var ingredient = Assert.Single(parsed.Ingredients);

        Assert.Equal(formId, ingredient.FormId);
        Assert.Equal("TestIngredient", ingredient.EditorId);
        Assert.Equal(3u, ingredient.EquipType);
        Assert.Equal(1.25f, ingredient.Weight);

        var flat = Assert.Single(RecordFlattener.Flatten(parsed), record => record.Type == "INGR");
        Assert.Equal(formId, flat.FormId);
        Assert.Equal("TestIngredient", flat.EditorId);
    }

    private static byte[] UInt32Bytes(uint value, bool bigEndian)
    {
        var bytes = new byte[4];
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

    private static byte[] FloatBytes(float value, bool bigEndian)
    {
        return UInt32Bytes(BitConverter.SingleToUInt32Bits(value), bigEndian);
    }

    [Fact]
    public async Task RetailFalloutNv_HasOneExplicitlyNonCreatableIngredient()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var esm = ResolveFalloutNvEsm();
        Assert.SkipUnless(esm is not null,
            "FalloutNV.esm not found (set BETHESDA_TEST_DATA_ROOT or install Fallout: New Vegas).");

        var result = await RealAssetEsmCache.LoadAsync(
            esm!, TestContext.Current.CancellationToken);
        var ingredient = Assert.Single(result.Records.Ingredients);

        Assert.Equal(0x0003135Bu, ingredient.FormId);
        Assert.Equal(RetailEditorId, ingredient.EditorId);
        Assert.True(float.IsFinite(ingredient.Weight));
    }

    private static string? ResolveFalloutNvEsm()
    {
        var root = Environment.GetEnvironmentVariable("BETHESDA_TEST_DATA_ROOT");
        if (!string.IsNullOrEmpty(root) && File.Exists(Path.Combine(root, "FalloutNV.esm")))
        {
            return Path.Combine(root, "FalloutNV.esm");
        }

        string?[] candidates =
        [
            @"Sample\ESM\pc_final\FalloutNV.esm",
            RealAssetPaths.SteamGameFile("Fallout New Vegas", @"Data\FalloutNV.esm")
        ];
        return candidates.FirstOrDefault(File.Exists);
    }
}