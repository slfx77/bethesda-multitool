using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Records;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Terrain;
using BethesdaMultitool.Core.Minidump;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Runtime;

public class RuntimeWorldReaderLandVisualTests
{
    private const uint BaseVa = 0x40000000;
    private const uint LandVa = BaseVa + 0x0100;
    private const uint LoadedLandVa = BaseVa + 0x0200;
    private const uint TextureArrayVa = BaseVa + 0x0300;
    private const uint PercentArrayVa = BaseVa + 0x8000;
    private const uint PercentWeightsVa = BaseVa + 0x9000;
    private const uint BadPercentArrayVa = BaseVa + 0xB000;
    private const uint BadPercentWeightsVa = BaseVa + 0xC000;
    private const uint BaseTextureVa = BaseVa + 0x1000;
    private const uint AlphaTextureVa = BaseVa + 0x1100;
    private const uint SecondAlphaTextureVa = BaseVa + 0x1180;
    private const uint InvalidTextureVa = BaseVa + 0x1200;
    private const uint TextureSetVa = BaseVa + 0x1300;
    private const uint GrassVa = BaseVa + 0x1400;
    private const uint StringVa = BaseVa + 0x1500;
    private const uint DiffusePathVa = BaseVa + 0x4000;
    private const uint NormalPathVa = BaseVa + 0x4100;
    private const uint AlternateDiffusePathVa = BaseVa + 0x4200;
    private const uint AlternateNormalPathVa = BaseVa + 0x4300;
    private const uint WrongInlinePathVa = BaseVa + 0x4400;
    private const uint NiDiffuseTextureVa = BaseVa + 0x5000;
    private const uint NiNormalTextureVa = BaseVa + 0x5100;
    private const uint TerrainPointerArrayVa = BaseVa + 0x6000;
    private const uint TerrainQuadrantsVa = BaseVa + 0x15000;

    [Fact]
    public void ReadRuntimeLandData_CarriesLoadedBaseHeightWithTerrainMesh()
    {
        var buffer = new byte[0x20000];
        WriteLand(buffer, 0x00099999);
        WriteLoadedLand(buffer, 0, 0, 0, 824f);
        WriteTerrainQuadrants(buffer);

        var land = ReadLand(buffer, 0x00099999);

        Assert.NotNull(land);
        Assert.Equal(824f, land.BaseHeight);
        Assert.NotNull(land.TerrainMesh);
        Assert.Equal(824f, land.TerrainMesh.RuntimeBaseHeight);
    }

    [Fact]
    public void ReadRuntimeLandData_InvalidBaseHeightLeavesTerrainProvenanceMissing()
    {
        var buffer = new byte[0x20000];
        WriteLand(buffer, 0x00099998);
        WriteLoadedLand(buffer, 0, 0, 0, float.NaN);
        WriteTerrainQuadrants(buffer);

        var land = ReadLand(buffer, 0x00099998);

        Assert.NotNull(land);
        Assert.Equal(0f, land.BaseHeight);
        Assert.NotNull(land.TerrainMesh);
        Assert.Null(land.TerrainMesh.RuntimeBaseHeight);
    }

    [Fact]
    public void ReadRuntimeLandData_ReconstructsTextureLayersFromRuntimeLoadedLandData()
    {
        var buffer = new byte[0x20000];
        WriteLand(buffer, 0x000AAAAA);
        WriteLoadedLand(buffer, BaseTextureVa, TextureArrayVa,
            PercentArrayVa);
        WriteUInt32BE(buffer, Offset(TextureArrayVa), AlphaTextureVa);
        WriteUInt32BE(buffer, Offset(TextureArrayVa) + 4, SecondAlphaTextureVa);
        // Slots 0..4 are the only alpha slots. Keep the intermediate slots
        // nonzero so a reader with the historical 64-pointer overrun reaches
        // the valid-looking canary in slot 5.
        WriteUInt32BE(buffer, Offset(TextureArrayVa) + 8, InvalidTextureVa);
        WriteUInt32BE(buffer, Offset(TextureArrayVa) + 12, InvalidTextureVa);
        WriteUInt32BE(buffer, Offset(TextureArrayVa) + 16, InvalidTextureVa);
        WriteUInt32BE(buffer, Offset(TextureArrayVa) + 20, AlphaTextureVa);
        WriteRuntimePercentWeights(
            buffer,
            PercentArrayVa,
            PercentWeightsVa,
            (18, 1, 0.75f),
            (19, 2, 0.25f),
            (288, 1, 1.0f));

        WriteLandTexture(buffer, BaseTextureVa, 0x00111111, "RuntimeBaseTexture");
        WriteLandTexture(buffer, AlphaTextureVa, 0x00222222, "RuntimeAlphaTexture");
        WriteLandTexture(buffer, SecondAlphaTextureVa, 0x00222223, "RuntimeSecondAlphaTexture");
        WriteFormHeader(buffer, InvalidTextureVa, 0x3A, 0x00333333);
        WriteTextureSet(buffer);
        WriteFormHeader(buffer, GrassVa, 0x24, 0x00555555);

        var land = ReadLand(buffer, 0x000AAAAA);

        Assert.NotNull(land);
        Assert.NotNull(land.VisualData);
        Assert.Equal(VisualDataSource.Runtime, land.VisualData.Source);

        var layers = land.VisualData.TextureLayers;
        Assert.Equal(3, layers.Count);

        var baseLayer = layers[0];
        Assert.Equal(LandTextureLayerKind.Base, baseLayer.Kind);
        Assert.Equal(0x00111111u, baseLayer.TextureFormId);
        Assert.Equal(0, baseLayer.Quadrant);

        var alphaLayer = layers[1];
        Assert.Equal(LandTextureLayerKind.Alpha, alphaLayer.Kind);
        Assert.Equal(0x00222222u, alphaLayer.TextureFormId);
        Assert.Equal(0, alphaLayer.Quadrant);
        Assert.Equal((ushort)0, alphaLayer.Layer);
        Assert.Equal([18, 288], alphaLayer.BlendEntries.Select(e => (int)e.Position).ToArray());
        Assert.Equal(0.75f, alphaLayer.BlendEntries[0].Opacity);
        Assert.Equal(1.0f, alphaLayer.BlendEntries[1].Opacity);

        var secondAlphaLayer = layers[2];
        Assert.Equal(LandTextureLayerKind.Alpha, secondAlphaLayer.Kind);
        Assert.Equal(0x00222223u, secondAlphaLayer.TextureFormId);
        Assert.Equal((ushort)1, secondAlphaLayer.Layer);
        var secondBlend = Assert.Single(secondAlphaLayer.BlendEntries);
        Assert.Equal((ushort)19, secondBlend.Position);
        Assert.Equal(0.25f, secondBlend.Opacity);

        Assert.DoesNotContain(layers, l => l.TextureFormId == 0x00333333);

        var runtimeTextures = land.RuntimeLandTextures.OrderBy(t => t.FormId).ToArray();
        Assert.Equal([0x00111111u, 0x00222222u, 0x00222223u], runtimeTextures.Select(t => t.FormId).ToArray());
        Assert.All(runtimeTextures, t =>
        {
            Assert.Equal(0x00444444u, t.TextureSetFormId);
            Assert.Equal(new byte[] { 1, 2, 3 }, t.HavokData);
            Assert.Equal(new byte[] { 4 }, t.SpecularData);
            Assert.Equal([0x00555555u], t.GrassFormIds);
        });

        var runtimeTextureSet = Assert.Single(land.RuntimeTextureSets);
        Assert.Equal(0x00444444u, runtimeTextureSet.FormId);
        Assert.Equal("RuntimeTerrainTextureSet", runtimeTextureSet.EditorId);
        Assert.Equal("textures\\landscape\\runtime_diffuse.dds", runtimeTextureSet.DiffuseTexture);
        Assert.Equal("Textures\\Landscape\\RuntimeNormal.dds", runtimeTextureSet.NormalTexture);
        Assert.Equal((ushort)0x1234, runtimeTextureSet.Flags);

        var textureDiag = Assert.Single(land.Diagnostics!.QuadTextureArrays, d => d.Pointer.IsMapped);
        Assert.Equal(5, textureDiag.SampledPointerCount);
        Assert.Equal(2, textureDiag.ResolvedTextureCount);
        Assert.Equal([0x00222222u, 0x00222223u], textureDiag.TextureFormIds);

        var percentDiag = Assert.Single(land.Diagnostics.PercentArrays, d => d.Pointer.IsMapped);
        Assert.Equal(17 * 17 * 6, percentDiag.SampledCount);
        Assert.Equal(17 * 17 * 6, percentDiag.NormalFloatCount);
        Assert.Equal(17 * 17 * 6, percentDiag.UnitRangeCount);
    }

    [Fact]
    public void ParseAll_MergesTextureSetsRecoveredFromRuntimeLandTexturePointers()
    {
        var runtimeTextureSet = new TextureSetRecord
        {
            FormId = 0x00ABCDEF,
            EditorId = "RuntimeTerrainTextureSet",
            DiffuseTexture = "textures\\landscape\\runtime_diffuse.dds",
            NormalTexture = "textures\\landscape\\runtime_normal.dds",
            Flags = 0x1234,
            IsBigEndian = true
        };
        var scanResult = new EsmRecordScanResult
        {
            LandRecords =
            [
                new ExtractedLandRecord
                {
                    Header = new DetectedMainRecord("LAND", 0, 0, 0x00123456, 0, true),
                    RuntimeTextureSets = [runtimeTextureSet]
                }
            ]
        };

        var records = new RecordParser(scanResult).ParseAll();

        var parsedTextureSet = Assert.Single(records.TextureSets);
        Assert.Equal(0x00ABCDEFu, parsedTextureSet.FormId);
        Assert.Equal("RuntimeTerrainTextureSet", parsedTextureSet.EditorId);
        Assert.Equal("textures\\landscape\\runtime_diffuse.dds", parsedTextureSet.DiffuseTexture);
    }

    [Fact]
    public void ReadRuntimeLandData_RecoversTextureSetPathsFromNiSourcePointerArray()
    {
        var buffer = new byte[0x20000];
        WriteLand(buffer, 0x000CCCCC);
        WriteLoadedLand(buffer, BaseTextureVa, 0, 0);
        WriteLandTexture(buffer, BaseTextureVa, 0x00111111, "RuntimeBaseTexture");
        WriteTextureSetWithNiSourcePointerArray(buffer);

        var land = ReadLand(buffer, 0x000CCCCC);

        var textureSet = Assert.Single(land!.RuntimeTextureSets);
        Assert.Equal("textures\\landscape\\direct_diffuse.dds", textureSet.DiffuseTexture);
        Assert.Equal("textures\\landscape\\direct_diffuse_n.dds", textureSet.NormalTexture);
    }

    [Fact]
    public void ReadRuntimeLandData_SelectsHighestScoringTextureSetPathLayout()
    {
        var buffer = new byte[0x20000];
        WriteLand(buffer, 0x000DDDDD);
        WriteLoadedLand(buffer, BaseTextureVa, 0, 0);
        WriteLandTexture(buffer, BaseTextureVa, 0x00111111, "RuntimeBaseTexture");
        WriteTextureSetWithFileEntriesAndInlineNoise(buffer);

        var land = ReadLand(buffer, 0x000DDDDD);

        var textureSet = Assert.Single(land!.RuntimeTextureSets);
        Assert.Equal("textures\\landscape\\scored_diffuse.dds", textureSet.DiffuseTexture);
        Assert.Equal("textures\\landscape\\scored_diffuse_n.dds", textureSet.NormalTexture);
    }

    [Fact]
    public void ReadRuntimeLandData_SkipsAlphaLayerWhenPercentMaskIsInvalid()
    {
        var buffer = new byte[0x20000];
        WriteLand(buffer, 0x000BBBBB);
        WriteLoadedLand(buffer, BaseTextureVa, TextureArrayVa,
            BadPercentArrayVa);
        WriteUInt32BE(buffer, Offset(TextureArrayVa), AlphaTextureVa);
        WriteRuntimePercentWeights(
            buffer,
            BadPercentArrayVa,
            BadPercentWeightsVa,
            (0, 1, float.NaN));

        WriteLandTexture(buffer, BaseTextureVa, 0x00111111, "RuntimeBaseTexture");
        WriteLandTexture(buffer, AlphaTextureVa, 0x00222222, "RuntimeAlphaTexture");
        WriteTextureSet(buffer);
        WriteFormHeader(buffer, GrassVa, 0x24, 0x00555555);

        var land = ReadLand(buffer, 0x000BBBBB);

        Assert.NotNull(land);
        Assert.NotNull(land.VisualData);
        Assert.Single(land.VisualData.TextureLayers);
        Assert.Equal(LandTextureLayerKind.Base, land.VisualData.TextureLayers[0].Kind);
    }

    private static RuntimeLoadedLandData? ReadLand(byte[] buffer, uint formId)
    {
        var accessor = new SparseMemoryAccessor();
        accessor.AddRange(0, buffer);
        var minidumpInfo = new MinidumpInfo
        {
            IsValid = true,
            ProcessorArchitecture = 0x03,
            MemoryRegions =
            [
                new MinidumpMemoryRegion
                {
                    VirtualAddress = BaseVa,
                    FileOffset = 0,
                    Size = buffer.Length
                }
            ]
        };
        var context = new RuntimeMemoryContext(accessor, buffer.Length, minidumpInfo);
        var reader = new RuntimeWorldReader(context);

        return reader.ReadRuntimeLandData(new RuntimeEditorIdEntry
        {
            EditorId = "RuntimeLand",
            FormId = formId,
            FormType = 0x44,
            TesFormOffset = Offset(LandVa)
        });
    }

    private static void WriteLand(byte[] buffer, uint formId)
    {
        WriteFormHeader(buffer, LandVa, 0x44, formId);
        WriteUInt32BE(buffer, Offset(LandVa) + 56, LoadedLandVa);
    }

    private static void WriteLoadedLand(
        byte[] buffer,
        uint baseTextureVa,
        uint textureArrayVa,
        uint percentArrayVa,
        float baseHeight = 0f)
    {
        var offset = Offset(LoadedLandVa);
        WriteUInt32BE(buffer, offset + 32, baseTextureVa);
        WriteUInt32BE(buffer, offset + 48, textureArrayVa);
        WriteUInt32BE(buffer, offset + 64, percentArrayVa);
        WriteInt32BE(buffer, offset + 152, 1);
        WriteInt32BE(buffer, offset + 156, -2);
        WriteSingleBE(buffer, offset + 160, baseHeight);
    }

    private static void WriteTerrainQuadrants(byte[] buffer)
    {
        WriteUInt32BE(buffer, Offset(LoadedLandVa) + 4, TerrainPointerArrayVa);
        for (var quadrant = 0; quadrant < 4; quadrant++)
        {
            const int quadrantSize = 17;
            const int quadrantBytes = quadrantSize * quadrantSize * 3 * sizeof(float);
            var quadrantVa = TerrainQuadrantsVa + (uint)(quadrant * quadrantBytes);
            WriteUInt32BE(buffer, Offset(TerrainPointerArrayVa) + quadrant * 4, quadrantVa);

            var minX = (quadrant & 1) == 0 ? -2048f : 0f;
            var minY = (quadrant & 2) == 0 ? -2048f : 0f;
            for (var y = 0; y < quadrantSize; y++)
            {
                for (var x = 0; x < quadrantSize; x++)
                {
                    var localX = minX + x * 128f;
                    var localY = minY + y * 128f;
                    var offset = Offset(quadrantVa) + (y * quadrantSize + x) * 3 * sizeof(float);
                    WriteSingleBE(buffer, offset, localX);
                    WriteSingleBE(buffer, offset + 4, localY);
                    WriteSingleBE(buffer, offset + 8, localX * 0.125f + localY * 0.25f);
                }
            }
        }
    }

    private static void WriteLandTexture(byte[] buffer, uint va, uint formId, string editorId)
    {
        WriteFormHeader(buffer, va, 0x12, formId);
        WriteBsString(buffer, va + 16, StringVa + (formId & 0xFF) * 0x40, editorId);
        WriteUInt32BE(buffer, Offset(va) + 40, TextureSetVa);
        buffer[Offset(va) + 44] = 1;
        buffer[Offset(va) + 45] = 2;
        buffer[Offset(va) + 46] = 3;
        buffer[Offset(va) + 47] = 4;
        WriteUInt32BE(buffer, Offset(va) + 48, GrassVa);
    }

    private static void WriteTextureSet(byte[] buffer)
    {
        WriteFormHeader(buffer, TextureSetVa, 0x04, 0x00444444);
        WriteBsString(buffer, TextureSetVa + 16, StringVa + 0x1200, "RuntimeTerrainTextureSet");
        WriteInt16BE(buffer, Offset(TextureSetVa) + 52, -1);
        WriteInt16BE(buffer, Offset(TextureSetVa) + 54, -2);
        WriteInt16BE(buffer, Offset(TextureSetVa) + 56, -3);
        WriteInt16BE(buffer, Offset(TextureSetVa) + 58, 1);
        WriteInt16BE(buffer, Offset(TextureSetVa) + 60, 2);
        WriteInt16BE(buffer, Offset(TextureSetVa) + 62, 3);
        WriteTextureInlineEntry(buffer, 0, DiffusePathVa, "textures/landscape/runtime_diffuse.dds");
        WriteTextureInlineEntry(buffer, 1, NormalPathVa, "Data\\Textures\\Landscape\\RuntimeNormal.dds");
        WriteUInt16BE(buffer, Offset(TextureSetVa) + 160, 0x1234);
    }

    private static void WriteTextureSetWithNiSourcePointerArray(byte[] buffer)
    {
        WriteFormHeader(buffer, TextureSetVa, 0x04, 0x00444444);
        WriteBsString(buffer, TextureSetVa + 16, StringVa + 0x1200, "RuntimeTerrainTextureSet");
        WriteUInt32BE(buffer, Offset(TextureSetVa) + 72, NiDiffuseTextureVa);
        WriteUInt32BE(buffer, Offset(TextureSetVa) + 76, NiNormalTextureVa);
        WriteNiSourceTexture(buffer, NiDiffuseTextureVa, AlternateDiffusePathVa,
            "textures\\landscape\\direct_diffuse.dds");
        WriteNiSourceTexture(buffer, NiNormalTextureVa, AlternateNormalPathVa,
            "textures\\landscape\\direct_diffuse_n.dds");
    }

    private static void WriteTextureSetWithFileEntriesAndInlineNoise(byte[] buffer)
    {
        WriteFormHeader(buffer, TextureSetVa, 0x04, 0x00444444);
        WriteBsString(buffer, TextureSetVa + 16, StringVa + 0x1200, "RuntimeTerrainTextureSet");
        WriteUInt32BE(buffer, Offset(TextureSetVa) + 76, WrongInlinePathVa);
        WriteAsciiNullTerminated(buffer, WrongInlinePathVa, "textures\\clutter\\wrong.dds");
        WriteUInt32BE(buffer, Offset(TextureSetVa) + 164, AlternateDiffusePathVa);
        WriteUInt32BE(buffer, Offset(TextureSetVa) + 168, AlternateNormalPathVa);
        WriteAsciiNullTerminated(buffer, AlternateDiffusePathVa, "textures\\landscape\\scored_diffuse.dds");
        WriteAsciiNullTerminated(buffer, AlternateNormalPathVa, "textures\\landscape\\scored_diffuse_n.dds");
    }

    private static void WriteNiSourceTexture(byte[] buffer, uint va, uint pathVa, string path)
    {
        WriteUInt32BE(buffer, Offset(va) + 4, 1);
        WriteUInt32BE(buffer, Offset(va) + 48, pathVa);
        WriteAsciiNullTerminated(buffer, pathVa, path);
    }

    private static void WriteTextureInlineEntry(byte[] buffer, int slot, uint pathVa, string path)
    {
        var offset = Offset(TextureSetVa) + 72 + slot * 12;
        WriteUInt32BE(buffer, offset, 0x82015E40);
        WriteUInt32BE(buffer, offset + 4, pathVa);
        WriteUInt32BE(buffer, offset + 8, 0x001D001D);
        WriteAsciiNullTerminated(buffer, pathVa, path);
    }

    private static void WriteAsciiNullTerminated(byte[] buffer, uint va, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        bytes.CopyTo(buffer.AsSpan(Offset(va), bytes.Length));
        buffer[Offset(va) + bytes.Length] = 0;
    }

    private static void WriteRuntimePercentWeights(
        byte[] buffer,
        uint pointerArrayVa,
        uint weightVectorsVa,
        params (int Position, int Slot, float Opacity)[] values)
    {
        const int activeSlotCount = 6;
        const int allocatedSlotCount = 8;
        for (var position = 0; position < 17 * 17; position++)
        {
            var vectorVa = weightVectorsVa + (uint)(position * allocatedSlotCount * 4);
            WriteUInt32BE(buffer, Offset(pointerArrayVa) + position * 4, vectorVa);
            for (var slot = 0; slot < activeSlotCount; slot++)
            {
                WriteSingleBE(buffer, Offset(vectorVa) + slot * 4, slot == 0 ? 1f : 0f);
            }
        }

        foreach (var (position, slot, opacity) in values)
        {
            var vectorVa = weightVectorsVa + (uint)(position * allocatedSlotCount * 4);
            WriteSingleBE(buffer, Offset(vectorVa) + slot * 4, opacity);
        }
    }

    private static void WriteFormHeader(byte[] buffer, uint va, byte formType, uint formId)
    {
        var offset = Offset(va);
        buffer[offset + 4] = formType;
        WriteUInt32BE(buffer, offset + 12, formId);
    }

    private static void WriteBsString(byte[] buffer, uint fieldVa, uint stringVa, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        WriteUInt32BE(buffer, Offset(fieldVa), stringVa);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(Offset(fieldVa) + 4, 2), (ushort)bytes.Length);
        bytes.CopyTo(buffer.AsSpan(Offset(stringVa), bytes.Length));
    }

    private static void WriteUInt32BE(byte[] buffer, int offset, uint value)
    {
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(offset, 4), value);
    }

    private static void WriteInt32BE(byte[] buffer, int offset, int value)
    {
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(offset, 4), value);
    }

    private static void WriteInt16BE(byte[] buffer, int offset, short value)
    {
        BinaryPrimitives.WriteInt16BigEndian(buffer.AsSpan(offset, 2), value);
    }

    private static void WriteUInt16BE(byte[] buffer, int offset, ushort value)
    {
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset, 2), value);
    }

    private static void WriteSingleBE(byte[] buffer, int offset, float value)
    {
        BinaryPrimitives.WriteSingleBigEndian(buffer.AsSpan(offset, 4), value);
    }

    private static int Offset(uint va)
    {
        return checked((int)(va - BaseVa));
    }
}